"""CAM++ voice embeddings (sherpa-onnx), used to tell whether two speakers are the same voice."""
import hashlib
import logging
import threading
from pathlib import Path

import numpy as np

from app.core.errors import TransientEventError
from app.utils.audio_utils import SAMPLE_RATE

log = logging.getLogger(__name__)


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
