"""Small shared test helpers: in-memory WAV encoding and a minimal fake Redis client."""
import io

import numpy as np
import soundfile as sf

SR = 16_000


def wav_bytes(samples: np.ndarray, sample_rate: int = SR, subtype: str = "PCM_16") -> bytes:
    buffer = io.BytesIO()
    sf.write(buffer, samples, sample_rate, format="WAV", subtype=subtype)
    return buffer.getvalue()


class FakeRedis:
    """Just enough of redis.Redis for VoiceReferenceStore: get() and set(nx=, ex=). TTLs are recorded, not enforced."""

    def __init__(self):
        self.data: dict[str, str] = {}
        self.ttls: dict[str, int] = {}

    def get(self, key):
        return self.data.get(key)

    def set(self, key, value, nx: bool = False, ex: int | None = None):
        if nx and key in self.data:
            return None  # redis-py returns None when NX blocks the write
        self.data[key] = value
        if ex is not None:
            self.ttls[key] = ex
        return True
