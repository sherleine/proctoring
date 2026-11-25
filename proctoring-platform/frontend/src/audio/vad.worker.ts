// Web Worker: Silero VAD (ONNX Runtime Web, WASM, single thread) + rolling buffer + event detection + WAV.
// Frames arrive straight from the AudioWorklet (MessagePort); results go to the main thread.
import * as ort from "onnxruntime-web/wasm";
import { EventDetector, type DetectorConfig, type DetectorOutput } from "./eventDetector";
import { SileroVad, SILERO_FRAME_SAMPLES } from "./sileroVad";
import { encodeWav, validateWav } from "./wav";

export type WorkerIn =
  | { type: "init"; modelUrl: string; ortBaseUrl: string | null; detector: DetectorConfig }
  | { type: "port"; port: MessagePort }
  | { type: "flush" }
  // Voice sample (enrolment): frames are collected instead of being turned into speech events.
  | { type: "enrol-start"; maxSeconds: number }
  | { type: "enrol-stop" };

export type WorkerOut =
  | { type: "ready" }
  | { type: "error"; stage: "init" | "inference" | "wav" | "pipeline"; message: string }
  | { type: "started"; contextTime: number; inputRate: number }
  | { type: "level"; probability: number; rmsDbfs: number; speaking: boolean; samples: number; state: import("./eventDetector").DetectorState; events: number }
  | { type: "speech-start"; groupId: string; partIndex: number; clipStartSample: number; speechStartSample: number }
  | { type: "event"; event: Omit<import("./eventDetector").DetectedEvent, "samples">; wav: ArrayBuffer }
  | { type: "flushed" }
  | { type: "enrol-progress"; seconds: number; speechSeconds: number }
  | { type: "enrolment"; wav: ArrayBuffer; startSample: number; samples: number; speechSeconds: number; maxProbability: number };

const post = (msg: WorkerOut, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(msg, transfer);

let vad: SileroVad | null = null;
let detector: EventDetector | null = null;
let chain: Promise<void> = Promise.resolve();
let framePort: MessagePort | null = null; // strong reference: the audio frames arrive on this port
let frameCount = 0;
let failed = false;

// Voice-sample recording state. While active, the detector sees probability 0 (no speech events from the sample),
// but it still receives every frame so its rolling buffer and sample clock stay in step.
const SAMPLE_RATE = 16000;
let enrol: { chunks: Float32Array[]; startSample: number; maxSamples: number; speechFrames: number; maxP: number } | null = null;

function finishEnrolment(): void {
  if (!enrol) return;
  const { chunks, startSample, speechFrames, maxP } = enrol;
  enrol = null;
  const length = chunks.reduce((n, c) => n + c.length, 0);
  const all = new Float32Array(length);
  let offset = 0;
  for (const c of chunks) { all.set(c, offset); offset += c.length; }
  try {
    const wav = encodeWav(all);
    validateWav(wav);
    post({ type: "enrolment", wav, startSample, samples: length, maxProbability: maxP,
      speechSeconds: (speechFrames * SILERO_FRAME_SAMPLES) / SAMPLE_RATE }, [wav]);
  } catch (err) {
    post({ type: "error", stage: "wav", message: (err as Error).message });
  }
}

function emit(outputs: DetectorOutput[]): void {
  for (const o of outputs) {
    if (o.type === "speech-start") {
      post(o);
      continue;
    }
    const { samples, ...event } = o.event;
    try {
      const wav = encodeWav(samples);
      validateWav(wav); // never upload something the backend would reject as a WAV
      post({ type: "event", event, wav }, [wav]);
    } catch (err) {
      post({ type: "error", stage: "wav", message: (err as Error).message });
    }
  }
}

async function handleFrame(samples: Float32Array): Promise<void> {
  if (!vad || !detector || failed) return;
  if (samples.length !== SILERO_FRAME_SAMPLES) return;
  let probability: number;
  try {
    probability = await vad.process(samples);
  } catch (err) {
    failed = true;
    post({ type: "error", stage: "inference", message: (err as Error).message });
    return;
  }
  if (enrol) {
    if (enrol.chunks.length === 0) enrol.startSample = detector.samplesSeen;
    enrol.chunks.push(samples.slice());
    if (probability >= 0.5) enrol.speechFrames++;
    enrol.maxP = Math.max(enrol.maxP, probability);
    const recorded = enrol.chunks.length * SILERO_FRAME_SAMPLES;
    if (enrol.chunks.length % 8 === 0) {
      post({ type: "enrol-progress", seconds: recorded / SAMPLE_RATE, speechSeconds: (enrol.speechFrames * SILERO_FRAME_SAMPLES) / SAMPLE_RATE });
    }
    emit(detector.push(samples, 0)); // the sample never becomes a speech event
    if (recorded >= enrol.maxSamples) finishEnrolment();
  } else {
    emit(detector.push(samples, probability));
  }
  if (++frameCount % 4 === 0) {
    let sum = 0;
    for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
    const rmsDbfs = Math.max(-100, 20 * Math.log10(Math.sqrt(sum / samples.length) || 1e-5));
    post({ type: "level", probability, rmsDbfs, speaking: detector.isActive, samples: detector.samplesSeen, state: detector.state, events: detector.eventCount });
  }
}

self.onmessage = async (e: MessageEvent<WorkerIn>) => {
  const msg = e.data;
  if (msg.type === "init") {
    try {
      // By default the runtime files are resolved by the bundler (Vite serves/emits them with the app).
      if (msg.ortBaseUrl) ort.env.wasm.wasmPaths = msg.ortBaseUrl;
      ort.env.wasm.numThreads = 1; // no cross-origin isolation needed; the model is tiny
      vad = await SileroVad.create(ort, msg.modelUrl); // loaded once per exam
      detector = new EventDetector(msg.detector);
      post({ type: "ready" });
    } catch (err) {
      post({ type: "error", stage: "init", message: (err as Error).message });
    }
  } else if (msg.type === "port") {
    framePort = msg.port;
    framePort.onmessage = (m: MessageEvent) => {
      if (m.data?.type === "started") post(m.data);
      else if (m.data?.type === "frame") {
        const samples = m.data.samples as Float32Array;
        // Strictly in order. A failure is reported and never blocks the following frames.
        chain = chain.then(() => handleFrame(samples)).catch((err) =>
          post({ type: "error", stage: "pipeline", message: (err as Error)?.message ?? String(err) }));
      }
    };
  } else if (msg.type === "enrol-start") {
    enrol = { chunks: [], startSample: 0, maxSamples: Math.round(msg.maxSeconds * SAMPLE_RATE), speechFrames: 0, maxP: 0 };
  } else if (msg.type === "enrol-stop") {
    await chain; // frames already queued belong to the sample
    finishEnrolment();
  } else if (msg.type === "flush") {
    await chain;
    if (detector) emit(detector.flush());
    post({ type: "flushed" });
  }
};

self.addEventListener("error", (e) => post({ type: "error", stage: "pipeline", message: (e as ErrorEvent).message }));
self.addEventListener("unhandledrejection", (e) => post({ type: "error", stage: "pipeline", message: String((e as PromiseRejectionEvent).reason) }));
