"""Voice embeddings with CAM++ (used to tell whether two diarized speakers have different voices).

Model: 3D-Speaker CAM++ trained on VoxCeleb (ModelScope `iic/speech_campplus_sv_en_voxceleb_16k`, weights
Apache-2.0), as the ONNX export published by sherpa-onnx, run with sherpa-onnx (Apache-2.0) on the CPU.
Loaded once and reused; calls are serialized.

The service never identifies anybody: it only turns a stretch of one speaker's speech into a 512-value voice
embedding. The pipeline compares embeddings of the speakers inside the same clip with each other (cosine
similarity, thresholds SPEAKER_SAME_THRESHOLD / SPEAKER_DIFFERENT_THRESHOLD). Similarities are raw model scores,
not probabilities.
"""
from __future__ import annotations

import hashlib
import logging
import threading
from pathlib import Path

import numpy as np

from app.core.errors import TransientEventError

log = logging.getLogger(__name__)
SAMPLE_RATE = 16_000


class SpeakerService:
    def __init__(self, model_path: Path, num_threads: int = 2):
        self.model_path = Path(model_path)
        self.num_threads = num_threads
        self._extractor = None
        self._lock = threading.Lock()
        self.model_id = f"campplus-voxceleb-en:{self.model_path.name}"

    @property
    def loaded(self) -> bool:
        return self._extractor is not None

    def load(self) -> None:
        import sherpa_onnx

        if not self.model_path.is_file():
            raise FileNotFoundError("speaker model file is missing (run scripts/fetch_models.py)")
        digest = hashlib.sha256(self.model_path.read_bytes()).hexdigest()
        config = sherpa_onnx.SpeakerEmbeddingExtractorConfig(model=str(self.model_path), num_threads=self.num_threads,
                                                             debug=False, provider="cpu")
        if not config.validate():
            raise RuntimeError("invalid speaker model configuration")
        self._extractor = sherpa_onnx.SpeakerEmbeddingExtractor(config)
        self.model_id = f"campplus-voxceleb-en:sha256-{digest[:12]}"
        log.info("Speaker model loaded", extra={"model": self.model_id, "dim": self._extractor.dim, "device": "cpu"})

    def embed(self, samples: np.ndarray) -> np.ndarray:
        """L2-normalised voice embedding of a mono 16 kHz float32 signal (ValueError if it is too short)."""
        if self._extractor is None:
            raise TransientEventError("speaker model is not loaded")
        try:
            with self._lock:
                stream = self._extractor.create_stream()
                stream.accept_waveform(sample_rate=SAMPLE_RATE, waveform=np.ascontiguousarray(samples, dtype=np.float32))
                stream.input_finished()
                if not self._extractor.is_ready(stream):
                    raise ValueError("audio too short for an embedding")
                vector = np.asarray(self._extractor.compute(stream), dtype=np.float32)
        except ValueError:
            raise
        except Exception as exc:
            raise TransientEventError(f"speaker embedding failed ({type(exc).__name__})") from exc
        norm = float(np.linalg.norm(vector))
        if not np.isfinite(norm) or norm == 0.0:
            raise ValueError("degenerate embedding")
        return vector / norm
