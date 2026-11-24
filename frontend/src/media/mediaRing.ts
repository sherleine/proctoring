/**
 * Rolling buffers of encoded media and raw audio, plus the selection logic that
 * cuts one event clip out of them. Pure functions, no browser APIs (unit-tested).
 *
 * All timestamps are capture timestamps in microseconds (VideoFrame/AudioData
 * clock), so video, Opus audio and PCM share one timeline.
 */

export interface TimedPacket {
  ts: number; // presentation timestamp, us
  dur: number; // us
  key: boolean;
  data: Uint8Array;
  /** Audio only: position of the packet's first sample in the continuous input sample stream. */
  pos?: number;
}

export interface PcmBlock {
  ts: number; // us of samples[0]
  /** Position of samples[0] in the continuous input sample stream. */
  index: number;
  samples: Float32Array; // mono, native sample rate
}

/** Drops packets before `us`, but always keeps the video keyframe that precedes `us`. */
export function pruneVideo(packets: TimedPacket[], us: number): TimedPacket[] {
  let keep = 0;
  for (let i = 0; i < packets.length && packets[i].ts <= us; i++) if (packets[i].key) keep = i;
  return keep > 0 ? packets.slice(keep) : packets;
}

export function pruneBefore<T extends { ts: number }>(items: T[], us: number): T[] {
  let i = 0;
  while (i < items.length && items[i].ts < us) i++;
  return i > 0 ? items.slice(i) : items;
}

export function prunePcm(blocks: PcmBlock[], us: number, sampleRate: number): PcmBlock[] {
  let i = 0;
  while (i < blocks.length && blocks[i].ts + (blocks[i].samples.length / sampleRate) * 1e6 < us) i++;
  return i > 0 ? blocks.slice(i) : blocks;
}

/**
 * Video packets for [startUs, endUs): starts at the last keyframe at or before
 * startUs (a clip must begin with a keyframe to be decodable), or the first
 * keyframe after it if the buffer does not reach back that far.
 */
export function selectVideo(packets: TimedPacket[], startUs: number, endUs: number): TimedPacket[] {
  let first = -1;
  for (let i = 0; i < packets.length; i++) {
    if (!packets[i].key) continue;
    if (packets[i].ts <= startUs) first = i;
    else {
      if (first === -1) first = i;
      break;
    }
  }
  if (first === -1) return [];
  const out: TimedPacket[] = [];
  for (let i = first; i < packets.length && packets[i].ts < endUs; i++) out.push(packets[i]);
  return out;
}

export function selectRange<T extends { ts: number }>(items: T[], fromUs: number, endUs: number): T[] {
  return items.filter((p) => p.ts >= fromUs && p.ts < endUs);
}

/**
 * Exact PCM for [fromUs, endUs). Positions come from block timestamps; any gap
 * (dropped audio) or missing lead-in is filled with silence so the WAV stays
 * aligned with the video timeline.
 */
export function extractPcm(blocks: PcmBlock[], sampleRate: number, fromUs: number, endUs: number): Float32Array {
  const total = Math.max(0, Math.round(((endUs - fromUs) * sampleRate) / 1e6));
  const out = new Float32Array(total);
  for (const b of blocks) {
    const offset = Math.round(((b.ts - fromUs) * sampleRate) / 1e6); // position of b.samples[0] in `out`
    const srcStart = Math.max(0, -offset);
    const dstStart = Math.max(0, offset);
    const n = Math.min(b.samples.length - srcStart, total - dstStart);
    if (n > 0) out.set(b.samples.subarray(srcStart, srcStart + n), dstStart);
  }
  return out;
}

/**
 * Puts one media stream on the shared timeline (worker clock, microseconds).
 *
 * Device timestamps are only trusted for the spacing between consecutive frames;
 * their epoch, clock and even units differ between browsers and devices (a real
 * webcam was observed ~250 s off the microphone). The timeline is anchored to
 * arrival time and corrected so that a frame is never placed after it arrived
 * (capture always precedes arrival). This converges to capture time plus the
 * stream's smallest delivery latency, and recovers from jumps or wrong clocks.
 */
export class StreamClock {
  private lastRaw: number | null = null;
  private lastMapped = -Infinity;

  constructor(private readonly maxLagUs = 1_000_000) {}

  map(rawUs: number, arrivalUs: number): number {
    let t = arrivalUs;
    if (this.lastRaw !== null) {
      const delta = rawUs - this.lastRaw;
      // Use the device's spacing when plausible; otherwise fall back to arrival time.
      if (delta > 0 && delta < 2_000_000) t = this.lastMapped + delta;
    }
    if (t > arrivalUs) t = arrivalUs; // cannot be captured after it arrived
    if (arrivalUs - t > this.maxLagUs) t = arrivalUs; // drifted far behind: resync
    if (t <= this.lastMapped) t = this.lastMapped + 1; // keep strictly increasing
    this.lastRaw = rawUs;
    this.lastMapped = t;
    return t;
  }
}

/** Sample position (continuous stream) that corresponds to timeline time `us`. */
export function sampleIndexAt(blocks: PcmBlock[], us: number, sampleRate: number): number {
  let b = blocks[0];
  for (const x of blocks) {
    if (x.ts <= us) b = x;
    else break;
  }
  return b.index + Math.round(((us - b.ts) * sampleRate) / 1e6);
}

/**
 * `count` consecutive samples of the continuous stream starting at `start`.
 * Samples not in the buffer (pruned or not yet captured) are silence.
 */
export function extractSamples(blocks: PcmBlock[], start: number, count: number): Float32Array {
  const out = new Float32Array(Math.max(0, count));
  for (const b of blocks) {
    const from = Math.max(start, b.index);
    const to = Math.min(start + count, b.index + b.samples.length);
    if (to > from) out.set(b.samples.subarray(from - b.index, to - b.index), from - start);
  }
  return out;
}

/** Converts float samples to 16-bit PCM. */
export function floatToInt16(samples: Float32Array): Int16Array {
  const out = new Int16Array(samples.length);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    out[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
  }
  return out;
}
