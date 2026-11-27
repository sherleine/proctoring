
import json
import logging
from typing import Protocol

from jsonschema import Draft202012Validator

from app.services.question_context import QuestionContext
from app.services.similarity_service import SimilarityService
from app.services.transcription_service import TranscriptSegmentData

log = logging.getLogger(__name__)

# These two Tier 1 flags are the strongest evidence of "more than one voice" within a single clip; they're
# passed to the LLM as context (see build_prompt) even though escalation itself no longer gates on them -
# every clip with a transcript is now reviewed, so a single clip Tier 1 saw as one voice still gets a look.
REVIEW_TRIGGER_TYPES = frozenset({"MULTIPLE_SPEAKERS_DETECTED", "OVERLAPPING_SPEECH_DETECTED"})

REVIEW_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["classification", "attributedTo", "confidence", "rationale", "recommendedReviewPriority"],
    "properties": {
        "classification": {
            "enum": ["likely_reading_question", "likely_benign_other", "possible_external_assistance", "inconclusive"],
        },
        "attributedTo": {"enum": ["main", "other", "mixed", "unknown"]},
        "matchedQuestionSpan": {
            "anyOf": [
                {"type": "null"},
                {"type": "array", "items": {"type": "number", "minimum": 0}, "minItems": 2, "maxItems": 2},
            ],
        },
        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
        "rationale": {"type": "string", "maxLength": 500},
        "recommendedReviewPriority": {"enum": ["low", "normal", "high"]},
    },
}
_VALIDATOR = Draft202012Validator(REVIEW_SCHEMA)


class LlmClient(Protocol):
    def complete(self, prompt: str, schema: dict) -> dict:
        """Returns the model's parsed JSON. Raises on any failure (network, timeout, bad JSON) - review_clip
        turns that into a safe fallback rather than letting it propagate."""
        ...


def tag_transcript(segments: list[TranscriptSegmentData], turns: list[tuple[float, float, str]],
                   role_by_label: dict[str, str]) -> list[dict]:
    """Attaches each transcript segment to whichever diarized speaker turn overlaps it the most, and that
    speaker's role (main/other/background/uncertain). A segment that overlaps no turn (diarization and ASR
    disagreed on timing) is tagged "uncertain" - never guessed into "main", since that is exactly the role a
    second voice must not be mistaken for."""
    tagged = []
    for seg in segments:
        best_label, best_overlap = None, 0.0
        for t_start, t_end, label in turns:
            overlap = min(seg.end, t_end) - max(seg.start, t_start)
            if overlap > best_overlap:
                best_overlap, best_label = overlap, label
        role = role_by_label.get(best_label, "uncertain") if best_label else "uncertain"
        tagged.append({"start": seg.start, "end": seg.end, "text": seg.text, "speaker": best_label, "role": role})
    return tagged


def fallback_review(reason: str) -> dict:
    """What a reviewer sees when the LLM could not be asked, or did not answer usefully: the clip keeps
    whatever priority Tier 1 already gave it - this never lowers confidence in the original flag."""
    return {"classification": "inconclusive", "attributedTo": "unknown", "matchedQuestionSpan": None,
           "confidence": 0.0, "rationale": f"AI review unavailable: {reason}"[:500],
           "recommendedReviewPriority": "normal"}


def validate_review(payload: object) -> dict | None:
    return payload if isinstance(payload, dict) and _VALIDATOR.is_valid(payload) else None


def build_prompt(tagged_segments: list[dict], question: QuestionContext | None, observation_types: list[str],
                 diarization_summary: dict, similarity_score: float | None) -> str:
    lines = [
        "You are assisting exam proctoring review. You decide nothing automatically - a human reviewer makes "
        "the final call. Judge only from the evidence below; do not assume anything it does not say.",
        f"Rule-based flags already raised for this clip: {', '.join(observation_types) if observation_types else 'none - this clip was reviewed as one voice, but a conversation can span separate clips'}.",
        f"Diarization summary: {json.dumps(diarization_summary, separators=(',', ':'))}",
    ]
    if question is not None:
        suffix = f" Options: {question.options}" if question.options else ""
        lines.append(f'Question displayed to the candidate during this clip: "{question.text}"{suffix}')
    else:
        lines.append("No question text is available for this time range - do not assume any reading happened; "
                     "judge from the transcript and diarization evidence alone.")
    if similarity_score is not None:
        lines.append(f"Transcript-to-question similarity score (embedding cosine, -1 to 1): {similarity_score}")
    lines.append("Speaker-tagged transcript (role describes the audio, never an identity - main is the clip's "
                 "primary voice, other is a clearly different voice, background is a much quieter voice, "
                 "uncertain is unresolved):")
    for seg in tagged_segments:
        lines.append(f"  [{seg['start']:.1f}-{seg['end']:.1f}] ({seg['role']}): {seg['text']}")
    lines.append(
        "Only speech attributed to the main speaker can ever count as the candidate reading the question "
        "aloud. The same words, spoken by any other role, are not a reading - they are a second voice saying "
        "the question, which is possible_external_assistance, however closely they match, never "
        "likely_reading_question.")
    lines.append("Respond with JSON only, matching the schema you were given.")
    return "\n".join(lines)


def review_clip(*, tagged_segments: list[dict], question: QuestionContext | None, observation_types: list[str],
               diarization_summary: dict, similarity: SimilarityService | None, llm: LlmClient) -> dict:
    """The orchestration for one clip's Tier 2 escalation. Every clip with transcribable speech is reviewed,
    whatever Tier 1 found in that clip alone - a question in one clip and its answer in a later, separate clip
    can each look like "one voice" on their own. Always returns a dict with at least "attempted"; never raises -
    a failure anywhere downgrades to "attempted but inconclusive", never to silence."""
    if not any(seg["text"].strip() for seg in tagged_segments):
        log.debug("Escalation skipped: no transcribable speech in this clip",
                 extra={"observationTypes": observation_types})
        return {"attempted": False, "reason": "no transcribable speech in this clip"}

    similarity_score = None
    if similarity is not None and similarity.loaded and question is not None:
        main_text = " ".join(seg["text"] for seg in tagged_segments if seg["role"] == "main").strip()
        if main_text:
            try:
                similarity_score = similarity.score(main_text, question.text)
            except Exception as exc:  # the pre-filter is an optimization only; never let it block the LLM call
                log.warning("Similarity pre-filter failed", extra={"error": str(exc)})

    prompt = build_prompt(tagged_segments, question, observation_types, diarization_summary, similarity_score)
    log.info("Escalating clip to the LLM", extra={"observationTypes": observation_types,
                                                   "hasQuestion": question is not None,
                                                   "similarityScore": similarity_score})
    try:
        payload = llm.complete(prompt, REVIEW_SCHEMA)
    except Exception as exc:
        log.warning("LLM call failed; falling back to inconclusive", extra={"error": f"{type(exc).__name__}: {exc}"})
        return {"attempted": True, "modelValid": False, "similarityScore": similarity_score,
               **fallback_review(type(exc).__name__)}
    validated = validate_review(payload)
    if validated is None:
        log.warning("LLM response did not match the expected schema; falling back to inconclusive",
                   extra={"rawResponse": payload})
        return {"attempted": True, "modelValid": False, "similarityScore": similarity_score,
               **fallback_review("response did not match the expected schema")}
    log.info("LLM review complete", extra={"classification": validated["classification"],
                                           "attributedTo": validated["attributedTo"],
                                           "confidence": validated["confidence"],
                                           "recommendedReviewPriority": validated["recommendedReviewPriority"]})
    return {"attempted": True, "modelValid": True, "similarityScore": similarity_score, **validated}


class TruncatedResponseError(RuntimeError):
    """The LLM stopped because it reached max_tokens - the JSON is incomplete and must not be trusted."""


class VllmClient:
    """Talks to an LLM through an OpenAI-compatible server (vLLM / LiteLLM). The server is asked for a JSON object
    and given the schema in the prompt; review_clip then validates the answer against that schema locally."""

    def __init__(self, base_url: str, model: str, timeout_seconds: float = 20.0, api_key: str = "",
                 max_tokens: int = 800, retries: int = 1):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.max_tokens = max_tokens
        self.retries = retries
        self.model = model
        self.timeout_seconds = timeout_seconds

    @property
    def model_id(self) -> str:
        return f"vllm:{self.model}"

    def complete(self, prompt: str, schema: dict) -> dict:
        """One retry, only for a runaway answer: with JSON-schema decoding some models occasionally keep
        emitting whitespace inside the object until they hit the token cap (seen: ~19,600 tokens / 174 s for an
        answer that normally takes ~150 tokens / 1.3 s). max_tokens bounds that to seconds; a cut-off answer is
        never parsed as if it were complete. The retry uses a small temperature to step off the stuck path."""
        last_error: Exception | None = None
        for attempt in range(1 + self.retries):
            try:
                return self._complete_once(prompt, schema, temperature=0.0 if attempt == 0 else 0.3)
            except TruncatedResponseError as exc:
                last_error = exc
                log.warning("LLM answer was cut off at max_tokens; retrying" if attempt < self.retries else
                            "LLM answer was cut off at max_tokens", extra={"attempt": attempt + 1})
        raise last_error  # type: ignore[misc]

    def _complete_once(self, prompt: str, schema: dict, temperature: float) -> dict:
        import httpx
        body = {
            "model": self.model,
            "messages": [{"role": "user", "content": f"{prompt}\nJSON schema of the answer: "
                                                     f"{json.dumps(schema, separators=(',', ':'))}"}],
            "temperature": temperature,
            "max_tokens": self.max_tokens,
            # json_object, not strict json_schema: on the deployed model, schema-constrained decoding regularly
            # looped on whitespace until max_tokens (4/5 first attempts), json_object answered 5/5 in ~1 s.
            # The answer is still checked against the schema locally (validate_review), so nothing is trusted blind.
            "response_format": {"type": "json_object"},
        }
        headers = {"Authorization": f"Bearer {self.api_key}"} if self.api_key else {}
        response = httpx.post(f"{self.base_url}/chat/completions", json=body, headers=headers,
                              timeout=self.timeout_seconds)
        response.raise_for_status()
        choice = response.json()["choices"][0]
        if choice.get("finish_reason") == "length":
            raise TruncatedResponseError(f"answer hit max_tokens={self.max_tokens}")
        return json.loads(choice["message"]["content"])
