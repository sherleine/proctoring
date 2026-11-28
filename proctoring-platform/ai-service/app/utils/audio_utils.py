"""Small stateless audio helpers: levels for float32 audio in [-1, 1], and upload conversion to 16 kHz mono WAV."""
import io
import math
import subprocess
import tempfile
from pathlib import Path

import numpy as np
import soundfile as sf

TARGET_SAMPLE_RATE = 16_000

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


def needs_conversion(data: bytes, filename: str) -> bool:
    """True unless the upload already is a 16 kHz mono PCM16 .wav."""
    if Path(filename or "").suffix.lower() != ".wav":
        return True
    try:
        info = sf.info(io.BytesIO(data))
    except Exception:
        return True
    return info.samplerate != TARGET_SAMPLE_RATE or info.channels != 1 or info.subtype != "PCM_16"


def to_16k_mono_wav(data: bytes, filename: str) -> bytes:
    """ffmpeg converts whatever was uploaded (wav of any rate/channels, mp3, mp4, mov, webm ...) into 16 kHz mono
    PCM16 WAV. Raises FileNotFoundError when ffmpeg is not on PATH, ValueError when the file is not audio/video."""
    suffix = Path(filename or "").suffix or ".bin"
    with tempfile.TemporaryDirectory() as tmp:
        src, dst = Path(tmp) / f"input{suffix}", Path(tmp) / "output.wav"
        src.write_bytes(data)
        try:
            subprocess.run(["ffmpeg", "-y", "-i", str(src), "-ar", str(TARGET_SAMPLE_RATE), "-ac", "1", "-f", "wav", str(dst)],
                           check=True, capture_output=True, timeout=120)
        except subprocess.CalledProcessError as exc:
            detail = exc.stderr.decode(errors="replace")[-500:] if exc.stderr else str(exc)
            raise ValueError(f"Could not decode the uploaded file as audio/video: {detail}") from exc
        return dst.read_bytes()


def wav_duration_seconds(data: bytes) -> float:
    """Raises ValueError when the bytes are not a readable WAV."""
    try:
        return sf.info(io.BytesIO(data)).duration
    except Exception as exc:
        raise ValueError(f"Uploaded file is not valid audio: {exc}") from exc
