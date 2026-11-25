"""Voice embedding service and voice-comparison thresholds (no model needed)."""
import numpy as np
import pytest

from app.core.errors import TransientEventError
from app.services.detection_rules import ScoreScale
from app.services.speaker_service import SpeakerService


def test_thresholds_must_leave_an_ordered_band():
    with pytest.raises(ValueError):
        ScoreScale(same=0.3, different=0.3)
    with pytest.raises(ValueError):
        ScoreScale(same=0.2, different=0.5)


def test_distinct_confidence_is_half_at_the_threshold_and_full_for_typical_different_voices():
    scale = ScoreScale()
    assert scale.distinct_confidence(scale.different) == pytest.approx(0.5)
    assert scale.distinct_confidence(scale.different_median) == pytest.approx(1.0)
    assert scale.distinct_confidence(-0.5) == 1.0  # clipped


def test_embedding_before_loading_is_a_transient_error():
    with pytest.raises(TransientEventError):
        SpeakerService("unused.onnx").embed(np.zeros(16000, np.float32))


def test_missing_model_file_fails_loading_with_a_clear_message(tmp_path):
    with pytest.raises(FileNotFoundError, match="fetch_models"):
        SpeakerService(tmp_path / "missing.onnx").load()
