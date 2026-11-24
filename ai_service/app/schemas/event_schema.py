from enum import Enum
from typing import Any

from pydantic import BaseModel, Field


class EventType(str, Enum):
    """Observations the pipeline can report. None of these is proof of misconduct."""

    CANDIDATE_SPEECH_DETECTED = "CANDIDATE_SPEECH_DETECTED"
    MULTIPLE_SPEAKERS_DETECTED = "MULTIPLE_SPEAKERS_DETECTED"
    BACKGROUND_CONVERSATION_DETECTED = "BACKGROUND_CONVERSATION_DETECTED"
    OVERLAPPING_SPEECH_DETECTED = "OVERLAPPING_SPEECH_DETECTED"
    POSSIBLE_EXTERNAL_ASSISTANCE = "POSSIBLE_EXTERNAL_ASSISTANCE"
    UNUSUAL_AUDIO_DETECTED = "UNUSUAL_AUDIO_DETECTED"
    NO_INTELLIGIBLE_SPEECH = "NO_INTELLIGIBLE_SPEECH"
    NO_SPEECH_VERIFIED = "NO_SPEECH_VERIFIED"


class AudioEvent(BaseModel):
    event_type: EventType
    description: str
    start: float | None = None
    end: float | None = None
    requires_review: bool
    # Which component produced the event ("rules", "llm", ...) and the evidence used.
    source: str
    evidence: dict[str, Any] = Field(default_factory=dict)


class ErrorResponse(BaseModel):
    error_code: str
    message: str
    details: dict[str, Any] = Field(default_factory=dict)
    request_id: str | None = None
