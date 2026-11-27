"""The pure cosine() helper, with no embedding model loaded (SimilarityService.load/.score need the real
sentence-transformers model and are exercised manually, not in the unit suite - see SIMILARITY_ENABLED)."""
import numpy as np

from app.services.similarity_service import SimilarityService, cosine


def test_cosine_of_identical_vectors_is_one():
    v = np.array([0.6, 0.8], dtype=np.float32)
    assert cosine(v, v) == 1.0


def test_cosine_of_orthogonal_vectors_is_zero():
    assert cosine(np.array([1.0, 0.0]), np.array([0.0, 1.0])) == 0.0


def test_cosine_of_opposite_vectors_is_minus_one():
    assert cosine(np.array([1.0, 2.0]), np.array([-1.0, -2.0])) == -1.0


def test_cosine_is_zero_when_a_vector_has_no_magnitude():
    assert cosine(np.zeros(3), np.array([1.0, 2.0, 3.0])) == 0.0


def test_score_raises_before_the_model_is_loaded():
    svc = SimilarityService("intfloat/multilingual-e5-small")
    assert not svc.loaded
    try:
        svc.score("hello", "world")
        assert False, "expected a RuntimeError"
    except RuntimeError:
        pass


def test_score_is_zero_for_blank_text_without_needing_the_model():
    svc = SimilarityService("intfloat/multilingual-e5-small")
    svc._model = object()  # loaded, but score() must short-circuit before touching it
    assert svc.score("  ", "What is the capital of France?") == 0.0
    assert svc.score("some speech", "   ") == 0.0
