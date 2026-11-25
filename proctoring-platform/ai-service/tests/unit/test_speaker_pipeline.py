"""Enrolment and voice comparison inside the pipeline, with a fake speaker embedding (no model download).

The fake embedding encodes "who is speaking" by the sign of the audio: positive DC offset = candidate voice,
negative = another voice. Every result is checked against the contract (analysis-result.v1.json, 1.1).
"""
import numpy as np
import pytest

from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, validation_errors
from app.services.audio_loader import AudioStore
from app.services.processing_pipeline import ProcessingPipeline, speech_regions
from app.services.speaker_service import SpeakerService, SpeakerThresholds
from tests.helpers import FakeVad, event_entry, put_audio, wav_bytes

SR = 16_000
CANDIDATE = np.eye(16, dtype=np.float32)[0]   # the contract requires >= 16 embedding values
OTHER = np.eye(16, dtype=np.float32)[1]


def fake_embed(x: np.ndarray) -> np.ndarray:
    return CANDIDATE if float(np.mean(x)) >= 0 else OTHER


def speaker():
    svc = SpeakerService("unused.onnx", SpeakerThresholds(same=0.4028, different=0.3188))
    svc._extractor = object()
    svc.model_id = "campplus-test"
    svc.embed = fake_embed
    return svc


def voice(seconds: float, sign: float = 1.0) -> np.ndarray:
    t = np.arange(int(SR * seconds)) / SR
    return (sign * 0.2 + 0.1 * np.sin(2 * np.pi * 220 * t)).astype(np.float32)  # loud enough for FakeVad


def run(store_root, samples, event_type="speech", reference=None):
    data = wav_bytes(np.clip(samples, -1, 1))
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data, duration=round(len(samples) / SR, 3), event_type=event_type,
                        speaker_reference=reference)
    payload = ProcessingPipeline(AudioStore(store_root), FakeVad(), None, speaker()).process(parse_event_entry(entry)).to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    return payload


def review(payload):
    """Observations meant for review (informational SPEECH_DETECTED / NO_SPEECH_DETECTED excluded)."""
    return [o for o in payload["observations"] if o["category"] == "review"]


def informational(payload):
    return [o["observationType"] for o in payload["observations"] if o["category"] == "informational"]


REFERENCE = {"embedding": CANDIDATE.tolist(), "model": "campplus-test",
             "enrolmentEventId": "00000000-0000-4000-8000-000000000001"}


def test_enrolment_event_returns_the_reference_and_no_observations(store_root):
    payload = run(store_root, voice(12.0), event_type="enrolment")
    assert payload["schemaVersion"] == "1.1"
    assert payload["enrolment"]["status"] == "accepted" and len(payload["enrolment"]["embedding"]) == 16
    assert payload["observations"] == [] and payload["transcription"]["available"] is False


def test_short_enrolment_is_rejected_without_an_embedding(store_root):
    payload = run(store_root, voice(4.0), event_type="enrolment")
    assert payload["enrolment"]["status"] == "rejected" and payload["enrolment"]["embedding"] is None


def test_candidate_speech_produces_no_observation(store_root):
    payload = run(store_root, voice(6.0), reference=REFERENCE)
    assert payload["speaker"]["available"] and payload["speaker"]["reference"] == "present"
    assert {c["decision"] for c in payload["speaker"]["comparisons"]} == {"LIKELY_CANDIDATE"}
    assert review(payload) == [] and informational(payload) == ["SPEECH_DETECTED"]


def test_other_voice_produces_a_non_candidate_observation_with_evidence(store_root):
    payload = run(store_root, voice(4.0, sign=-1.0), reference=REFERENCE)
    (obs,) = review(payload)
    assert obs["observationType"] == "NON_CANDIDATE_VOICE" and obs["confidenceKind"] == "model-score"
    assert 0.5 <= obs["confidence"] <= 1.0
    assert obs["supportingEvidence"]["similarityToCandidate"] == 0.0
    assert obs["modelVersion"] == "campplus-test" and obs["eventId"] == payload["eventId"]


def test_candidate_then_other_voice_in_one_clip_is_multiple_speakers(store_root):
    clip = np.concatenate([voice(3.5), np.zeros(SR, np.float32), voice(3.5, sign=-1.0)])
    payload = run(store_root, clip, reference=REFERENCE)
    kinds = sorted(o["observationType"] for o in review(payload))
    assert kinds == ["MULTIPLE_SPEAKERS_DETECTED", "NON_CANDIDATE_VOICE"]
    multi = next(o for o in payload["observations"] if o["observationType"] == "MULTIPLE_SPEAKERS_DETECTED")
    assert multi["startSeconds"] < 1.0 and multi["endSeconds"] > 4.0


def test_short_other_voice_is_not_decided(store_root):
    payload = run(store_root, voice(2.0, sign=-1.0), reference=REFERENCE)
    assert review(payload) == []
    (comparison,) = payload["speaker"]["comparisons"]
    assert comparison["decision"] == "UNCERTAIN" and "not decided" in comparison["reason"]


def test_without_enrolment_voices_are_not_compared(store_root):
    payload = run(store_root, voice(4.0, sign=-1.0))
    assert payload["speaker"]["reference"] == "missing" and "No accepted voice sample" in payload["speaker"]["reason"]
    assert review(payload) == [] and informational(payload) == ["SPEECH_DETECTED"]


def test_reference_from_another_model_version_is_not_used(store_root):
    payload = run(store_root, voice(4.0, sign=-1.0), reference={**REFERENCE, "model": "campplus-older"})
    assert review(payload) == [] and "different model version" in payload["speaker"]["reason"]


def test_observation_ids_are_deterministic(store_root):
    clip = voice(4.0, sign=-1.0)
    data = wav_bytes(clip)
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data, duration=4.0, speaker_reference=REFERENCE)
    pipe = ProcessingPipeline(AudioStore(store_root), FakeVad(), None, speaker())
    first = pipe.process(parse_event_entry(entry)).to_payload()["observations"]
    second = pipe.process(parse_event_entry(entry)).to_payload()["observations"]
    assert [o["observationId"] for o in first] == [o["observationId"] for o in second]


def test_silence_is_recorded_as_no_speech_informational_only(store_root):
    payload = run(store_root, np.zeros(SR * 3, np.float32), reference=REFERENCE)
    (obs,) = payload["observations"]
    assert obs["observationType"] == "NO_SPEECH_DETECTED" and obs["category"] == "informational"
    assert obs["startSeconds"] == 0.0 and obs["endSeconds"] == 3.0


@pytest.mark.parametrize("segments, expected", [
    ([(0.0, 1.0), (1.2, 2.0)], [(0.0, 2.0)]),                       # short gap merged
    ([(0.0, 1.0), (2.0, 3.0)], [(0.0, 1.0), (2.0, 3.0)]),           # long gap kept apart
    ([(0.0, 7.0)], [(0.0, 3.5), (3.5, 7.0)]),                       # long region cut into ~3 s pieces
])
def test_speech_regions(segments, expected):
    assert speech_regions(segments) == expected
