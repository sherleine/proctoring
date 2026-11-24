import { describe, expect, it } from "vitest";
import { StreamingResampler } from "../src/audio/resampler";
import { encodeWav, validateWav } from "../src/audio/wav";

describe("encodeWav / validateWav", () => {
  it("writes a 16 kHz mono 16-bit PCM WAV with the right size and duration", () => {
    const samples = new Float32Array(16000 * 2).map((_, i) => Math.sin((2 * Math.PI * 440 * i) / 16000) * 0.5);
    const wav = encodeWav(samples);
    expect(wav.byteLength).toBe(44 + samples.length * 2);
    const info = validateWav(wav);
    expect(info).toEqual({ sampleRate: 16000, channels: 1, bitsPerSample: 16, dataBytes: samples.length * 2, durationSeconds: 2 });
    const view = new DataView(wav);
    expect(String.fromCharCode(...new Uint8Array(wav, 0, 4))).toBe("RIFF");
    expect(view.getUint32(4, true)).toBe(wav.byteLength - 8);
    expect(view.getUint32(28, true)).toBe(32000); // byte rate
  });

  it("clips out-of-range samples instead of wrapping around", () => {
    const view = new DataView(encodeWav(Float32Array.from([2, -2, 1, -1, 0])));
    expect([0, 1, 2, 3, 4].map((i) => view.getInt16(44 + i * 2, true))).toEqual([32767, -32768, 32767, -32768, 0]);
  });

  it("rejects anything that is not 16 kHz mono 16-bit PCM", () => {
    const good = encodeWav(new Float32Array(1600));
    const patch = (offset: number, bytes: number, value: number) => {
      const copy = good.slice(0);
      const v = new DataView(copy);
      if (bytes === 2) v.setUint16(offset, value, true);
      else v.setUint32(offset, value, true);
      return copy;
    };
    expect(() => validateWav(patch(22, 2, 2))).toThrow(/mono/);
    expect(() => validateWav(patch(24, 4, 48000))).toThrow(/16000/);
    expect(() => validateWav(patch(34, 2, 8))).toThrow(/16-bit/);
    expect(() => validateWav(patch(20, 2, 3))).toThrow(/PCM/);
    expect(() => validateWav(new TextEncoder().encode("not a wav file, just text....................................").buffer as ArrayBuffer)).toThrow(/RIFF/);
    expect(() => validateWav(good.slice(0, 100))).toThrow(/truncated/);
    expect(() => validateWav(encodeWav(new Float32Array(0)))).toThrow(/no samples/);
  });
});

describe("StreamingResampler", () => {
  const tone = (rate: number, seconds: number, freq: number) =>
    Float32Array.from({ length: rate * seconds }, (_, i) => Math.sin((2 * Math.PI * freq * i) / rate));

  function dominantFrequency(x: Float32Array, rate: number): number {
    let crossings = 0;
    for (let i = 1; i < x.length; i++) if (x[i - 1] < 0 && x[i] >= 0) crossings++;
    return (crossings / x.length) * rate;
  }

  function process(rate: number, input: Float32Array): Float32Array {
    const r = new StreamingResampler(rate, 16000);
    const parts: number[] = [];
    for (let i = 0; i < input.length; i += 128) parts.push(...r.process(input.subarray(i, i + 128))); // like the worklet
    return Float32Array.from(parts);
  }

  it.each([48000, 44100])("converts %i Hz to 16 kHz keeping length and pitch", (rate) => {
    const out = process(rate, tone(rate, 2, 440));
    expect(Math.abs(out.length - 32000)).toBeLessThan(40); // filter delay only
    expect(dominantFrequency(out.subarray(1000), 16000)).toBeCloseTo(440, -1);
  });

  it("removes content above 8 kHz instead of aliasing it into the speech band", () => {
    const out = process(48000, tone(48000, 1, 12000)).subarray(500);
    const rms = Math.sqrt(out.reduce((s, v) => s + v * v, 0) / out.length);
    expect(rms).toBeLessThan(0.05);
  });

  it("passes 16 kHz through unchanged", () => {
    const input = tone(16000, 0.1, 300);
    expect(new StreamingResampler(16000, 16000).process(input)).toBe(input);
  });
});
