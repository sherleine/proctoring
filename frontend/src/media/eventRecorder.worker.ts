/**
 * Event-based recorder (Web Worker).
 *
 * Inputs: VideoFrames and AudioData from MediaStreamTrackProcessor (camera + mic).
 * Device timestamps use different clocks per stream and device, so StreamClock
 * puts both onto one timeline (worker clock, microseconds) at arrival.
 *
 * Continuously:
 *   video  -> VideoEncoder (VP8, keyframe every second) -> rolling packet buffer
 *   audio  -> AudioEncoder (Opus)                       -> rolling packet buffer
 *          -> mono PCM (native rate)                    -> rolling PCM buffer
 *          -> 16 kHz -> Silero VAD -> ClipRecorder (event timing)
 * Only a few seconds are kept while nothing happens. When ClipRecorder reports a
 * finished event, the clip [event start - pre-buffer, speech end + pad) is cut from
 * the buffers - starting at the preceding keyframe - and written as WebM, plus a
 * WAV from the same PCM over exactly the same time range.
 */
import * as ort from "onnxruntime-web/wasm";
import { ClipRecorder, type FinalizedClip, type RecorderEvent } from "../audio/clipRecorder";
import { NoiseFloorEstimator } from "../audio/noiseFloor";
import { StreamingResampler } from "../audio/resampler";
import { SileroVad, SILERO_FRAME_SAMPLES } from "../audio/vadEngine";
import { rmsDb } from "../utils/audioMath";
import { wavBlobFromInt16 } from "../utils/wav";
import { muxWebm } from "./clipMuxer";
import type { EventClipMeta, EventWorkerConfig, EventWorkerInbound, EventWorkerOutbound } from "./eventMessages";
import { extractSamples, floatToInt16, prunePcm, pruneBefore, pruneVideo, sampleIndexAt, selectVideo, StreamClock, type PcmBlock, type TimedPacket } from "./mediaRing";

const VAD_RATE = 16000;
const NOISE_UPDATE_MAX_PROB = 0.15;
// Extra history beyond pre-buffer + keyframe interval: covers VAD lag before an event is known.
const RETENTION_MARGIN_US = 2_500_000;
// Frames held by the encoder come from the camera's small buffer pool; holding many can starve
// the camera (Chrome then ends the track). Drop frames instead of queueing.
const MAX_ENCODE_QUEUE = 2;
const DATA_WAIT_TIMEOUT_MS = 3000;

const nowUs = () => (performance.timeOrigin + performance.now()) * 1000;
// Frames that queued up before reading started arrive in a burst and carry no usable
// arrival time; they are discarded. Monitoring is reported as started only afterwards.
const BACKLOG_DISCARD_US = 300_000;
let readStartUs = 0;
const inBacklog = () => nowUs() - readStartUs < BACKLOG_DISCARD_US;
const videoClock = new StreamClock();
const audioClock = new StreamClock();
/** Encoder outputs keep the raw input timestamp; look up the timeline position assigned at input. */
const videoTimeline = new Map<number, number>();

const post = (msg: EventWorkerOutbound, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(msg, transfer);

let cfg: EventWorkerConfig;
let vad: SileroVad;
let recorder: ClipRecorder;
let noise: NoiseFloorEstimator;
let resampler: StreamingResampler | null = null;

let videoEncoder: VideoEncoder | null = null;
let audioEncoder: AudioEncoder | null = null;
let videoMeta: EncodedVideoChunkMetadata | null = null;
let audioMeta: EncodedAudioChunkMetadata | null = null;
let videoSize = { width: 0, height: 0 };
let videoPackets: TimedPacket[] = [];
let audioPackets: TimedPacket[] = [];
let pcm: PcmBlock[] = [];
let latestVideoUs = -Infinity;
let latestAudioUs = -Infinity;
let lastKeyUs = -Infinity;

let audioRate = 0;
let audioBaseUs: number | null = null; // timestamp of 16 kHz sample position 0
let inputSamples = 0; // native-rate samples consumed by the VAD path
let frame16 = new Float32Array(SILERO_FRAME_SAMPLES);
let fill16 = 0;

// Opus packets are stamped from their sample position on the capture timeline
// (the encoder itself counts samples and would hide dropped input as a time shift).
let encInputs: { startSample: number; ts: number }[] = [];
let encInputSamples = 0;
let encOutputSamples = 0;

// VAD runs from its own queue so the audio read loop never waits on inference.
const vadQueue: { mono: Float32Array; ts: number }[] = [];
let vadRunning = false;
let vadReady: Promise<void> = Promise.resolve();

let openEventHoldUs: number | null = null;
const pendingHolds = new Map<string, number>();
const speechStartUs = new Map<string, number>();
let assembleChain: Promise<void> = Promise.resolve();

let videoReader: ReadableStreamDefaultReader<VideoFrame> | null = null;
let audioReader: ReadableStreamDefaultReader<AudioData> | null = null;
let loops: Promise<void>[] = [];
let stopping = false;
let stoppingVad = false;
let flushed = false;
let firstVideo: { ts: number; at: number } | null = null;
let firstAudio: { ts: number; at: number } | null = null;
let readyPosted = false;
let vadLoaded = false;

self.onmessage = async (e: MessageEvent<EventWorkerInbound>) => {
  const msg = e.data;
  if (msg.type === "init") {
    try {
      cfg = msg.config;
      ort.env.wasm.wasmPaths = cfg.ortBaseUrl;
      ort.env.wasm.numThreads = 1;
      recorder = new ClipRecorder(cfg.recorder);
      noise = new NoiseFloorEstimator(cfg.noiseAlpha);
      // Load the model first: compiling it blocks this thread for a moment. Frames that
      // queue up meanwhile are discarded (see BACKLOG_DISCARD_US).
      vad = await SileroVad.create(ort, cfg.modelUrl);
      vadLoaded = true;
      videoReader = msg.video.getReader();
      audioReader = msg.audio.getReader();
      readStartUs = nowUs();
      loops = [videoLoop(), audioLoop()];
    } catch (err) {
      post({ type: "error", stage: "init", message: (err as Error).message, fatal: true });
    }
  } else if (msg.type === "stop") {
    await stop();
  }
};

// ------------------------------------------------------------------ video

async function videoLoop(): Promise<void> {
  const reader = videoReader!;
  for (;;) {
    let frame: VideoFrame | undefined;
    try {
      const r = await reader.read();
      if (r.done) break;
      frame = r.value;
      if (inBacklog()) continue; // frame closed in finally
      videoTimeline.set(frame.timestamp, videoClock.map(frame.timestamp, nowUs()));
      if (!firstVideo) firstVideo = { ts: frame.timestamp, at: performance.now() };
      if (!videoEncoder) await setupVideoEncoder(frame);
      if (videoEncoder!.encodeQueueSize > MAX_ENCODE_QUEUE) {
        videoTimeline.delete(frame.timestamp);
        continue; // encoder overloaded: drop this frame
      }
      const key = frame.timestamp - lastKeyUs >= cfg.video.keyframeIntervalUs;
      if (key) lastKeyUs = frame.timestamp;
      videoEncoder!.encode(frame, { keyFrame: key });
    } catch (err) {
      if (!stopping) post({ type: "error", stage: "video", message: (err as Error).message, fatal: true });
      break;
    } finally {
      frame?.close();
    }
  }
}

async function setupVideoEncoder(frame: VideoFrame): Promise<void> {
  videoSize = { width: frame.displayWidth, height: frame.displayHeight };
  const config: VideoEncoderConfig = {
    codec: "vp8",
    width: videoSize.width,
    height: videoSize.height,
    bitrate: cfg.video.bitrate,
    framerate: cfg.video.frameRate,
    latencyMode: "realtime",
  };
  const support = await VideoEncoder.isConfigSupported(config);
  if (!support.supported) throw new Error(`VP8 encoding at ${videoSize.width}x${videoSize.height} is not supported`);
  videoEncoder = new VideoEncoder({
    output: onVideoChunk,
    error: (err) => post({ type: "error", stage: "video", message: `Video encoder: ${err.message}`, fatal: true }),
  });
  videoEncoder.configure(config);
  maybeReady();
}

function onVideoChunk(chunk: EncodedVideoChunk, meta?: EncodedVideoChunkMetadata): void {
  if (meta?.decoderConfig) videoMeta = { decoderConfig: meta.decoderConfig };
  const data = new Uint8Array(chunk.byteLength);
  chunk.copyTo(data);
  const ts = videoTimeline.get(chunk.timestamp) ?? nowUs();
  videoTimeline.delete(chunk.timestamp);
  const prev = videoPackets[videoPackets.length - 1];
  if (prev && ts > prev.ts) prev.dur = ts - prev.ts;
  videoPackets.push({ ts, dur: chunk.duration ?? Math.round(1e6 / cfg.video.frameRate), key: chunk.type === "key", data });
  latestVideoUs = ts;
  prune();
}

// ------------------------------------------------------------------ audio

async function audioLoop(): Promise<void> {
  const reader = audioReader!;
  for (;;) {
    let data: AudioData | undefined;
    try {
      const r = await reader.read();
      if (r.done) break;
      data = r.value;
      if (inBacklog()) continue; // data closed in finally
      const ts = audioClock.map(data.timestamp, nowUs());
      if (!firstAudio) firstAudio = { ts: data.timestamp, at: performance.now() };
      if (!audioEncoder) await setupAudioEncoder(data);
      const mono = toMono(data);
      pcm.push({ ts, index: encInputSamples, samples: mono });
      encInputs.push({ startSample: encInputSamples, ts });
      encInputSamples += data.numberOfFrames;
      audioEncoder!.encode(data); // the encoder keeps its own copy; `data` is closed below
      vadQueue.push({ mono, ts });
      void pumpVad();
    } catch (err) {
      if (!stopping) post({ type: "error", stage: "audio", message: (err as Error).message, fatal: true });
      break;
    } finally {
      data?.close();
    }
  }
}

async function setupAudioEncoder(data: AudioData): Promise<void> {
  audioRate = data.sampleRate;
  resampler = new StreamingResampler(audioRate, VAD_RATE);
  const config: AudioEncoderConfig = {
    codec: "opus",
    sampleRate: data.sampleRate,
    numberOfChannels: data.numberOfChannels,
    bitrate: cfg.audio.bitrate,
  };
  const support = await AudioEncoder.isConfigSupported(config);
  if (!support.supported) throw new Error(`Opus encoding at ${data.sampleRate} Hz is not supported`);
  audioEncoder = new AudioEncoder({
    output: (chunk, meta) => {
      if (meta?.decoderConfig) audioMeta = { decoderConfig: meta.decoderConfig };
      const bytes = new Uint8Array(chunk.byteLength);
      chunk.copyTo(bytes);
      const dur = chunk.duration ?? 20000;
      const ts = timestampForSample(encOutputSamples);
      encOutputSamples += Math.round((dur * audioRate) / 1e6);
      audioPackets.push({ ts, dur, key: true, data: bytes, pos: encOutputSamples - Math.round((dur * audioRate) / 1e6) });
      latestAudioUs = ts;
    },
    error: (err) => post({ type: "error", stage: "audio", message: `Audio encoder: ${err.message}`, fatal: true }),
  });
  audioEncoder.configure(config);
  maybeReady();
}

/** Capture timestamp of the n-th sample fed to the audio encoder. */
function timestampForSample(n: number): number {
  let i = encInputs.length - 1;
  while (i > 0 && encInputs[i].startSample > n) i--;
  const block = encInputs[i];
  if (i > 0) encInputs = encInputs.slice(i); // older blocks are no longer needed
  return block.ts + ((n - block.startSample) / audioRate) * 1e6;
}

function toMono(data: AudioData): Float32Array {
  const n = data.numberOfFrames;
  const out = new Float32Array(n);
  const tmp = data.numberOfChannels > 1 ? new Float32Array(n) : out;
  for (let ch = 0; ch < data.numberOfChannels; ch++) {
    data.copyTo(tmp, { planeIndex: ch, format: "f32-planar" });
    if (tmp !== out) for (let i = 0; i < n; i++) out[i] += tmp[i] / data.numberOfChannels;
  }
  return out;
}

function maybeReady(): void {
  if (readyPosted || !vadLoaded || !videoEncoder || !audioEncoder || !firstVideo || !firstAudio) return;
  readyPosted = true;
  post({
    type: "ready",
    videoCodec: "vp8",
    audioSampleRate: audioRate,
  });
}

// ------------------------------------------------------------------ VAD + event timing

async function pumpVad(): Promise<void> {
  if (vadRunning) return;
  vadRunning = true;
  try {
    await vadReady;
    while (vadQueue.length > 0 && !stoppingVad) {
      const item = vadQueue.shift()!;
      await runVad(item.mono, item.ts);
    }
  } catch {
    /* reported by runVad */
  } finally {
    vadRunning = false;
  }
}

async function runVad(mono: Float32Array, ts: number): Promise<void> {
  if (audioBaseUs === null) audioBaseUs = ts;
  // Re-anchor if the stream skipped (dropped audio), so event times stay on the capture clock.
  const expected = audioBaseUs + (inputSamples / audioRate) * 1e6;
  if (Math.abs(ts - expected) > 20000) audioBaseUs += ts - expected;
  inputSamples += mono.length;

  const out = resampler!.process(mono);
  for (let i = 0; i < out.length; i++) {
    frame16[fill16++] = out[i];
    if (fill16 < SILERO_FRAME_SAMPLES) continue;
    const frame = frame16;
    frame16 = new Float32Array(SILERO_FRAME_SAMPLES);
    fill16 = 0;
    let prob: number;
    try {
      prob = await vad.process(frame);
    } catch (err) {
      post({ type: "error", stage: "vad", message: (err as Error).message, fatal: true });
      throw err;
    }
    const level = rmsDb(frame);
    if (!recorder.recording && prob < NOISE_UPDATE_MAX_PROB) noise.update(level);
    handle(recorder.process(frame, prob, noise.snr(level)));
  }
}

const toUs = (sample16: number) => audioBaseUs! + (sample16 / VAD_RATE) * 1e6;
/** Wall-clock ms, measured relative to Date.now() so it matches the page clock exactly. */
const toWall = (us: number) => Date.now() - ((performance.timeOrigin + performance.now()) - us / 1000);
const holdMarginUs = () => cfg.recorder.preSpeechMs * 1000 + cfg.video.keyframeIntervalUs + RETENTION_MARGIN_US;

function handle(events: RecorderEvent[]): void {
  for (const ev of events) {
    switch (ev.type) {
      case "speech-start": {
        const us = toUs(ev.sample);
        speechStartUs.set(ev.eventId, us);
        openEventHoldUs = us - holdMarginUs();
        post({ type: "capture-start", eventId: ev.eventId, at: toWall(us) });
        break;
      }
      case "clip":
        scheduleClip(ev.clip);
        break;
      case "speech-end":
        openEventHoldUs = null;
        post({ type: "capture-end", eventId: ev.eventId, at: toWall(toUs(ev.sample)) });
        break;
      case "misfire":
        openEventHoldUs = null;
        speechStartUs.delete(ev.eventId);
        post({ type: "misfire", speechMs: ev.speechMs });
        break;
    }
  }
}

function prune(): void {
  const latest = Math.max(latestVideoUs, latestAudioUs);
  let keepFrom = latest - holdMarginUs();
  if (openEventHoldUs !== null) keepFrom = Math.min(keepFrom, openEventHoldUs);
  // Audio not yet analysed by VAD may still start an event: keep its pre-event window.
  if (vadQueue.length > 0) keepFrom = Math.min(keepFrom, vadQueue[0].ts - holdMarginUs());
  for (const us of pendingHolds.values()) keepFrom = Math.min(keepFrom, us);
  videoPackets = pruneVideo(videoPackets, keepFrom);
  audioPackets = pruneBefore(audioPackets, keepFrom);
  if (audioRate) pcm = prunePcm(pcm, keepFrom, audioRate);
}

// ------------------------------------------------------------------ clip assembly

function scheduleClip(clip: FinalizedClip): void {
  const startUs = toUs(clip.startSample);
  let endUs = toUs(clip.startSample + clip.samples.length);
  const holdKey = `${clip.eventId}:${clip.partIndex}`;
  pendingHolds.set(holdKey, startUs - cfg.video.keyframeIntervalUs - RETENTION_MARGIN_US);
  assembleChain = assembleChain.then(async () => {
    try {
      await waitForData(endUs);
      // Never extend a clip past captured data (e.g. the event in progress when monitoring stops).
      const lastVideo = videoPackets[videoPackets.length - 1];
      const lastPcm = pcm[pcm.length - 1];
      if (lastVideo) endUs = Math.min(endUs, lastVideo.ts + lastVideo.dur);
      if (lastPcm) endUs = Math.min(endUs, lastPcm.ts + (lastPcm.samples.length / audioRate) * 1e6);
      const video = selectVideo(videoPackets, startUs, endUs);
      if (video.length === 0 || !videoMeta || !audioMeta) {
        const span = (xs: TimedPacket[]) => (xs.length ? `${((xs[0].ts - startUs) / 1000).toFixed(0)}..${((xs[xs.length - 1].ts - startUs) / 1000).toFixed(0)} ms` : "empty");
        throw new Error(
          `No buffered video for this event (event ${((endUs - startUs) / 1000).toFixed(0)} ms; buffered video ${span(videoPackets)}, audio ${span(audioPackets)} relative to event start)`,
        );
      }
      const baseUs = video[0].ts;
      // Within a clip, audio is one continuous run of samples starting at the sample that
      // matches the first video frame. The WAV and the Opus track are both cut from that
      // run by sample position, so they are identical in content and timing by construction.
      const startSample = sampleIndexAt(pcm, baseUs, audioRate);
      const sampleCount = Math.round(((endUs - baseUs) * audioRate) / 1e6);
      const samples = extractSamples(pcm, startSample, sampleCount);
      // Opus output lags its input by the encoder delay (pre-skip); shift packets back by it.
      const delay = opusPreSkip(audioMeta);
      const audio = audioPackets
        .filter((p) => p.pos !== undefined && p.pos - delay >= startSample && p.pos - delay < startSample + sampleCount)
        .map((p) => ({ ...p, ts: baseUs + ((p.pos! - delay - startSample) / audioRate) * 1e6 }));
      const webm = await muxWebm({ video, audio, baseUs, frameRate: cfg.video.frameRate, videoMeta, audioMeta });
      const wav = wavBlobFromInt16([floatToInt16(samples)], audioRate);
      const speechUs = speechStartUs.get(clip.eventId) ?? startUs;
      const meta: EventClipMeta = {
        eventId: clip.eventId,
        partIndex: clip.partIndex,
        splitReason: clip.splitReason,
        startedAt: toWall(baseUs),
        endedAt: toWall(endUs),
        speechStartedAt: toWall(speechUs),
        durationSec: (endUs - baseUs) / 1e6,
        preEventSec: clip.partIndex === 0 ? Math.max(0, (speechUs - baseUs) / 1e6) : 0,
        video: { codec: "vp8", width: videoSize.width, height: videoSize.height, packets: video.length },
        audio: { codec: "opus", sampleRate: audioRate, durationSec: samples.length / audioRate },
      };
      post({ type: "clip", meta, webm, wav }, [webm]);
    } catch (err) {
      post({ type: "error", stage: "clip", message: `Event clip could not be created: ${(err as Error).message}`, fatal: false });
    } finally {
      pendingHolds.delete(holdKey);
      if (clip.splitReason !== "max_duration") speechStartUs.delete(clip.eventId);
    }
  });
}

/** Encoder delay in samples from the OpusHead pre-skip field (bytes 10-11, little endian). */
function opusPreSkip(meta: EncodedAudioChunkMetadata): number {
  const d = meta.decoderConfig?.description;
  if (!d) return 0;
  const bytes = ArrayBuffer.isView(d) ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength) : new Uint8Array(d as ArrayBuffer);
  if (bytes.length < 12) return 0;
  const preSkip48k = bytes[10] | (bytes[11] << 8); // pre-skip is defined at 48 kHz
  return Math.round((preSkip48k * audioRate) / 48000);
}

/** Encoders run slightly behind capture; wait until both have produced output up to `endUs`. */
async function waitForData(endUs: number): Promise<void> {
  const frameUs = 1e6 / cfg.video.frameRate;
  const deadline = performance.now() + DATA_WAIT_TIMEOUT_MS;
  while (!(flushed || (latestVideoUs >= endUs - frameUs && latestAudioUs >= endUs - 25000))) {
    if (performance.now() > deadline) break;
    await new Promise((r) => setTimeout(r, 30));
  }
}

// ------------------------------------------------------------------ shutdown

async function stop(): Promise<void> {
  if (stopping) return;
  stopping = true;
  await Promise.allSettled([videoReader?.cancel(), audioReader?.cancel()]);
  await Promise.allSettled(loops);
  // Let VAD finish the audio that is still queued, so the last event ends correctly.
  void pumpVad();
  const drainDeadline = performance.now() + 3000;
  while ((vadRunning || vadQueue.length > 0) && performance.now() < drainDeadline) await new Promise((r) => setTimeout(r, 10));
  stoppingVad = true;
  if (recorder) handle(recorder.flush()); // event in progress -> final clip
  await Promise.allSettled([videoEncoder?.flush(), audioEncoder?.flush()]);
  flushed = true;
  await assembleChain;
  for (const enc of [videoEncoder, audioEncoder]) if (enc && enc.state !== "closed") enc.close();
  await vad?.release().catch(() => undefined);
  post({ type: "stopped" });
}
