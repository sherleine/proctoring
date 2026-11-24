// REAL model test: the Silero VAD ONNX file the browser uses, run with onnxruntime-web (WASM) in Node,
// through the real EventDetector and WAV encoder. Speech: the AI service's text-to-speech fixture.
// This proves the model + event logic on real speech audio; it does NOT replace a real-microphone test.
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import * as ort from "onnxruntime-web/wasm";
import { describe, expect, it } from "vitest";
import { EventDetector, type DetectedEvent } from "../src/audio/eventDetector";
import { SileroVad, SILERO_FRAME_SAMPLES } from "../src/audio/sileroVad";
import { encodeWav, validateWav } from "../src/audio/wav";

const MODEL = join(__dirname, "..", "public", "models", "silero_vad_v5.onnx");
const SPEECH = join(__dirname, "..", "..", "ai-service", "tests", "fixtures", "speech_16k_mono.wav");
const SR = 16000;

function readWav16k(path: string): Float32Array {
  const buf = readFileSync(path);
  const dataAt = buf.indexOf("data") + 8;
  const samples = new Int16Array(buf.buffer.slice(buf.byteOffset + dataAt, buf.byteOffset + buf.length - ((buf.length - dataAt) % 2)));
  return Float32Array.from(samples, (s) => s / 32768);
}

async function loadVad(): Promise<SileroVad> {
  ort.env.wasm.numThreads = 1;
  const url = `data:application/octet-stream;base64,${readFileSync(MODEL).toString("base64")}`;
  return SileroVad.create(ort, url);
}

async function detect(vad: SileroVad, audio: Float32Array) {
  vad.reset();
  const detector = new EventDetector({ sampleRate: SR, startThreshold: 0.5, endThreshold: 0.35, minSpeechMs: 250, candidateGapMs: 300,
    silenceMs: 1500, preRollMs: 2500, tailPadMs: 500, maxEventMs: 10000, overlapMs: 1000, bufferMs: 5000 });
  const events: DetectedEvent[] = [];
  for (let i = 0; i + SILERO_FRAME_SAMPLES <= audio.length; i += SILERO_FRAME_SAMPLES) {
    const frame = audio.slice(i, i + SILERO_FRAME_SAMPLES);
    for (const o of detector.push(frame, await vad.process(frame))) if (o.type === "event") events.push(o.event);
  }
  for (const o of detector.flush()) if (o.type === "event") events.push(o.event);
  return { events, detector };
}

/** First and last sample above -40 dBFS: where the voice actually is. */
function voicedRange(x: Float32Array): [number, number] {
  const limit = 0.01;
  let first = 0;
  while (first < x.length && Math.abs(x[first]) < limit) first++;
  let last = x.length - 1;
  while (last > 0 && Math.abs(x[last]) < limit) last--;
  return [first, last];
}

describe.skipIf(!existsSync(MODEL) || !existsSync(SPEECH))("Silero VAD v5 ONNX + EventDetector (real model, recorded speech)", () => {
  it("F-equivalent: four utterances with pauses give four valid speech-only WAV events; silence and noise give none", async () => {
    const vad = await loadVad();
    const speech = readWav16k(SPEECH);
    const [voiceFrom, voiceTo] = voicedRange(speech);
    const gaps = [4, 5, 6, 5, 6]; // seconds of silence around the utterances
    const total = gaps.reduce((a, b) => a + b, 0) * SR + 4 * speech.length;
    const audio = new Float32Array(total);
    const placed: number[] = [];
    let at = gaps[0] * SR;
    for (let k = 0; k < 4; k++) {
      audio.set(speech, at);
      placed.push(at);
      at += speech.length + gaps[k + 1] * SR;
    }

    const { events, detector } = await detect(vad, audio);
    expect(events).toHaveLength(4);
    expect(detector.state).toBe("IDLE");
    events.forEach((e, k) => {
      const voiceStart = placed[k] + voiceFrom;
      const voiceEnd = placed[k] + voiceTo;
      // the whole utterance is inside the clip (beginning and end of words not cut)
      expect(e.startSample).toBeLessThanOrEqual(voiceStart);
      expect(e.endSample).toBeGreaterThanOrEqual(voiceEnd);
      // ... and the clip is not padded with long silence: <= pre-roll 2.5 s + voice + tail 0.5 s + 1 frame
      expect((e.endSample - e.startSample) / SR).toBeLessThanOrEqual(2.5 + (voiceEnd - voiceStart) / SR + 0.5 + 0.1);
      expect((e.endSample - e.speechEndSample) / SR).toBeLessThanOrEqual(0.55);
      // each clip is a valid upload
      const info = validateWav(encodeWav(e.samples));
      expect(info).toMatchObject({ sampleRate: 16000, channels: 1, bitsPerSample: 16 });
      expect(info.durationSeconds).toBeCloseTo(e.samples.length / SR, 5);
    });

    expect((await detect(vad, new Float32Array(SR * 20))).events).toHaveLength(0);
    let seed = 7;
    const noise = Float32Array.from({ length: SR * 10 }, () => ((seed = (seed * 16807) % 2147483647) / 2147483647 - 0.5) * 0.05);
    expect((await detect(vad, noise)).events).toHaveLength(0);
    await vad.release();
  }, 120_000);
});
