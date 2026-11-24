import { describe, expect, it } from "vitest";
import { ClipRecorder, type ClipRecorderConfig, type FinalizedClip, type RecorderEvent } from "../../frontend/src/audio/clipRecorder";
import { NoiseFloorEstimator } from "../../frontend/src/audio/noiseFloor";

const FS = 512;
const base: ClipRecorderConfig = {
  sampleRate: 16000,
  frameSamples: FS,
  startThreshold: 0.5,
  endThreshold: 0.35,
  minSpeechMs: 250, // 8 frames
  silenceMs: 704, // 22 frames
  preSpeechMs: 320, // 5120 samples = 10 frames
  postSpeechPadMs: 192, // 6 frames
  maxClipMs: 10000,
  minStartSnrDb: 3,
};

/** Frame whose every sample holds its global frame index, so clip contents can be traced. */
const frame = (i: number) => new Float32Array(FS).fill(i);

function run(rec: ClipRecorder, probs: number[], startIndex = 0) {
  const events: RecorderEvent[] = [];
  probs.forEach((p, k) => events.push(...rec.process(frame(startIndex + k), p, null)));
  return events;
}

const clips = (events: RecorderEvent[]) => events.filter((e): e is { type: "clip"; clip: FinalizedClip } => e.type === "clip").map((e) => e.clip);
const frameIds = (c: FinalizedClip) => Array.from({ length: c.samples.length / FS }, (_, i) => c.samples[i * FS]);
const seq = (a: number, b: number) => Array.from({ length: b - a }, (_, i) => a + i);
const P = (n: number, v: number) => Array<number>(n).fill(v);

let ids = 0;
const newRecorder = (cfg: Partial<ClipRecorderConfig> = {}) => new ClipRecorder({ ...base, ...cfg }, () => `ev${++ids}`);

describe("ClipRecorder", () => {
  it("does nothing during silence", () => {
    expect(run(newRecorder(), P(200, 0.05))).toEqual([]);
  });

  it("captures pre-speech audio, the speech and a short trailing pad", () => {
    const rec = newRecorder();
    // frames 0-29 silence, 30-59 speech, 60-89 silence
    const events = run(rec, [...P(30, 0.02), ...P(30, 0.9), ...P(30, 0.02)]);
    expect(events.map((e) => e.type)).toEqual(["speech-start", "clip", "speech-end"]);
    const [c] = clips(events);
    // 10 pre-speech frames (20-29), speech 30-59, 6 pad frames 60-65
    expect(frameIds(c)).toEqual(seq(20, 66));
    expect(c.preSpeechSamples).toBe(10 * FS);
    expect(c.startSample).toBe(20 * FS);
    expect(c.speechFrames).toBe(30);
    expect(c.splitReason).toBe("speech_end");
  });

  it("uses hysteresis: frames between the thresholds continue speech but cannot start it", () => {
    const rec = newRecorder();
    expect(run(rec, P(40, 0.4))).toEqual([]); // 0.4 < start threshold
    const events = run(rec, [...P(10, 0.9), ...P(40, 0.4), ...P(30, 0.0)], 40);
    const [c] = clips(events);
    expect(c.speechFrames).toBe(50); // 0.4 >= end threshold keeps the clip going
  });

  it("keeps a clip open across pauses shorter than the silence duration", () => {
    const rec = newRecorder();
    const events = run(rec, [...P(10, 0.9), ...P(15, 0.05), ...P(10, 0.9), ...P(30, 0.05)]);
    expect(clips(events)).toHaveLength(1);
  });

  it("ignores short bursts (misfire) and recycles their audio as pre-speech", () => {
    const rec = newRecorder();
    const events = run(rec, [...P(5, 0.02), ...P(3, 0.9), ...P(30, 0.02)]);
    expect(events.map((e) => e.type)).toEqual(["speech-start", "misfire"]);
    expect(clips(events)).toHaveLength(0);
  });

  it("splits long speech into contiguous clips without losing or duplicating audio", () => {
    const rec = newRecorder({ maxClipMs: 2048 }); // 64 frames per clip
    const events = run(rec, [...P(20, 0.02), ...P(200, 0.9), ...P(30, 0.02)]);
    const cs = clips(events);
    expect(cs.length).toBe(4);
    expect(cs.slice(0, -1).every((c) => c.splitReason === "max_duration" && c.samples.length === 64 * FS)).toBe(true);
    expect(new Set(cs.map((c) => c.eventId)).size).toBe(1);
    expect(cs.map((c) => c.partIndex)).toEqual([0, 1, 2, 3]);
    const all = cs.flatMap(frameIds);
    expect(all).toEqual(seq(10, 226)); // pre-speech from 10, speech 20-219, pad 220-225: every frame exactly once
    for (let i = 1; i < cs.length; i++) {
      expect(cs[i].startSample).toBe(cs[i - 1].startSample + cs[i - 1].samples.length);
    }
    expect(events.filter((e) => e.type === "speech-end")).toHaveLength(1);
  });

  it("finalizes the clip in progress on flush", () => {
    const rec = newRecorder();
    run(rec, [...P(5, 0.02), ...P(20, 0.9)]);
    const events = rec.flush();
    expect(clips(events)[0].splitReason).toBe("session_stopped");
    expect(rec.recording).toBe(false);
  });

  it("does not start a clip when the frame is not above the noise floor", () => {
    const rec = newRecorder();
    expect(rec.process(frame(0), 0.9, 1.0)).toEqual([]); // SNR 1 dB < 3 dB gate
    expect(rec.process(frame(1), 0.9, 10)[0].type).toBe("speech-start");
  });

  it("reports the remaining silence budget during a pause", () => {
    const rec = newRecorder();
    run(rec, [...P(10, 0.9), ...P(2, 0.05)]);
    expect(rec.silenceRemainingMs).toBeCloseTo(20 * 32, 5);
  });
});

describe("NoiseFloorEstimator", () => {
  it("follows the EWMA formula", () => {
    const n = new NoiseFloorEstimator(0.1);
    expect(n.snr(-40)).toBeNull();
    n.update(-60);
    n.update(-50);
    expect(n.db).toBeCloseTo(0.1 * -50 + 0.9 * -60, 6);
    expect(n.snr(-30)).toBeCloseTo(-30 - n.db!, 6);
  });
});
