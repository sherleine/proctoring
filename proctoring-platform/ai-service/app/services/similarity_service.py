"""Optional, cheap pre-filter: how closely a transcript segment resembles the active question/options.

This is a triage signal only, attached to the AI review for the reviewer's benefit - it can lower an
escalation's priority but must never suppress it outright (see llm_review_service.review_clip). Uses a small
multilingual sentence-embedding model (intfloat/multilingual-e5-small, MIT license) so a transcript segment
can be compared against a question that stayed in the candidate's own language, independent of whatever
language Whisper's "translate" task happened to render the speech into.
"""
import logging

import numpy as np

log = logging.getLogger(__name__)


def cosine(a: np.ndarray, b: np.ndarray) -> float:
    """Cosine similarity in [-1, 1]. Returns 0.0 if either vector has no magnitude (nothing to compare)."""
    denom = float(np.linalg.norm(a) * np.linalg.norm(b))
    if denom == 0.0:
        return 0.0
    return round(float(np.dot(a, b) / denom), 4)


class SimilarityService:
    def __init__(self, model_name: str):
        self.model_name = model_name
        self._model = None

    @property
    def model_id(self) -> str:
        return f"sentence-transformers:{self.model_name}"

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        from sentence_transformers import SentenceTransformer
        self._model = SentenceTransformer(self.model_name)
        log.info("Similarity model loaded", extra={"model": self.model_id})

    def score(self, speech_text: str, question_text: str) -> float:
        """Cosine similarity between a transcript segment and the question/options text.

        E5 models are trained with "query:"/"passage:" prefixes for retrieval; the transcript is the query,
        the question is the passage it is being checked against."""
        if self._model is None:
            raise RuntimeError("Similarity model is not loaded")
        if not speech_text.strip() or not question_text.strip():
            return 0.0
        vectors = self._model.encode([f"query: {speech_text}", f"passage: {question_text}"],
                                     normalize_embeddings=True)
        return cosine(vectors[0], vectors[1])
