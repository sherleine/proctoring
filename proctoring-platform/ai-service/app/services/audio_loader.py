"""Reads an event's WAV from the shared audio store and turns it into numbers.

Safety rules:
  * only `audioRef.key` is used (the backend's absolute `uri` is ignored); the key must match the backend's
    storage-key pattern and the resolved path must stay inside AUDIO_STORE_PATH (no path traversal);
  * size and SHA-256 must match the message, so the analysed audio is exactly what was uploaded;
  * the file is opened read-only and never modified;
  * paths never appear in errors or logs - only the key.
"""
import hashlib
import io
import re
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import soundfile as sf

from app.core.errors import PermanentEventError
from app.schemas.audio_event import AudioRef

KEY_PATTERN = re.compile(r"^ses_[A-Za-z0-9]{20,36}/[a-f0-9]{32}\.wav$")
SAMPLE_RATE = 16_000
MAX_BYTES = 50_000_000


@dataclass(frozen=True)
class LoadedAudio:
    samples: np.ndarray  # float32, mono, [-1, 1], original timing (no resampling)
    sample_rate: int

    @property
    def duration_seconds(self) -> float:
        return len(self.samples) / self.sample_rate


class AudioStore:
    """Read-only access to the local/shared audio directory."""

    def __init__(self, root: Path):
        self._root = root.resolve()

    def is_available(self) -> bool:
        return self._root.is_dir()

    def resolve(self, key: str) -> Path:
        if not KEY_PATTERN.fullmatch(key or ""):
            raise PermanentEventError("INVALID_AUDIO_REFERENCE", "audioRef.key does not match the storage key format")
        path = (self._root / key).resolve()
        if not path.is_relative_to(self._root):
            raise PermanentEventError("INVALID_AUDIO_REFERENCE", "audioRef.key points outside the audio store")
        return path

    def read(self, ref: AudioRef) -> bytes:
        path = self.resolve(ref.key or "")
        if not path.is_file():
            raise PermanentEventError("AUDIO_NOT_FOUND", f"Audio '{ref.key}' was not found in the audio store")
        try:
            size = path.stat().st_size
            if size != ref.sizeBytes or size > MAX_BYTES:
                raise PermanentEventError("AUDIO_SIZE_MISMATCH", f"Audio '{ref.key}' has {size} bytes, the message says {ref.sizeBytes}")
            data = path.read_bytes()
        except OSError as exc:
            raise PermanentEventError("AUDIO_NOT_READABLE", f"Audio '{ref.key}' could not be read ({type(exc).__name__})") from exc
        if hashlib.sha256(data).hexdigest() != ref.sha256:
            raise PermanentEventError("AUDIO_CHECKSUM_MISMATCH", f"Audio '{ref.key}' does not match its SHA-256")
        return data


def decode_wav(data: bytes) -> LoadedAudio:
    """Validates the upload format (WAV, 16 kHz, mono, 16-bit PCM) and returns float32 samples."""
    try:
        with sf.SoundFile(io.BytesIO(data)) as f:
            if f.format != "WAV" or f.subtype != "PCM_16":
                raise PermanentEventError("INVALID_AUDIO", f"Expected 16-bit PCM WAV, got {f.format}/{f.subtype}")
            if f.samplerate != SAMPLE_RATE or f.channels != 1:
                raise PermanentEventError("INVALID_AUDIO", f"Expected 16 kHz mono, got {f.samplerate} Hz / {f.channels} channels")
            samples = f.read(dtype="float32", always_2d=False)
    except PermanentEventError:
        raise
    except (sf.LibsndfileError, RuntimeError, ValueError) as exc:
        raise PermanentEventError("INVALID_AUDIO", "The file is not a readable WAV") from exc
    if samples.size == 0:
        raise PermanentEventError("EMPTY_AUDIO", "The WAV contains no samples")
    if not np.all(np.isfinite(samples)):
        raise PermanentEventError("INVALID_AUDIO", "The WAV contains invalid sample values")
    return LoadedAudio(samples=np.ascontiguousarray(samples, dtype=np.float32), sample_rate=SAMPLE_RATE)
