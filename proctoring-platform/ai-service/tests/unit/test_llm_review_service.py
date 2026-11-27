"""Tier 2 escalation logic, pure: no model and no network. A fake LlmClient stands in for the real
vLLM-backed one (app.services.llm_review_service.VllmClient), which only does HTTP I/O."""
from app.services.llm_review_service import (REVIEW_TRIGGER_TYPES, build_prompt, fallback_review, review_clip,
                                             tag_transcript, validate_review)
from app.services.question_context import QuestionContext
from app.services.transcription_service import TranscriptSegmentData


class FakeLlm:
    def __init__(self, response=None, fail: Exception | None = None):
        self.response = response
        self.fail = fail
        self.calls: list[tuple[str, dict]] = []

    def complete(self, prompt: str, schema: dict) -> dict:
        self.calls.append((prompt, schema))
        if self.fail:
            raise self.fail
        return self.response


VALID_RESPONSE = {"classification": "likely_reading_question", "attributedTo": "main",
                  "matchedQuestionSpan": [0.0, 2.5], "confidence": 0.8,
                  "rationale": "The main speaker's words match the question text.",
                  "recommendedReviewPriority": "low"}


# ------------------------------------------------------------------ tag_transcript

def test_tag_transcript_assigns_the_turn_with_the_most_overlap():
    segments = [TranscriptSegmentData(0.0, 2.0, "hello there", -0.2, 0.01)]
    turns = [(0.0, 0.5, "S1"), (0.5, 3.0, "S2")]  # S2 overlaps 1.5s of the segment, S1 only 0.5s
    tagged = tag_transcript(segments, turns, {"S1": "other", "S2": "main"})
    assert tagged == [{"start": 0.0, "end": 2.0, "text": "hello there", "speaker": "S2", "role": "main"}]


def test_tag_transcript_with_no_overlapping_turn_is_uncertain():
    segments = [TranscriptSegmentData(10.0, 11.0, "orphan segment", -0.2, 0.01)]
    tagged = tag_transcript(segments, [(0.0, 1.0, "S1")], {"S1": "main"})
    assert tagged[0]["speaker"] is None
    assert tagged[0]["role"] == "uncertain"


# ------------------------------------------------------------------ schema / fallback

def test_fallback_review_is_always_inconclusive_and_normal_priority():
    out = fallback_review("boom")
    assert out["classification"] == "inconclusive"
    assert out["recommendedReviewPriority"] == "normal"
    assert "boom" in out["rationale"]


def test_validate_review_accepts_a_well_formed_payload():
    assert validate_review(VALID_RESPONSE) == VALID_RESPONSE


def test_validate_review_rejects_an_unknown_classification():
    bad = {**VALID_RESPONSE, "classification": "definitely_cheating"}
    assert validate_review(bad) is None


def test_validate_review_rejects_a_non_dict():
    assert validate_review("not json") is None
    assert validate_review(None) is None


# ------------------------------------------------------------------ review_clip orchestration

MAIN_SEGMENT = [{"start": 0.0, "end": 2.0, "text": "what is the capital of France", "speaker": "S1", "role": "main"}]


def test_review_clip_still_runs_on_a_clip_tier_1_saw_as_a_single_voice():
    """A question in one clip and its answer in a separate clip can each look like "one voice" alone - so
    escalation no longer gates on a Tier 1 flag; only on there being something to transcribe."""
    llm = FakeLlm(VALID_RESPONSE)
    out = review_clip(tagged_segments=MAIN_SEGMENT, question=None, observation_types=[],
                      diarization_summary={}, similarity=None, llm=llm)
    assert out["attempted"] is True
    assert llm.calls, "the LLM should have been called even with no Tier 1 flag on this clip"


def test_review_clip_is_not_attempted_with_no_transcribable_speech():
    empty = [{"start": 0.0, "end": 1.0, "text": "  ", "speaker": None, "role": "uncertain"}]
    out = review_clip(tagged_segments=empty, question=None, observation_types=["MULTIPLE_SPEAKERS_DETECTED"],
                      diarization_summary={}, similarity=None, llm=FakeLlm(VALID_RESPONSE))
    assert out["attempted"] is False


def test_review_clip_returns_the_validated_llm_output():
    llm = FakeLlm(VALID_RESPONSE)
    out = review_clip(tagged_segments=MAIN_SEGMENT, question=QuestionContext("What is the capital of France?"),
                      observation_types=["OVERLAPPING_SPEECH_DETECTED"], diarization_summary={"speakerCount": 2},
                      similarity=None, llm=llm)
    assert out["attempted"] is True and out["modelValid"] is True
    assert out["classification"] == "likely_reading_question"
    assert llm.calls, "the LLM should have been called"


def test_review_clip_falls_back_when_the_llm_call_fails():
    out = review_clip(tagged_segments=MAIN_SEGMENT, question=None, observation_types=["MULTIPLE_SPEAKERS_DETECTED"],
                      diarization_summary={}, similarity=None, llm=FakeLlm(fail=TimeoutError("slow")))
    assert out["attempted"] is True and out["modelValid"] is False
    assert out["classification"] == "inconclusive"
    assert "TimeoutError" in out["rationale"]


def test_review_clip_falls_back_when_the_llm_response_is_invalid():
    out = review_clip(tagged_segments=MAIN_SEGMENT, question=None, observation_types=["MULTIPLE_SPEAKERS_DETECTED"],
                      diarization_summary={}, similarity=None, llm=FakeLlm({"not": "the expected shape"}))
    assert out["attempted"] is True and out["modelValid"] is False


def test_review_clip_is_not_attempted_with_an_empty_clip():
    assert REVIEW_TRIGGER_TYPES  # sanity: still used as prompt context in build_prompt
    out = review_clip(tagged_segments=[], question=None, observation_types=[], diarization_summary={},
                      similarity=None, llm=FakeLlm(VALID_RESPONSE))
    assert out["attempted"] is False


def test_build_prompt_tells_the_llm_only_main_speech_counts_as_reading():
    prompt = build_prompt(MAIN_SEGMENT, QuestionContext("What is the capital of France?"),
                          ["OVERLAPPING_SPEECH_DETECTED"], {"speakerCount": 2}, similarity_score=0.9)
    assert "main" in prompt and "possible_external_assistance" in prompt
    assert "What is the capital of France?" in prompt


def test_build_prompt_handles_no_tier_1_flags_at_all():
    prompt = build_prompt(MAIN_SEGMENT, None, [], {"speakerCount": 1}, similarity_score=None)
    assert "none" in prompt
    assert "what is the capital of France" in prompt


# ------------------------------------------------------------------ VllmClient runaway handling

class _Resp:
    def __init__(self, content, finish):
        self._j = {"choices": [{"finish_reason": finish, "message": {"content": content}}]}

    def raise_for_status(self):
        pass

    def json(self):
        return self._j


def _client_with(monkeypatch, responses):
    import httpx
    from app.services.llm_review_service import VllmClient
    sent = []

    def fake_post(url, json, headers, timeout):
        sent.append(json)
        return responses.pop(0)
    monkeypatch.setattr(httpx, "post", fake_post)
    return VllmClient("http://llm/v1", "m", api_key="k", max_tokens=800), sent


def test_runaway_answer_is_retried_once_and_the_retry_is_used(monkeypatch):
    client, sent = _client_with(monkeypatch, [_Resp('{"a": 1,\n  \n  \n', "length"), _Resp('{"ok": true}', "stop")])
    assert client.complete("p", {}) == {"ok": True}
    assert len(sent) == 2 and all(b["max_tokens"] == 800 for b in sent)
    assert sent[0]["temperature"] == 0.0 and sent[1]["temperature"] > 0


def test_two_runaway_answers_raise_so_the_review_falls_back(monkeypatch):
    import pytest
    from app.services.llm_review_service import TruncatedResponseError
    client, _ = _client_with(monkeypatch, [_Resp("{", "length"), _Resp("{", "length")])
    with pytest.raises(TruncatedResponseError):
        client.complete("p", {})
