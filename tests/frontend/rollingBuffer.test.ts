import { describe, expect, it } from "vitest";
import { RollingBuffer } from "../../frontend/src/audio/rollingBuffer";

const seq = (from: number, n: number) => Float32Array.from({ length: n }, (_, i) => from + i);

describe("RollingBuffer", () => {
  it("returns samples in order before wrapping", () => {
    const b = new RollingBuffer(10);
    b.push(seq(0, 4));
    b.push(seq(4, 3));
    expect(Array.from(b.read())).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it("keeps only the most recent samples after wrapping", () => {
    const b = new RollingBuffer(5);
    for (let i = 0; i < 4; i++) b.push(seq(i * 3, 3)); // 0..11
    expect(Array.from(b.read())).toEqual([7, 8, 9, 10, 11]);
  });

  it("handles a push larger than capacity", () => {
    const b = new RollingBuffer(4);
    b.push(seq(0, 10));
    expect(Array.from(b.read())).toEqual([6, 7, 8, 9]);
  });

  it("drain empties the buffer", () => {
    const b = new RollingBuffer(4);
    b.push(seq(0, 2));
    expect(Array.from(b.drain())).toEqual([0, 1]);
    expect(b.length).toBe(0);
    b.push(seq(5, 1));
    expect(Array.from(b.read())).toEqual([5]);
  });
});
