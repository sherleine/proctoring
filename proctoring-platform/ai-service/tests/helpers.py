"""Test helpers: synthetic WAV fixtures, a temporary audio store, contract-valid event entries
built exactly like the backend builds them, and fake models for tests that must not load real ones."""
import hashlib
import io
import json
import secrets
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np
import pytest
import soundfile as sf

import app  # noqa: F401  (applies the OpenBLAS thread limit before numpy-heavy imports)
from app.services.transcription_service import TranscriptionData, TranscriptSegmentData
from app.services.vad_service import VadResult

FIXTURES = Path(__file__).parent / "fixtures"
SR = 16_000


# ------------------------------------------------------------------ audio fixtures

def wav_bytes(samples: np.ndarray, sample_rate: int = SR, subtype: str = "PCM_16") -> bytes:
    buffer = io.BytesIO()
    sf.write(buffer, samples, sample_rate, format="WAV", subtype=subtype)
    return buffer.getvalue()


def speech_samples() -> np.ndarray:
    """Real (text-to-speech) English speech, 16 kHz mono: 'Can you tell me the answer to question number three?'"""
    samples, rate = sf.read(FIXTURES / "speech_16k_mono.wav", dtype="float32")
    assert rate == SR
    return samples


def silence(seconds: float = 2.0) -> np.ndarray:
    return np.zeros(int(SR * seconds), dtype=np.float32)


def noise(seconds: float = 2.0, level: float = 0.05, seed: int = 1) -> np.ndarray:
    return (np.random.default_rng(seed).standard_normal(int(SR * seconds)) * level).astype(np.float32)


def tone(seconds: float = 1.0, freq: float = 440.0, level: float = 0.3) -> np.ndarray:
    t = np.arange(int(SR * seconds)) / SR
    return (level * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def quiet_speech() -> np.ndarray:
    return (speech_samples() * 0.02).astype(np.float32)  # about -40 dBFS peak


def noisy_speech() -> np.ndarray:
    s = speech_samples()
    return np.clip(s + noise(len(s) / SR, level=0.03), -1, 1).astype(np.float32)


INVALID_WAV = b"this is not a wav file at all" * 10


def corrupted_wav() -> bytes:
    data = bytearray(wav_bytes(speech_samples()))
    data[8:12] = b"XXXX"  # "WAVE" marker destroyed
    return bytes(data[:600])


# ------------------------------------------------------------------ store + messages

def new_session_id() -> str:
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    return "ses_" + "".join(secrets.choice(alphabet) for _ in range(26))


def put_audio(root: Path, data: bytes, session_id: str | None = None) -> tuple[str, str]:
    """Stores bytes like the backend does: {root}/{sessionId}/{32 hex}.wav. Returns (sessionId, key)."""
    session_id = session_id or new_session_id()
    key = f"{session_id}/{secrets.token_hex(16)}.wav"
    path = root / key
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return session_id, key


def iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def event_entry(session_id: str, key: str, data: bytes, event_id: str | None = None, duration: float = 2.0,
                event_type: str = "speech", speaker_reference: dict | None = None) -> dict[str, str]:
    """A contract-valid `audio.event.submitted` stream entry (the 8 envelope fields)."""
    event_id = event_id or str(uuid.uuid4())
    start = datetime(2026, 11, 23, 10, 0, 0, tzinfo=timezone.utc)
    sha = hashlib.sha256(data).hexdigest()
    payload = {
        "sessionId": session_id, "candidateId": "cand-1", "examId": "exam-1",
        "event": {
            "schemaVersion": "1.0", "eventId": event_id, "eventType": event_type, "sequenceNumber": 0,
            "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
            "startedAt": iso(start), "endedAt": iso(start + timedelta(seconds=duration)),
            "speechStartedAt": iso(start + timedelta(seconds=0.5)), "durationSeconds": duration, "preEventSeconds": 0.5,
            "trigger": {"type": "enrolment" if event_type == "enrolment" else "vad", "model": "silero-vad-v5", "startThreshold": 0.5, "endThreshold": 0.35,
                        "maxProbability": 0.97, "speechSeconds": 1.0, "noiseFloorDbfs": -60.0},
            "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": 16000, "channels": 1,
                      "sizeBytes": len(data), "sha256": sha},
            "video": {"recordedLocally": True, "uploaded": False},
        },
        "audioRef": {"storage": "file", "uri": f"file:///backend-only/{key}", "sha256": sha,
                     "sizeBytes": len(data), "contentType": "audio/wav", "key": key},
    }
    if speaker_reference is not None:
        payload["speakerReference"] = speaker_reference
    return {"messageType": "audio.event.submitted", "schemaVersion": "1", "messageId": str(uuid.uuid4()),
            "correlationId": event_id, "sessionId": session_id, "eventId": event_id,
            "producedAt": iso(datetime.now(timezone.utc)), "payload": json.dumps(payload)}


# ------------------------------------------------------------------ fake models (no download, deterministic)

class FakeVad:
    """Speech = any 512-sample window above an amplitude threshold. Records what it was given."""
    model_id = "fake-vad"
    loaded = True
    threshold = 0.5

    def __init__(self, fail: Exception | None = None):
        self.fail = fail
        self.calls: list[np.ndarray] = []

    def detect(self, samples: np.ndarray) -> VadResult:
        self.calls.append(samples)
        if self.fail:
            raise self.fail
        windows = [(i / SR, (i + 512) / SR) for i in range(0, len(samples), 512)
                   if np.max(np.abs(samples[i:i + 512])) > 0.1]
        segments = [(round(windows[0][0], 3), round(min(windows[-1][1], len(samples) / SR), 3))] if windows else []
        speech = round(sum(e - s for s, e in segments), 3)
        return VadResult(segments, speech, round(speech / (len(samples) / SR), 3), 0.9 if windows else 0.05,
                         0.9 if windows else None, 0.5)


class FakeTranscriber:
    model_id = "faster-whisper:fake"
    loaded = True
    device = "cpu"
    compute_type = "int8"

    def __init__(self, text: str = "hello world", fail: Exception | None = None):
        self.text = text
        self.fail = fail
        self.calls = 0

    def transcribe(self, samples: np.ndarray) -> TranscriptionData:
        self.calls += 1
        if self.fail:
            raise self.fail
        return TranscriptionData("en", 0.99, [TranscriptSegmentData(0.1, len(samples) / SR, self.text, -0.2, 0.01)])
