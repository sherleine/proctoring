"""faster-whisper transcription, with Whisper's own hallucination guard."""
import logging
import os
import sys
from dataclasses import dataclass

import numpy as np

from app.core.errors import TransientEventError

log = logging.getLogger(__name__)

# Whisper's own hallucination guard: a segment the model itself rates as "probably no speech" and decodes
# with low confidence is dropped rather than reported as words.
NO_SPEECH_PROB_LIMIT = 0.6
LOW_LOGPROB_LIMIT = -1.0


@dataclass(frozen=True)
class TranscriptSegmentData:
    start: float
    end: float
    text: str
    avg_logprob: float | None
    no_speech_prob: float | None


@dataclass(frozen=True)
class TranscriptionData:
    language: str | None
    language_probability: float | None
    segments: list[TranscriptSegmentData]

    @property
    def text(self) -> str:
        return " ".join(s.text for s in self.segments if s.text).strip()


def add_pip_cuda_libraries() -> None:
    """On Windows, CTranslate2 needs cublas64_12.dll. If the nvidia-cublas-cu12 wheel is installed in the venv, make that folder visible to the DLL loader (no system changes)."""
    if sys.platform != "win32":
        return
    try:
        import nvidia.cublas
    except ImportError:
        return
    for base in nvidia.cublas.__path__:
        folder = os.path.join(base, "bin")
        if os.path.isdir(folder):
            os.add_dll_directory(folder)
            os.environ["PATH"] = folder + os.pathsep + os.environ.get("PATH", "")


def cuda_device_count() -> int:
    try:
        import ctranslate2
        return ctranslate2.get_cuda_device_count()
    except Exception:  # CUDA libraries missing or broken
        return 0


class TranscriptionService:
    def __init__(self, model_size: str, device: str, compute_type: str, language: str | None, workers: int = 1,
                 task: str = "transcribe"):
        self.model_size = model_size
        self.requested_device = device
        self.device = device
        self.compute_type = compute_type
        self.language = language
        self._workers = workers
        self.task = task
        self._model = None

    @property
    def model_id(self) -> str:
        return f"faster-whisper:{self.model_size}"

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        from faster_whisper import WhisperModel

        if self.requested_device == "cuda":
            add_pip_cuda_libraries()
        if self.requested_device == "cuda" and cuda_device_count() == 0:
            log.warning("WHISPER_DEVICE=cuda but CTranslate2 found no usable CUDA device; using CPU")
            self.device = "cpu"
            if self.compute_type in ("float16", "int8_float16"):
                self.compute_type = "int8"
        self._model = WhisperModel(self.model_size, device=self.device, compute_type=self.compute_type,
                                   num_workers=self._workers)
        log.info("Transcription model loaded", extra={"model": self.model_id, "device": self.device,
                                                       "computeType": self.compute_type,
                                                       "language": self.language or "auto", "task": self.task})

    def transcribe(self, samples: np.ndarray) -> TranscriptionData:
        if self._model is None:
            raise TransientEventError("Transcription model is not loaded")
        try:
            segments, info = self._model.transcribe(
                samples, language=self.language, task=self.task, beam_size=5, vad_filter=False,
                condition_on_previous_text=False, temperature=0.0)
            raw = list(segments)  # the generator does the actual decoding
        except Exception as exc:
            raise TransientEventError(f"Transcription failed ({type(exc).__name__})") from exc
        return map_segments(raw, info.language, info.language_probability)


def map_segments(raw, language: str | None, language_probability: float | None) -> TranscriptionData:
    """faster-whisper segments -> contract data, in time order, without likely hallucinations."""
    kept = []
    for s in sorted(raw, key=lambda seg: seg.start):
        text = (s.text or "").strip()
        if not text:
            continue
        if s.no_speech_prob is not None and s.avg_logprob is not None \
                and s.no_speech_prob > NO_SPEECH_PROB_LIMIT and s.avg_logprob < LOW_LOGPROB_LIMIT:
            continue
        kept.append(TranscriptSegmentData(
            start=round(max(0.0, float(s.start)), 3), end=round(max(0.0, float(s.end)), 3), text=text,
            avg_logprob=None if s.avg_logprob is None else round(float(s.avg_logprob), 3),
            no_speech_prob=None if s.no_speech_prob is None else round(float(s.no_speech_prob), 3)))
    return TranscriptionData(language=language,
                             language_probability=None if language_probability is None else round(float(language_probability), 3),
                             segments=kept)
