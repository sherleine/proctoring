/**
 * Fixed-capacity ring buffer of audio samples. Keeps the most recent `capacity`
 * samples; older samples are overwritten. Used for the pre-speech buffer and for
 * the "replay last N seconds" capture check.
 */
export class RollingBuffer {
  private readonly data: Float32Array;
  private writePos = 0;
  private size = 0;

  constructor(readonly capacity: number) {
    if (!Number.isInteger(capacity) || capacity <= 0) throw new Error("capacity must be a positive integer");
    this.data = new Float32Array(capacity);
  }

  get length(): number {
    return this.size;
  }

  push(samples: Float32Array): void {
    let src = samples;
    if (src.length >= this.capacity) {
      src = src.subarray(src.length - this.capacity);
    }
    const first = Math.min(src.length, this.capacity - this.writePos);
    this.data.set(src.subarray(0, first), this.writePos);
    if (first < src.length) this.data.set(src.subarray(first), 0);
    this.writePos = (this.writePos + src.length) % this.capacity;
    this.size = Math.min(this.capacity, this.size + src.length);
  }

  /** Copy of the buffered samples in chronological order. */
  read(): Float32Array {
    const out = new Float32Array(this.size);
    const start = (this.writePos - this.size + this.capacity) % this.capacity;
    const first = Math.min(this.size, this.capacity - start);
    out.set(this.data.subarray(start, start + first), 0);
    if (first < this.size) out.set(this.data.subarray(0, this.size - first), first);
    return out;
  }

  /** Returns the buffered samples and empties the buffer. */
  drain(): Float32Array {
    const out = this.read();
    this.clear();
    return out;
  }

  clear(): void {
    this.size = 0;
    this.writePos = 0;
  }
}
