
import logging
import threading
import warnings
from dataclasses import dataclass
from importlib.metadata import version

import numpy as np

from app.core.errors import TransientEventError

log = logging.getLogger(__name__)
SAMPLE_RATE = 16_000
MIN_SPEECH_SECONDS = 0.25  # less than this in total is reported as "no verified speech"


@dataclass(frozen=True)
class VadResult:
    segments: list[tuple[float, float]]  # seconds, ordered
    speech_seconds: float
    speech_ratio: float
    max_probability: float  # highest per-window speech probability (0..1)
    mean_speech_probability: float | None  # mean probability inside the detected segments
    threshold: float

    @property
    def verified(self) -> bool:
        return self.speech_seconds >= MIN_SPEECH_SECONDS


class VadService:
    def __init__(self, threshold: float):
        self.threshold = threshold
        self._model = None
        self._lock = threading.Lock()
        self.model_id = f"silero-vad-{version('silero-vad')}"

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        import torch
        from silero_vad import load_silero_vad

        torch.set_num_threads(max(1, min(4, torch.get_num_threads())))
        with warnings.catch_warnings():  # torch.jit.load deprecation notice (prints a local library path)
            warnings.simplefilter("ignore", FutureWarning)
            self._model = load_silero_vad()  # model file ships inside the package: no download
        log.info("VAD model loaded", extra={"model": self.model_id, "device": "cpu", "threshold": self.threshold})

    def detect(self, samples: np.ndarray) -> VadResult:
        if self._model is None:
            raise TransientEventError("VAD model is not loaded")
        import torch
        from silero_vad import get_speech_timestamps

        try:
            with self._lock, torch.inference_mode():
                audio = torch.from_numpy(samples)
                probabilities = self._model.audio_forward(audio, SAMPLE_RATE).squeeze(0).numpy()
                self._model.reset_states()
                stamps = get_speech_timestamps(audio, self._model, threshold=self.threshold,
                                               sampling_rate=SAMPLE_RATE, min_speech_duration_ms=250,
                                               min_silence_duration_ms=300, speech_pad_ms=30)
        except Exception as exc:  # inference failure: retry later, never guess
            raise TransientEventError(f"VAD inference failed ({type(exc).__name__})") from exc

        duration = len(samples) / SAMPLE_RATE
        segments = [(round(s["start"] / SAMPLE_RATE, 3), round(min(s["end"] / SAMPLE_RATE, duration), 3)) for s in stamps]
        speech = round(sum(e - s for s, e in segments), 3)
        window = 512 / SAMPLE_RATE
        inside = [p for i, p in enumerate(probabilities)
                  if any(s <= i * window < e for s, e in segments)]
        return VadResult(
            segments=segments,
            speech_seconds=speech,
            speech_ratio=round(min(1.0, speech / duration), 3) if duration else 0.0,
            max_probability=round(float(probabilities.max()), 3) if probabilities.size else 0.0,
            mean_speech_probability=round(float(np.mean(inside)), 3) if inside else None,
            threshold=self.threshold,
        )
