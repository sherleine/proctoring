import { expect, it } from "vitest";
import { encodeWavPcm16 } from "../../frontend/src/utils/wav";
import { rmsDb } from "../../frontend/src/utils/audioMath";

it("writes a valid 16 kHz mono PCM16 header and clamps samples", () => {
  const buf = encodeWavPcm16(Float32Array.from([0, 0.5, -1, 2]), 16000);
  const v = new DataView(buf);
  const tag = (o: number) => String.fromCharCode(...new Uint8Array(buf, o, 4));
  expect(tag(0)).toBe("RIFF");
  expect(tag(8)).toBe("WAVE");
  expect(v.getUint16(20, true)).toBe(1); // PCM
  expect(v.getUint16(22, true)).toBe(1); // mono
  expect(v.getUint32(24, true)).toBe(16000);
  expect(v.getUint16(34, true)).toBe(16);
  expect(v.getUint32(40, true)).toBe(8);
  expect(buf.byteLength).toBe(52);
  expect(v.getInt16(46, true)).toBe(16383);
  expect(v.getInt16(48, true)).toBe(-32768);
  expect(v.getInt16(50, true)).toBe(32767); // clamped
});

it("measures full-scale sine RMS at about -3 dBFS", () => {
  const s = Float32Array.from({ length: 16000 }, (_, i) => Math.sin((2 * Math.PI * 1000 * i) / 16000));
  expect(rmsDb(s)).toBeCloseTo(-3.01, 1);
});
