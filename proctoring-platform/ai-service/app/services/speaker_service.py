"""Candidate voice reference (enrolment) and voice comparison with CAM++ speaker embeddings.

Model: 3D-Speaker CAM++ trained on VoxCeleb (ModelScope `iic/speech_campplus_sv_en_voxceleb_16k`, weights
Apache-2.0), as the ONNX export published by sherpa-onnx, run with sherpa-onnx (Apache-2.0) on the CPU.
Loaded once and reused; calls are serialized.

What it does - and does not - do:
  * enrolment: builds ONE reference embedding from 10-20 s of the candidate's speech, after quality checks;
    an unusable sample is reported as `rejected` or `uncertain`, never silently accepted.
  * comparison: cosine similarity between a speech region and the reference, mapped to
    LIKELY_CANDIDATE / LIKELY_NON_CANDIDATE / UNCERTAIN with two thresholds. Regions that are too short are
    UNCERTAIN. Similarities are raw model scores, NOT probabilities.
  * it never decides anything about misconduct.
"""
from __future__ import annotations

import hashlib
import logging
import threading
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from app.core.errors import TransientEventError

log = logging.getLogger(__name__)
SAMPLE_RATE = 16_000

LIKELY_CANDIDATE = "LIKELY_CANDIDATE"
LIKELY_NON_CANDIDATE = "LIKELY_NON_CANDIDATE"
UNCERTAIN = "UNCERTAIN"


@dataclass(frozen=True)
class SpeakerThresholds:
    """Cosine-similarity thresholds. `same`: at or above -> LIKELY_CANDIDATE; `different`: at or below ->
    LIKELY_NON_CANDIDATE; in between -> UNCERTAIN. Calibrated on recordings (see docs), not on papers."""
    same: float
    different: float
    min_segment_seconds: float = 1.5

    def __post_init__(self) -> None:
        if not -1.0 <= self.different < self.same <= 1.0:
            raise ValueError("speaker thresholds must satisfy -1 <= different < same <= 1")


@dataclass(frozen=True)
class EnrolmentConfig:
    min_speech_seconds: float = 8.0   # below: rejected (too little speech for a stable reference)
    target_speech_seconds: float = 10.0
    max_clipped_ratio: float = 0.01   # >= 1 % of samples at full scale: rejected (distorted)
    min_speech_rms_dbfs: float = -45.0  # quieter speech: uncertain
    window_seconds: float = 3.0
    hop_seconds: float = 1.5
    min_self_consistency: float = 0.55  # mean window-to-reference similarity; lower: uncertain (noise/other voices)
    max_different_pair_ratio: float = 0.10  # share of window pairs scoring "different speaker"; higher: uncertain


@dataclass
class Enrolment:
    status: str  # accepted | uncertain | rejected
    reason: str | None
    speech_seconds: float
    windows: int
    self_consistency: float | None
    min_window_similarity: float | None
    speech_rms_dbfs: float | None
    clipped_ratio: float
    embedding: list[float] | None
    model: str

    def to_dict(self) -> dict:
        return {"status": self.status, "reason": self.reason, "speechSeconds": round(self.speech_seconds, 2),
                "windows": self.windows,
                "selfConsistency": None if self.self_consistency is None else round(self.self_consistency, 4),
                "minWindowSimilarity": None if self.min_window_similarity is None else round(self.min_window_similarity, 4),
                "speechRmsDbfs": None if self.speech_rms_dbfs is None else round(self.speech_rms_dbfs, 1),
                "clippedRatio": round(self.clipped_ratio, 5),
                "embedding": self.embedding, "embeddingModel": self.model}


@dataclass
class Comparison:
    start: float
    end: float
    similarity: float | None
    decision: str
    reason: str | None = None
    extra: dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        return {"start": round(self.start, 3), "end": round(self.end, 3),
                "similarity": None if self.similarity is None else round(self.similarity, 4),
                "decision": self.decision, "reason": self.reason, **self.extra}


class SpeakerService:
    def __init__(self, model_path: Path, thresholds: SpeakerThresholds, enrolment: EnrolmentConfig | None = None,
                 num_threads: int = 2):
        self.model_path = Path(model_path)
        self.thresholds = thresholds
        self.enrolment_config = enrolment or EnrolmentConfig()
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

    # ------------------------------------------------------------------ embeddings

    def embed(self, samples: np.ndarray) -> np.ndarray:
        """L2-normalised embedding of a mono 16 kHz float32 signal."""
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

    # ------------------------------------------------------------------ enrolment

    def enrol(self, samples: np.ndarray, speech_segments: list[tuple[float, float]]) -> Enrolment:
        cfg = self.enrolment_config
        speech = _concat(samples, speech_segments)
        speech_seconds = len(speech) / SAMPLE_RATE
        clipped = float(np.mean(np.abs(samples) >= 0.999)) if len(samples) else 0.0
        rms = _rms_dbfs(speech) if len(speech) else None

        def result(status: str, reason: str | None, windows: int = 0, consistency=None, minimum=None, emb=None):
            return Enrolment(status, reason, speech_seconds, windows, consistency, minimum, rms, clipped,
                             None if emb is None else [round(float(x), 6) for x in emb], self.model_id)

        if speech_seconds < cfg.min_speech_seconds:
            return result("rejected", f"Only {speech_seconds:.1f} s of speech; at least {cfg.min_speech_seconds:.0f} s are needed")
        if clipped >= cfg.max_clipped_ratio:
            return result("rejected", "The recording is distorted (too loud); move away from the microphone and retry")

        windows = _windows(speech, cfg.window_seconds, cfg.hop_seconds)
        embeddings = np.stack([self.embed(w) for w in windows])
        reference = embeddings.mean(axis=0)
        reference /= np.linalg.norm(reference)
        similarities = embeddings @ reference
        consistency, minimum = float(similarities.mean()), float(similarities.min())
        # Similarity to the AVERAGE cannot reveal a second voice (a 50/50 mix of two voices is ~0.7 similar to
        # its own mean). Pairs of windows can: a pair scoring like "different speakers" means another voice.
        pairs = embeddings @ embeddings.T
        off_diagonal = pairs[~np.eye(len(embeddings), dtype=bool)]
        different_pairs = float(np.mean(off_diagonal <= self.thresholds.different)) if off_diagonal.size else 0.0

        if different_pairs > cfg.max_different_pair_ratio:
            return result("uncertain", "The sample seems to contain more than one voice; please record it alone",
                          len(windows), consistency, minimum, reference)
        if consistency < cfg.min_self_consistency:
            return result("uncertain", "The sample is not consistent (background voices or strong noise?); please retry "
                          "in a quieter place", len(windows), consistency, minimum, reference)
        if rms is not None and rms < cfg.min_speech_rms_dbfs:
            return result("uncertain", "The voice is very quiet; please speak a little louder or closer to the microphone",
                          len(windows), consistency, minimum, reference)
        reason = None if speech_seconds >= cfg.target_speech_seconds else \
            f"Accepted with {speech_seconds:.1f} s of speech (target {cfg.target_speech_seconds:.0f} s)"
        return result("accepted", reason, len(windows), consistency, minimum, reference)

    # ------------------------------------------------------------------ comparison

    def compare(self, samples: np.ndarray, start: float, end: float, reference: np.ndarray) -> Comparison:
        """Compares samples[start:end] (seconds) with the reference embedding."""
        if end - start < self.thresholds.min_segment_seconds:
            return Comparison(start, end, None, UNCERTAIN,
                              f"shorter than {self.thresholds.min_segment_seconds:.1f} s: too short to compare")
        segment = samples[int(start * SAMPLE_RATE):int(end * SAMPLE_RATE)]
        try:
            similarity = float(self.embed(segment) @ reference)
        except ValueError as exc:
            return Comparison(start, end, None, UNCERTAIN, str(exc))
        return Comparison(start, end, similarity, self.decide(similarity))

    def decide(self, similarity: float) -> str:
        if similarity >= self.thresholds.same:
            return LIKELY_CANDIDATE
        if similarity <= self.thresholds.different:
            return LIKELY_NON_CANDIDATE
        return UNCERTAIN


def reference_from_list(values: list[float] | None, expected_dim: int | None = None) -> np.ndarray | None:
    """Validates a stored reference embedding (from the Redis message) and returns it normalised."""
    if not values:
        return None
    vector = np.asarray(values, dtype=np.float32)
    if vector.ndim != 1 or (expected_dim and vector.shape[0] != expected_dim) or not np.all(np.isfinite(vector)):
        return None
    norm = float(np.linalg.norm(vector))
    return None if norm == 0.0 else vector / norm


def _concat(samples: np.ndarray, segments: list[tuple[float, float]]) -> np.ndarray:
    parts = [samples[int(s * SAMPLE_RATE):int(e * SAMPLE_RATE)] for s, e in segments if e > s]
    return np.concatenate(parts).astype(np.float32) if parts else np.zeros(0, np.float32)


def _windows(speech: np.ndarray, window_s: float, hop_s: float) -> list[np.ndarray]:
    win, hop = int(window_s * SAMPLE_RATE), int(hop_s * SAMPLE_RATE)
    if len(speech) <= win:
        return [speech]
    return [speech[i:i + win] for i in range(0, len(speech) - win + 1, hop)]


def _rms_dbfs(x: np.ndarray) -> float:
    rms = float(np.sqrt(np.mean(np.square(x, dtype=np.float64)))) if len(x) else 0.0
    return -120.0 if rms <= 1e-6 else round(20 * np.log10(rms), 1)
