"""The valid/invalid + reason convenience summary, pure: built from AnalysisResult.to_payload()-shaped dicts."""
from app.services.review_summary import summarize


def obs(observation_type: str, category: str) -> dict:
    return {"observationType": observation_type, "category": category}


def test_no_review_flags_is_valid():
    payload = {"observations": [obs("SPEECH_DETECTED", "informational")], "aiReview": {"attempted": False}}
    out = summarize(payload)
    assert out["valid"] is True
    assert "No overlap" in out["reason"]


def test_flagged_and_cleared_by_the_llm_is_valid():
    payload = {"observations": [obs("MULTIPLE_SPEAKERS_DETECTED", "review")],
              "aiReview": {"attempted": True, "modelValid": True, "classification": "likely_reading_question",
                           "rationale": "The main speaker read the question aloud."}}
    out = summarize(payload)
    assert out["valid"] is True
    assert "MULTIPLE_SPEAKERS_DETECTED" in out["reason"]
    assert "read the question aloud" in out["reason"]


def test_flagged_and_confirmed_by_the_llm_is_invalid():
    payload = {"observations": [obs("OVERLAPPING_SPEECH_DETECTED", "review")],
              "aiReview": {"attempted": True, "modelValid": True, "classification": "possible_external_assistance",
                           "rationale": "A second voice answered before the candidate finished speaking."}}
    out = summarize(payload)
    assert out["valid"] is False
    assert "second voice answered" in out["reason"]


def test_flagged_but_llm_review_failed_stays_invalid():
    payload = {"observations": [obs("MULTIPLE_SPEAKERS_DETECTED", "review")],
              "aiReview": {"attempted": True, "modelValid": False, "classification": "inconclusive"}}
    out = summarize(payload)
    assert out["valid"] is False
    assert "did not complete" in out["reason"]


def test_flagged_but_escalation_disabled_stays_invalid():
    payload = {"observations": [obs("MULTIPLE_SPEAKERS_DETECTED", "review")],
              "aiReview": {"attempted": False, "reason": "Tier 2 escalation is disabled (ESCALATION_ENABLED=false)"}}
    out = summarize(payload)
    assert out["valid"] is False
    assert "ESCALATION_ENABLED=false" in out["reason"]


def test_flagged_and_llm_calls_it_benign_other_is_valid():
    payload = {"observations": [obs("BACKGROUND_CONVERSATION_DETECTED", "review")],
              "aiReview": {"attempted": True, "modelValid": True, "classification": "likely_benign_other",
                           "rationale": "A television was audible in the background."}}
    out = summarize(payload)
    assert out["valid"] is True


def test_repeated_flags_are_counted_not_repeated():
    payload = {"observations": [obs("MULTIPLE_SPEAKERS_DETECTED", "review")] +
                               [obs("BACKGROUND_CONVERSATION_DETECTED", "review")] * 3,
               "aiReview": {"attempted": False, "reason": "disabled"}}
    reason = summarize(payload)["reason"]
    assert "MULTIPLE_SPEAKERS_DETECTED, BACKGROUND_CONVERSATION_DETECTED (x3)." in reason
    assert reason.count("BACKGROUND_CONVERSATION_DETECTED") == 1
