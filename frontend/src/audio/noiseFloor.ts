/**
 * Adaptive background-noise estimate (EWMA in the dB domain):
 *     N_t = alpha * X_t + (1 - alpha) * N_{t-1}
 *
 * Only updated on frames the VAD considers non-speech, so the candidate's own
 * speech is not learned as background. Used for a mild SNR gate at speech
 * onset and reported with every clip; it never removes audio.
 */
export class NoiseFloorEstimator {
  private value: number | null = null;

  constructor(private readonly alpha = 0.05) {}

  get db(): number | null {
    return this.value;
  }

  update(levelDb: number): void {
    this.value = this.value === null ? levelDb : this.alpha * levelDb + (1 - this.alpha) * this.value;
  }

  /** Signal-to-noise ratio of a frame against the current estimate (null until estimated). */
  snr(levelDb: number): number | null {
    return this.value === null ? null : levelDb - this.value;
  }
}
