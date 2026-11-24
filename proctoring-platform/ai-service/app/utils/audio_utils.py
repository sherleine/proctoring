"""Small numeric helpers for float32 audio in [-1, 1]."""
import math

import numpy as np

SILENCE_FLOOR_DBFS = -120.0  # reported instead of -infinity for digital silence (JSON has no infinity)
QUIET_PEAK_DBFS = -30.0  # quieter recordings are amplified (in memory only) before the models see them
MAX_GAIN_DB = 30.0  # never amplify more than this, so background noise is not blown up


def to_dbfs(amplitude: float) -> float:
    if amplitude <= 0:
        return SILENCE_FLOOR_DBFS
    return max(SILENCE_FLOOR_DBFS, round(20 * math.log10(amplitude), 1))


def peak_dbfs(samples: np.ndarray) -> float:
    return to_dbfs(float(np.max(np.abs(samples)))) if samples.size else SILENCE_FLOOR_DBFS


def rms_dbfs(samples: np.ndarray) -> float:
    return to_dbfs(float(np.sqrt(np.mean(np.square(samples, dtype=np.float64))))) if samples.size else SILENCE_FLOOR_DBFS


def clipped_ratio(samples: np.ndarray, limit: float = 0.999) -> float:
    return float(np.mean(np.abs(samples) >= limit)) if samples.size else 0.0


def normalize_quiet(samples: np.ndarray) -> tuple[np.ndarray, float]:
    """Returns (samples for the models, applied gain in dB). Audio at a normal level is returned unchanged;
    only quiet audio is raised to a -3 dBFS peak, by at most MAX_GAIN_DB. Digital silence is left alone."""
    peak = peak_dbfs(samples)
    if peak <= SILENCE_FLOOR_DBFS or peak >= QUIET_PEAK_DBFS:
        return samples, 0.0
    gain_db = min(MAX_GAIN_DB, -3.0 - peak)
    return (samples * (10 ** (gain_db / 20))).astype(np.float32), round(gain_db, 1)
