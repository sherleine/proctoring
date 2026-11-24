export const SILENCE_DB = -100;

/** Root-mean-square level in dBFS (0 dBFS = full scale sine peak). */
export function rmsDb(samples: Float32Array): number {
  if (samples.length === 0) return SILENCE_DB;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
  const rms = Math.sqrt(sum / samples.length);
  return rms > 0 ? Math.max(SILENCE_DB, 20 * Math.log10(rms)) : SILENCE_DB;
}

export function peakDb(samples: Float32Array): number {
  let peak = 0;
  for (let i = 0; i < samples.length; i++) {
    const a = Math.abs(samples[i]);
    if (a > peak) peak = a;
  }
  return peak > 0 ? Math.max(SILENCE_DB, 20 * Math.log10(peak)) : SILENCE_DB;
}
