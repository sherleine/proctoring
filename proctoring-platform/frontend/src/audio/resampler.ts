/**
 * Streaming windowed-sinc resampler (band-limited), used inside the AudioWorklet
 * to convert the microphone's native rate (usually 48 kHz or 44.1 kHz) to 16 kHz.
 *
 * Linear interpolation or plain decimation would alias energy above 8 kHz into
 * the speech band; the low-pass sinc kernel removes it before resampling.
 * Works for non-integer ratios (44.1k -> 16k) and keeps state across calls so
 * chunk boundaries do not produce clicks.
 */

const TABLE_RESOLUTION = 128; // kernel samples per input sample

export class StreamingResampler {
  readonly inputRate: number;
  readonly outputRate: number;
  private readonly ratio: number;
  private readonly halfWidth: number;
  private readonly table: Float32Array;
  private buf: Float32Array;
  private bufLen = 0;
  private bufStart = 0; // absolute input index of buf[0]
  private nextOut = 0; // absolute (fractional) input position of the next output sample
  private out: Float32Array;

  constructor(inputRate: number, outputRate: number, zeroCrossings = 8) {
    if (inputRate <= 0 || outputRate <= 0) throw new Error("Sample rates must be positive");
    this.inputRate = inputRate;
    this.outputRate = outputRate;
    this.ratio = inputRate / outputRate;
    // Cutoff in cycles per input sample, slightly below Nyquist of the lower rate.
    const cutoff = 0.5 * Math.min(1, outputRate / inputRate) * 0.92;
    this.halfWidth = Math.ceil(zeroCrossings / (2 * cutoff));
    const n = this.halfWidth * TABLE_RESOLUTION;
    this.table = new Float32Array(2 * n + 1);
    for (let i = -n; i <= n; i++) {
      const x = i / TABLE_RESOLUTION;
      const arg = 2 * cutoff * x;
      const sinc = x === 0 ? 1 : Math.sin(Math.PI * arg) / (Math.PI * arg);
      // Blackman window
      const w = 0.42 + 0.5 * Math.cos((Math.PI * x) / this.halfWidth) + 0.08 * Math.cos((2 * Math.PI * x) / this.halfWidth);
      this.table[i + n] = 2 * cutoff * sinc * w;
    }
    this.buf = new Float32Array(4096);
    this.out = new Float32Array(1024);
  }

  get passthrough(): boolean {
    return this.inputRate === this.outputRate;
  }

  /** Returns resampled samples. The returned view is only valid until the next call. */
  process(input: Float32Array): Float32Array {
    if (this.passthrough) return input;
    this.append(input);

    const maxOut = Math.ceil(input.length / this.ratio) + 2;
    if (this.out.length < maxOut) this.out = new Float32Array(maxOut * 2);

    const hw = this.halfWidth;
    const n = hw * TABLE_RESOLUTION;
    const bufEnd = this.bufStart + this.bufLen;
    let count = 0;
    while (Math.floor(this.nextOut) + hw < bufEnd) {
      const center = this.nextOut;
      const kFirst = Math.ceil(center - hw);
      const kLast = Math.floor(center + hw);
      let acc = 0;
      let wsum = 0;
      for (let k = kFirst; k <= kLast; k++) {
        const w = this.table[Math.round((k - center) * TABLE_RESOLUTION) + n];
        wsum += w;
        const idx = k - this.bufStart;
        if (idx >= 0) acc += this.buf[idx] * w; // k < 0 only during warm-up: treat as silence
      }
      this.out[count++] = wsum !== 0 ? acc / wsum : 0;
      this.nextOut += this.ratio;
    }

    // Drop input samples that no future output needs.
    const keepFrom = Math.floor(this.nextOut - hw);
    const drop = keepFrom - this.bufStart;
    if (drop > 0) {
      this.buf.copyWithin(0, drop, this.bufLen);
      this.bufLen -= drop;
      this.bufStart += drop;
    }
    return this.out.subarray(0, count);
  }

  private append(input: Float32Array): void {
    if (this.bufLen + input.length > this.buf.length) {
      const bigger = new Float32Array(Math.max(this.buf.length * 2, this.bufLen + input.length));
      bigger.set(this.buf.subarray(0, this.bufLen));
      this.buf = bigger;
    }
    this.buf.set(input, this.bufLen);
    this.bufLen += input.length;
  }
}
