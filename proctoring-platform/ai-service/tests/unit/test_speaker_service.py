"""Decision logic of the speaker service with a fake embedding function (no model needed)."""
import numpy as np
import pytest

from app.services.speaker_service import (LIKELY_CANDIDATE, LIKELY_NON_CANDIDATE, UNCERTAIN, EnrolmentConfig,
                                          SpeakerService, SpeakerThresholds, reference_from_list)

SR = 16_000


def service(embed=None, **thresholds):
    svc = SpeakerService("unused.onnx", SpeakerThresholds(**{"same": 0.6, "different": 0.3, **thresholds}))
    svc._extractor = object()  # "loaded"
    if embed:
        svc.embed = embed
    return svc


def unit(v):
    v = np.asarray(v, np.float32)
    return v / np.linalg.norm(v)


def test_thresholds_must_leave_an_ordered_band():
    with pytest.raises(ValueError):
        SpeakerThresholds(same=0.3, different=0.3)
    with pytest.raises(ValueError):
        SpeakerThresholds(same=0.2, different=0.5)


@pytest.mark.parametrize("similarity, expected", [(0.9, LIKELY_CANDIDATE), (0.6, LIKELY_CANDIDATE),
                                                  (0.45, UNCERTAIN), (0.3, LIKELY_NON_CANDIDATE), (-0.2, LIKELY_NON_CANDIDATE)])
def test_decision_bands(similarity, expected):
    assert service().decide(similarity) == expected


def test_short_segments_are_never_decided():
    svc = service(embed=lambda x: pytest.fail("must not embed a too-short segment"))
    result = svc.compare(np.zeros(SR * 3, np.float32), 0.0, 1.0, unit([1, 0]))
    assert result.decision == UNCERTAIN and result.similarity is None and "too short" in result.reason


def test_comparison_uses_cosine_similarity_with_the_reference():
    svc = service(embed=lambda x: unit([1, 1]))
    result = svc.compare(np.zeros(SR * 3, np.float32), 0.0, 2.0, unit([1, 0]))
    assert result.similarity == pytest.approx(0.7071, abs=1e-3) and result.decision == LIKELY_CANDIDATE


def test_enrolment_rejects_too_little_speech():
    svc = service(embed=lambda x: unit([1, 0]))
    result = svc.enrol(np.full(SR * 20, 0.1, np.float32), [(0.0, 5.0)])
    assert result.status == "rejected" and "5.0 s of speech" in result.reason and result.embedding is None


def test_enrolment_rejects_clipped_audio():
    samples = np.full(SR * 12, 0.1, np.float32)
    samples[: SR] = 1.0  # > 1 % at full scale
    result = service(embed=lambda x: unit([1, 0])).enrol(samples, [(0.0, 12.0)])
    assert result.status == "rejected" and "distorted" in result.reason


def test_consistent_enrolment_is_accepted_with_a_normalised_reference():
    result = service(embed=lambda x: unit([1, 0.05])).enrol(np.full(SR * 12, 0.1, np.float32), [(0.0, 12.0)])
    assert result.status == "accepted" and result.windows >= 5
    assert np.linalg.norm(result.embedding) == pytest.approx(1.0, abs=1e-4)
    assert result.self_consistency == pytest.approx(1.0, abs=1e-3)


def test_inconsistent_enrolment_is_uncertain_not_accepted():
    flip = iter(range(1000))
    result = service(embed=lambda x: unit([1, 0]) if next(flip) % 2 else unit([0, 1])).enrol(
        np.full(SR * 12, 0.1, np.float32), [(0.0, 12.0)])
    # every window is ~0.71 similar to the averaged reference, so only the pairwise check can catch this
    assert result.status == "uncertain" and result.self_consistency > EnrolmentConfig().min_self_consistency
    assert "more than one voice" in result.reason


def test_noisy_enrolment_with_low_consistency_is_uncertain():
    # 6 orthogonal directions: ~0.41 similar to their mean (below 0.55); with different=-0.9 no PAIR counts as
    # "different speaker", so the low-consistency rule (not the pairwise rule) must catch it
    vectors = iter(list(np.eye(6, dtype=np.float32)) * 50)
    svc = service(embed=lambda x: next(vectors), different=-0.9, same=0.6)
    result = svc.enrol(np.full(SR * 12, 0.1, np.float32), [(0.0, 12.0)])
    assert result.status == "uncertain" and "consistent" in result.reason


def test_quiet_enrolment_is_uncertain():
    result = service(embed=lambda x: unit([1, 0])).enrol(np.full(SR * 12, 0.001, np.float32), [(0.0, 12.0)])
    assert result.status == "uncertain" and "quiet" in result.reason


def test_reference_from_list_validates_input():
    assert reference_from_list(None) is None
    assert reference_from_list([]) is None
    assert reference_from_list([0.0, 0.0]) is None
    assert reference_from_list([1.0, float("nan")]) is None
    assert reference_from_list([3.0, 4.0], expected_dim=3) is None
    np.testing.assert_allclose(reference_from_list([3.0, 4.0], expected_dim=2), [0.6, 0.8])
