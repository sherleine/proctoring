"""Tier 2 escalation: when Tier 1 (VAD + diarization + CAM++ + detection_rules) flags a clip as overlapping or
multiple-speaker, send a speaker-tagged transcript plus the active exam question to a local LLM, and ask it to
tell "the candidate read the question aloud" apart from "someone else is talking". The LLM's answer is stored
as a note next to the original observations - it never replaces or deletes them, and a human reviewer always
makes the final call. See docs/history (research conversation) for the design rationale.

Everything here is pure and unit-testable without a model or a network call except LlmClient.complete, which
is the one method that actually talks to the LLM server (vLLM's OpenAI-compatible endpoint, structured output
via response_format/json_schema so the reply is always parsable JSON - see Settings.llm_base_url).
"""
import json
import logging
from typing import Protocol

from jsonschema import Draft202012Validator

from app.services.question_context import QuestionContext
from app.services.similarity_service import SimilarityService
from app.services.transcription_service import TranscriptSegmentData

log = logging.getLogger(__name__)

# Only these two Tier 1 flags are evidence of "more than one voice" - the only situation where "was this just
# the candidate reading the question" is a meaningful question to ask at all.
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
        f"Rule-based flags already raised for this clip: {', '.join(observation_types)}.",
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
    """The orchestration for one clip's Tier 2 escalation. Always returns a dict with at least "attempted";
    never raises - a failure anywhere downgrades to "attempted but inconclusive", never to silence."""
    if not set(observation_types) & REVIEW_TRIGGER_TYPES:
        return {"attempted": False, "reason": "no overlap/multiple-speaker flag on this clip"}
    if not any(seg["text"].strip() for seg in tagged_segments):
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
    try:
        payload = llm.complete(prompt, REVIEW_SCHEMA)
    except Exception as exc:
        return {"attempted": True, "modelValid": False, "similarityScore": similarity_score,
               **fallback_review(type(exc).__name__)}
    validated = validate_review(payload)
    if validated is None:
        return {"attempted": True, "modelValid": False, "similarityScore": similarity_score,
               **fallback_review("response did not match the expected schema")}
    return {"attempted": True, "modelValid": True, "similarityScore": similarity_score, **validated}


class VllmClient:
    """Talks to a local LLM through vLLM's OpenAI-compatible server, with the JSON schema enforced by the
    server's structured-output support (response_format: json_schema) rather than trusted to prompting alone."""

    def __init__(self, base_url: str, model: str, timeout_seconds: float = 20.0):
        self.base_url = base_url.rstrip("/")
        self.model = model
        self.timeout_seconds = timeout_seconds

    @property
    def model_id(self) -> str:
        return f"vllm:{self.model}"

    def complete(self, prompt: str, schema: dict) -> dict:
        import httpx
        body = {
            "model": self.model,
            "messages": [{"role": "user", "content": prompt}],
            "temperature": 0.0,
            "response_format": {"type": "json_schema",
                               "json_schema": {"name": "clip_review", "schema": schema, "strict": True}},
        }
        response = httpx.post(f"{self.base_url}/chat/completions", json=body, timeout=self.timeout_seconds)
        response.raise_for_status()
        content = response.json()["choices"][0]["message"]["content"]
        return json.loads(content)
