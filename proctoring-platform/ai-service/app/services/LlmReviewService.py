"""Tier 2: one clip's speaker-tagged transcript -> LLM review, validated and with a safe fallback."""
import logging

from jsonschema import Draft202012Validator

from app.clients.llm_client import LlmClient
from app.prompts.LlmReviewPrompt import build_prompt
from app.schemas.LlmReviewSchema import REVIEW_SCHEMA
from app.services.TranscriptionService import TranscriptSegmentData

log = logging.getLogger(__name__)

_VALIDATOR = Draft202012Validator(REVIEW_SCHEMA)


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
    return {"classification": "inconclusive", "attributedTo": "unknown", "confidence": 0.0,
            "rationale": f"AI review unavailable: {reason}"[:500], "recommendedReviewPriority": "normal"}


# The transcript the model sees is tagged with voice roles (main/other/background/uncertain), so it naturally
# answers attributedTo with those words. Map them onto the contract's values instead of rejecting the review.
_ATTRIBUTION_ALIASES = {"background": "other", "uncertain": "unknown"}


def _normalize_review(payload: dict) -> dict:
    payload = dict(payload)
    attributed = payload.get("attributedTo")
    if isinstance(attributed, str):
        attributed = attributed.strip().lower()
        payload["attributedTo"] = _ATTRIBUTION_ALIASES.get(attributed, attributed)
    if isinstance(payload.get("rationale"), str):
        payload["rationale"] = payload["rationale"][:500]
    return payload


def validate_review(payload: object) -> dict | None:
    if not isinstance(payload, dict):
        return None
    payload = _normalize_review(payload)
    if _VALIDATOR.is_valid(payload):
        return payload
    log.warning("LLM review schema errors", extra={"errors": [e.message for e in _VALIDATOR.iter_errors(payload)]})
    return None


def review_clip(*, tagged_segments: list[dict], observation_types: list[str], diarization_summary: dict,
                llm: LlmClient) -> dict:
    """Never raises: a failed or invalid LLM answer becomes "attempted" with an inconclusive fallback review."""
    if not any(seg["text"].strip() for seg in tagged_segments):
        log.debug("Escalation skipped: no transcribable speech in this clip",
                 extra={"observationTypes": observation_types})
        return {"attempted": False, "reason": "no transcribable speech in this clip"}

    prompt = build_prompt(tagged_segments, observation_types, diarization_summary)
    log.info("Escalating clip to the LLM", extra={"observationTypes": observation_types})
    try:
        payload = llm.complete(prompt, REVIEW_SCHEMA)
    except Exception as exc:
        log.warning("LLM call failed; falling back to inconclusive", extra={"error": f"{type(exc).__name__}: {exc}"})
        return {"attempted": True, "modelValid": False, **fallback_review(type(exc).__name__)}
    validated = validate_review(payload)
    if validated is None:
        log.warning("LLM response did not match the expected schema; falling back to inconclusive",
                   extra={"rawResponse": payload})
        return {"attempted": True, "modelValid": False,
                **fallback_review("response did not match the expected schema")}
    log.info("LLM review complete", extra={"classification": validated["classification"],
                                           "attributedTo": validated["attributedTo"],
                                           "confidence": validated["confidence"],
                                           "recommendedReviewPriority": validated["recommendedReviewPriority"]})
    return {"attempted": True, "modelValid": True, **validated}
