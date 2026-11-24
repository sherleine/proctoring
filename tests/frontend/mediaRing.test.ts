import { describe, expect, it } from "vitest";
import { extractPcm, floatToInt16, prunePcm, pruneVideo, selectRange, selectVideo, type PcmBlock, type TimedPacket } from "../../frontend/src/media/mediaRing";

/** 30 fps video, keyframe every second (every 30th frame), timestamps in us. */
const video: TimedPacket[] = Array.from({ length: 300 }, (_, i) => ({
  ts: 10_000_000 + Math.round((i * 1e6) / 30),
  dur: 33_333,
  key: i % 30 === 0,
  data: new Uint8Array([i % 256]),
}));

describe("selectVideo", () => {
  it("starts at the last keyframe at or before the requested start", () => {
    const clip = selectVideo(video, 12_500_000, 15_000_000);
    expect(clip[0].key).toBe(true);
    expect(clip[0].ts).toBe(12_000_000);
    expect(clip[clip.length - 1].ts).toBeLessThan(15_000_000);
    expect(clip.every((p, i) => i === 0 || p.ts > clip[i - 1].ts)).toBe(true);
  });

  it("uses the first keyframe when the buffer does not reach back far enough", () => {
    const partial = video.slice(45); // first keyframe in the buffer is frame 60
    const clip = selectVideo(partial, 10_000_000, 13_000_000);
    expect(clip[0].ts).toBe(video[60].ts);
  });

  it("returns nothing when there is no keyframe", () => {
    expect(selectVideo(video.filter((p) => !p.key), 11e6, 12e6)).toEqual([]);
  });
});

describe("pruning", () => {
  it("keeps the keyframe preceding the cut so the next clip stays decodable", () => {
    const kept = pruneVideo(video, 12_500_000);
    expect(kept[0].ts).toBe(12_000_000);
    expect(kept[0].key).toBe(true);
  });

  it("drops PCM blocks that end before the cut", () => {
    const blocks: PcmBlock[] = Array.from({ length: 10 }, (_, i) => ({ ts: i * 10_000, samples: new Float32Array(480) }));
    expect(prunePcm(blocks, 35_000, 48000)[0].ts).toBe(30_000);
  });

  it("selectRange is half-open", () => {
    expect(selectRange(video, 12_000_000, 12_100_000).map((p) => p.ts)).toEqual([12_000_000, 12_033_333, 12_066_667]);
  });
});

describe("extractPcm", () => {
  const SR = 48000;
  // 10 ms blocks whose samples hold the absolute sample index
  const blocks: PcmBlock[] = Array.from({ length: 100 }, (_, b) => ({
    ts: 5_000_000 + b * 10_000,
    samples: Float32Array.from({ length: 480 }, (_, i) => b * 480 + i),
  }));

  it("returns exactly the requested range on the capture timeline", () => {
    const out = extractPcm(blocks, SR, 5_100_000, 5_300_000);
    expect(out.length).toBe(9600);
    expect(out[0]).toBe(4800); // 100 ms after the first block
    expect(out[out.length - 1]).toBe(4800 + 9599);
  });

  it("pads missing lead-in and gaps with silence instead of shifting audio", () => {
    const gappy = blocks.filter((_, b) => b !== 20); // 10 ms of audio dropped
    const out = extractPcm(gappy, SR, 4_990_000, 5_300_000);
    expect(out.slice(0, 480).every((x) => x === 0)).toBe(true); // 10 ms before the first block
    const gapStart = 480 + 20 * 480;
    expect(out.slice(gapStart, gapStart + 480).every((x) => x === 0)).toBe(true);
    expect(out[gapStart + 480]).toBe(21 * 480); // audio after the gap stays at its true position
  });
});

it("floatToInt16 clamps and scales", () => {
  expect(Array.from(floatToInt16(Float32Array.from([0, 1, -1, 2, -0.5])))).toEqual([0, 32767, -32768, 32767, -16384]);
});

describe("StreamClock", () => {
  it("anchors an arbitrary device epoch to arrival time and keeps device spacing", async () => {
    const { StreamClock } = await import("../../frontend/src/media/mediaRing");
    const c = new StreamClock();
    const epoch = 253_000_000_000; // device clock far from the worker clock
    const out = [0, 1, 2, 3].map((i) => c.map(epoch + i * 33_333, 1_000_000 + i * 33_333 + (i % 2) * 3000));
    expect(out[0]).toBe(1_000_000);
    expect(out[3] - out[0]).toBe(3 * 33_333); // jitter-free spacing from the device
  });

  it("corrects a late anchor (frames queued before reading) down to real-time arrival", async () => {
    const { StreamClock } = await import("../../frontend/src/media/mediaRing");
    const c = new StreamClock();
    // 10 frames captured 10 ms apart arrive together at t=500 ms (backlog), then in real time.
    for (let i = 0; i < 10; i++) c.map(i * 10_000, 500_000);
    const t = c.map(10 * 10_000, 501_000);
    expect(t).toBeLessThanOrEqual(501_000);
    // capture of frame 10 happened at ~100 ms + offset; afterwards frames follow device spacing again
    expect(c.map(11 * 10_000, 511_500)).toBe(t + 10_000);
  });

  it("never places a frame after it arrived and recovers from jumps and fast clocks", async () => {
    const { StreamClock } = await import("../../frontend/src/media/mediaRing");
    const c = new StreamClock();
    let last = -Infinity;
    for (let i = 0; i < 100; i++) {
      const arrival = 2_000_000 + i * 20_000;
      const raw = i === 50 ? 9e12 : i * 200_000; // device clock 10x too fast, plus a jump
      const t = c.map(raw, arrival);
      expect(t).toBeLessThanOrEqual(arrival);
      expect(t).toBeGreaterThan(last);
      expect(arrival - t).toBeLessThan(1_000_001);
      last = t;
    }
  });
});

describe("sample-position cutting (clip audio)", () => {
  it("maps timeline time to a stream position and cuts a continuous run", async () => {
    const { sampleIndexAt, extractSamples } = await import("../../frontend/src/media/mediaRing");
    // 10 ms blocks at 48 kHz; a timestamp skip of 30 ms between blocks 4 and 5 (no samples lost)
    const blocks = Array.from({ length: 10 }, (_, b) => ({
      ts: 1_000_000 + b * 10_000 + (b >= 5 ? 30_000 : 0),
      index: b * 480,
      samples: Float32Array.from({ length: 480 }, (_, i) => b * 480 + i),
    }));
    const start = sampleIndexAt(blocks, 1_025_000, 48000); // 5 ms into block 2
    expect(start).toBe(2 * 480 + 240);
    const run = extractSamples(blocks, start, 4800);
    // contiguous samples, even across the timestamp skip
    expect(Array.from(run.slice(0, 3))).toEqual([1200, 1201, 1202]);
    expect(run[4799 - 1200 + 1200]).toBe(0); // beyond the buffer -> silence
    expect(run[3599]).toBe(1200 + 3599);
  });
});
