// Event rules with SCRIPTED speech probabilities (no model). They prove the state machine, not the microphone.
import { describe, expect, it } from "vitest";
import { EventDetector, type DetectedEvent, type DetectorConfig, type DetectorOutput } from "../src/audio/eventDetector";

const SR = 16000;
const FRAME = 512;
const FRAME_MS = (FRAME / SR) * 1000; // 32 ms

const cfg: DetectorConfig = {
  sampleRate: SR, startThreshold: 0.5, endThreshold: 0.35, minSpeechMs: 250, candidateGapMs: 300,
  silenceMs: 1500, preRollMs: 2500, tailPadMs: 500, maxEventMs: 10_000, overlapMs: 1000, bufferMs: 5000,
};

function run(detector: EventDetector, script: [ms: number, p: number][]): DetectorOutput[] {
  const out: DetectorOutput[] = [];
  for (const [ms, p] of script) {
    const frames = Math.round(ms / FRAME_MS);
    for (let i = 0; i < frames; i++) out.push(...detector.push(new Float32Array(FRAME), p));
  }
  return out;
}

const events = (out: DetectorOutput[]) => out.filter((o) => o.type === "event").map((o) => (o as { event: DetectedEvent }).event);
const seconds = (samples: number) => samples / SR;
let ids = 0;
const newDetector = (overrides: Partial<DetectorConfig> = {}) => new EventDetector({ ...cfg, ...overrides }, () => `g${++ids}`);

describe("EventDetector - repeated speech (the reported bug)", () => {
  it("A: four speech segments separated by silence give four separate events", () => {
    const d = newDetector();
    const out = run(d, [[3000, 0], [1500, 0.95], [5000, 0], [1200, 0.9], [5000, 0], [2000, 0.97], [5000, 0], [900, 0.9], [5000, 0]]);
    const evs = events(out);
    expect(evs).toHaveLength(4);
    expect(new Set(evs.map((e) => e.groupId)).size).toBe(4);
    expect(evs.map((e) => e.isFinal)).toEqual([true, true, true, true]);
    expect(evs.map((e) => Math.round(e.speechSeconds * 10) / 10)).toEqual([1.5, 1.2, 2, 0.9]);
    for (let i = 1; i < evs.length; i++) expect(evs[i].startSample).toBeGreaterThan(evs[i - 1].endSample); // no shared audio
    expect(d.state).toBe("IDLE");
    expect(d.eventCount).toBe(4);
  });

  it("B: keeps working through a long exam with many speech/silence transitions", () => {
    const d = newDetector();
    const script: [number, number][] = [];
    for (let i = 0; i < 60; i++) script.push([4000 + (i % 7) * 500, 0.02], [600 + (i % 5) * 300, 0.9]);
    script.push([3000, 0]);
    const evs = events(run(d, script)); // about 6.7 minutes of audio
    expect(evs).toHaveLength(60);
    expect(d.samplesSeen / SR).toBeGreaterThan(6 * 60);
    expect(d.state).toBe("IDLE");
    // every event has its pre-roll, even late in the exam (the rolling buffer survives finalization)
    expect(evs.every((e) => e.preEventSeconds > 2.4)).toBe(true);
  });

  it("returns to IDLE after every event and can start the next one immediately", () => {
    const d = newDetector();
    run(d, [[3000, 0], [1000, 0.9]]);
    expect(d.state).toBe("RECORDING");
    const closing = run(d, [[1600, 0]]);
    expect(events(closing)).toHaveLength(1);
    expect(d.state).toBe("IDLE");
    const next = run(d, [[400, 0.9]]);
    expect(next.some((o) => o.type === "speech-start")).toBe(true);
    expect(d.state).toBe("RECORDING");
  });

  it("goes IDLE -> SPEECH_DETECTED -> RECORDING", () => {
    const d = newDetector();
    expect(d.state).toBe("IDLE");
    run(d, [[2000, 0], [64, 0.9]]);
    expect(d.state).toBe("SPEECH_DETECTED");
    run(d, [[256, 0.9]]);
    expect(d.state).toBe("RECORDING");
  });

  it("survives non-numeric probabilities without stopping", () => {
    const d = newDetector();
    run(d, [[1000, Number.NaN], [3000, 0], [1000, 0.9], [2000, 0]]);
    expect(d.eventCount).toBe(1);
  });
});

describe("EventDetector - what is saved", () => {
  it("C: merges short pauses into one event", () => {
    const d = newDetector();
    const evs = events(run(d, [[3000, 0], [1000, 0.9], [800, 0.1], [1000, 0.9], [1200, 0.2], [600, 0.8], [3000, 0]]));
    expect(evs).toHaveLength(1);
    expect(evs[0].speechSeconds).toBeCloseTo(2.6, 1);
  });

  it("D: long silence and background noise below the threshold create no events", () => {
    const d = newDetector();
    expect(run(d, [[120_000, 0.02], [30_000, 0.3]])).toEqual([]);
    expect(d.flush()).toEqual([]);
  });

  it("ignores sounds shorter than the minimum speech duration", () => {
    const d = newDetector();
    expect(run(d, [[3000, 0], [160, 0.9], [1000, 0], [100, 0.9], [3000, 0]])).toEqual([]);
  });

  it("E: clip = pre-roll + speech + short tail; the closing silence is not saved", () => {
    const d = newDetector();
    const [e] = events(run(d, [[4000, 0], [2016, 0.9], [5000, 0]]));
    const speechStart = 125 * FRAME;
    const speechEnd = speechStart + 63 * FRAME;
    expect(e.speechStartSample).toBe(speechStart);
    expect(e.speechEndSample).toBe(speechEnd);
    expect(seconds(e.speechStartSample - e.startSample)).toBeCloseTo(2.5, 2); // beginning of words kept
    expect(seconds(e.endSample - e.speechEndSample)).toBeCloseTo(0.5, 2); // end of words kept, silence dropped
    expect(seconds(e.endSample - e.startSample)).toBeCloseTo(2.5 + 2.016 + 0.5, 2);
    expect(e.samples.length).toBe(e.endSample - e.startSample);
  });

  it("returns exactly the captured audio of the clip from the rolling buffer", () => {
    const d = newDetector();
    const out: DetectorOutput[] = [];
    let n = 0;
    const push = (p: number) => {
      const frame = new Float32Array(FRAME);
      for (let i = 0; i < FRAME; i++) frame[i] = ((n + i) % 1000) / 1000;
      n += FRAME;
      out.push(...d.push(frame, p));
    };
    for (let k = 0; k < 3; k++) { // three events: the buffer stays correct after each finalization
      for (let i = 0; i < 200; i++) push(0);
      for (let i = 0; i < 40; i++) push(0.9);
    }
    for (let i = 0; i < 60; i++) push(0);
    const evs = events(out);
    expect(evs).toHaveLength(3);
    for (const e of evs)
      for (const k of [0, 1, 777, e.samples.length - 1]) expect(e.samples[k]).toBeCloseTo(((e.startSample + k) % 1000) / 1000, 5);
  });

  it("pre-roll is limited to the audio captured so far", () => {
    const d = newDetector();
    const [e] = events(run(d, [[500, 0], [1000, 0.9], [2000, 0]]));
    expect(e.startSample).toBe(0);
    expect(e.preEventSeconds).toBeCloseTo(0.5, 1);
  });

  it("splits long speech into parts of at most maxEvent with overlap", () => {
    const d = newDetector();
    const out = run(d, [[3000, 0], [21_000, 0.9], [2000, 0]]);
    const evs = events(out);
    expect(evs.length).toBe(3);
    expect(new Set(evs.map((e) => e.groupId)).size).toBe(1);
    expect(evs.map((e) => e.isFinal)).toEqual([false, false, true]);
    for (const e of evs) expect(seconds(e.endSample - e.startSample)).toBeLessThanOrEqual(10 + FRAME / SR);
    expect(seconds(evs[0].endSample - evs[1].startSample)).toBeCloseTo(1, 2);
    expect(out.filter((o) => o.type === "speech-start")).toHaveLength(3);
  });

  it("flush finalizes a recording in progress and drops an unconfirmed candidate", () => {
    const d = newDetector();
    run(d, [[3000, 0], [1000, 0.9]]);
    expect(events(d.flush())).toHaveLength(1);
    expect(d.state).toBe("IDLE");
    const d2 = newDetector();
    run(d2, [[3000, 0], [100, 0.9]]);
    expect(d2.flush()).toEqual([]);
  });

  it("rejects inconsistent configuration", () => {
    expect(() => newDetector({ endThreshold: 0.6 })).toThrow();
    expect(() => newDetector({ overlapMs: 10_000 })).toThrow();
    expect(() => newDetector({ tailPadMs: 2000 })).toThrow();
  });
});
