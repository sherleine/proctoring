"""Adapter contracts.

Each AI capability is accessed through one of these interfaces. Services depend
only on the interface, so a model can be swapped (e.g. YAMNet -> PANNs) by adding
a new adapter and changing an environment variable.

Lifecycle: ``load()`` is called once at application startup; inference methods
are called per clip and must not reload weights.
"""

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any

import numpy as np

# All adapters receive 16 kHz mono float32 audio in [-1, 1].
SAMPLE_RATE = 16000


class ModelAdapter(ABC):
    provider: str
    model_name: str

    def __init__(self) -> None:
        self.loaded = False
        self.load_error: str | None = None

    @abstractmethod
    def load(self) -> None:
        """Load weights. Raise on failure; the registry records the error."""

    def status(self) -> dict[str, Any]:
        return {
            "provider": self.provider,
            "model": self.model_name,
            "loaded": self.loaded,
            "error": self.load_error,
        }


@dataclass
class VadRegion:
    start: float
    end: float


class VadAdapter(ModelAdapter):
    @abstractmethod
    def detect_speech(self, audio: np.ndarray) -> list[VadRegion]: ...


@dataclass
class WordResult:
    start: float
    end: float
    word: str
    probability: float


@dataclass
class SegmentResult:
    start: float
    end: float
    text: str
    avg_logprob: float | None
    no_speech_prob: float | None
    words: list[WordResult] = field(default_factory=list)


@dataclass
class TranscriptionOutput:
    language: str | None
    language_probability: float | None
    segments: list[SegmentResult]


class TranscriptionAdapter(ModelAdapter):
    @abstractmethod
    def transcribe(self, audio: np.ndarray, language: str | None = None) -> TranscriptionOutput: ...


@dataclass
class SpeakerTurnResult:
    speaker: str
    start: float
    end: float


class DiarizationAdapter(ModelAdapter):
    @abstractmethod
    def diarize(self, audio: np.ndarray) -> list[SpeakerTurnResult]: ...


@dataclass
class SoundLabelResult:
    label: str
    score: float
    start: float | None = None
    end: float | None = None


class AudioClassificationAdapter(ModelAdapter):
    @abstractmethod
    def classify(self, audio: np.ndarray, top_k: int = 5) -> list[SoundLabelResult]: ...


class LLMAdapter(ModelAdapter):
    @abstractmethod
    def interpret(self, evidence: dict[str, Any]) -> dict[str, Any]:
        """Send structured (non-audio) evidence; return a structured observation."""
