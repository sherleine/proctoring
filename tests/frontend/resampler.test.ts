import { describe, expect, it } from "vitest";
import { StreamingResampler } from "../../frontend/src/audio/resampler";

function sine(freq: number, rate: number, seconds: number): Float32Array {
  const out = new Float32Array(Math.round(rate * seconds));
  for (let i = 0; i < out.length; i++) out[i] = Math.sin((2 * Math.PI * freq * i) / rate);
  return out;
}

/** Feeds the signal in 128-sample render quanta, like an AudioWorklet does. */
function resampleInQuanta(r: StreamingResampler, input: Float32Array): Float32Array {
  const chunks: number[] = [];
  for (let i = 0; i < input.length; i += 128) chunks.push(...r.process(input.subarray(i, i + 128)));
  return Float32Array.from(chunks);
}

function rms(x: Float32Array, from = 0): number {
  let s = 0;
  for (let i = from; i < x.length; i++) s += x[i] * x[i];
  return Math.sqrt(s / (x.length - from));
}

describe.each([48000, 44100])("StreamingResampler %i Hz -> 16 kHz", (inRate) => {
  it("produces the right number of samples", () => {
    const out = resampleInQuanta(new StreamingResampler(inRate, 16000), sine(440, inRate, 2));
    // Output lags by the filter half-width; everything else must be present.
    expect(out.length).toBeGreaterThan(16000 * 2 - 40);
    expect(out.length).toBeLessThanOrEqual(16000 * 2);
  });

  it("passes a 1 kHz tone at unity gain", () => {
    const out = resampleInQuanta(new StreamingResampler(inRate, 16000), sine(1000, inRate, 1));
    expect(rms(out, 200)).toBeCloseTo(Math.SQRT1_2, 2);
  });

  it("removes content above the new Nyquist frequency (anti-aliasing)", () => {
    const out = resampleInQuanta(new StreamingResampler(inRate, 16000), sine(12000, inRate, 1));
    expect(rms(out, 200)).toBeLessThan(0.01); // > 37 dB attenuation
  });

  it("keeps the frequency (no pitch shift)", () => {
    const out = resampleInQuanta(new StreamingResampler(inRate, 16000), sine(500, inRate, 1));
    let crossings = 0;
    for (let i = 201; i < out.length; i++) if (out[i - 1] < 0 && out[i] >= 0) crossings++;
    const seconds = (out.length - 200) / 16000;
    expect(crossings / seconds).toBeGreaterThan(495);
    expect(crossings / seconds).toBeLessThan(505);
  });
});

it("passes through when rates match", () => {
  const input = sine(440, 16000, 0.1);
  expect(new StreamingResampler(16000, 16000).process(input)).toBe(input);
});
