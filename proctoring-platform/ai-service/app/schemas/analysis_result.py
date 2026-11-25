"""Outgoing messages: `audio.analysis.started`, `audio.analysis.completed`, `audio.analysis.failed`.

The models mirror analysis-result.v1.json / analysis-started.v1.json field for field (no extra fields), and
every payload is validated against those JSON Schemas before it is published.
"""
import json
import uuid
from typing import Literal

from pydantic import BaseModel, Field

from app.schemas.contracts import ANALYSIS_RESULT, ANALYSIS_STARTED, ensure_valid
from app.utils.time_utils import utc_now_iso

STARTED = "audio.analysis.started"
COMPLETED = "audio.analysis.completed"
FAILED = "audio.analysis.failed"
DEAD_LETTERED = "DEAD_LETTERED"  # error.code convention: the processor gave up (event becomes DEAD_LETTERED)

FindingType = Literal["CANDIDATE_SPEECH", "MULTIPLE_VOICES", "BACKGROUND_CONVERSATION", "OVERLAPPING_SPEECH",
                      "POSSIBLE_VERBAL_ASSISTANCE", "UNUSUAL_AUDIO", "NO_SPEECH_VERIFIED"]


class Span(BaseModel):
    start: float
    end: float


class AudioInfo(BaseModel):
    durationSeconds: float
    sampleRate: int
    rmsDbfs: float
    peakDbfs: float


class SpeechStage(BaseModel):
    available: bool
    reason: str | None = None
    verified: bool = False
    speechSeconds: float = 0.0
    speechRatio: float = 0.0
    segments: list[Span] = Field(default_factory=list)


class TranscriptSegment(Span):
    text: str
    speaker: str | None = None  # no diarization in this version
    avgLogprob: float | None = None
    noSpeechProb: float | None = None


class TranscriptionStage(BaseModel):
    available: bool
    reason: str | None = None
    language: str | None = None
    languageProbability: float | None = None
    text: str = ""
    segments: list[TranscriptSegment] = Field(default_factory=list)


class DiarizationStage(BaseModel):
    available: bool = False
    reason: str | None = "Speaker diarization was not run for this event"
    speakerCount: int | None = None
    turns: list[dict] = Field(default_factory=list)
    overlaps: list[Span] = Field(default_factory=list)
    # contract 1.1 additions (optional): per anonymous speaker label, speech amounts, level and voice role
    speakers: list[dict] = Field(default_factory=list)
    distinctVoices: int | None = None


class Finding(BaseModel):
    type: FindingType
    severity: Literal["info", "review"]
    description: str
    start: float | None
    end: float | None
    evidence: dict


class ErrorInfo(BaseModel):
    code: str
    message: str
    retryable: bool
    deliveryCount: int | None = None


class SpeakerStage(BaseModel):
    """Contract 1.1: comparison of speech regions with the candidate's enrolled voice."""
    available: bool = False
    reason: str | None = "No candidate voice comparison for this event"
    reference: Literal["present", "missing"] = "missing"
    model: str | None = None
    thresholds: dict | None = None
    comparisons: list[dict] = Field(default_factory=list)


class AnalysisResult(BaseModel):
    schemaVersion: Literal["1.0", "1.1"] = "1.1"
    eventId: str
    sessionId: str
    candidateId: str
    status: Literal["completed", "failed"]
    processedAt: str = Field(default_factory=utc_now_iso)
    processingMs: float
    models: dict[str, str | None]
    audio: AudioInfo | None
    speech: SpeechStage
    transcription: TranscriptionStage
    diarization: DiarizationStage = Field(default_factory=DiarizationStage)
    findings: list[Finding] = Field(default_factory=list)
    error: ErrorInfo | None = None
    # contract 1.1
    enrolment: dict | None = None
    speaker: SpeakerStage = Field(default_factory=SpeakerStage)
    observations: list[dict] = Field(default_factory=list)

    def to_payload(self) -> dict:
        payload = self.model_dump(mode="json", exclude_none=False)
        ensure_valid(ANALYSIS_RESULT, payload)
        return payload

    @property
    def message_type(self) -> str:
        return COMPLETED if self.status == "completed" else FAILED


def failed_result(session_id: str, event_id: str, candidate_id: str, code: str, message: str,
                  retryable: bool, delivery_count: int, processing_ms: float = 0.0) -> AnalysisResult:
    """A failure result: no stage output is invented, every stage says why it is unavailable."""
    reason = f"Not analysed: {code}"
    return AnalysisResult(
        eventId=event_id, sessionId=session_id, candidateId=candidate_id, status="failed",
        processingMs=processing_ms, models={}, audio=None,
        speech=SpeechStage(available=False, reason=reason),
        transcription=TranscriptionStage(available=False, reason=reason),
        diarization=DiarizationStage(available=False, reason=reason),
        error=ErrorInfo(code=code, message=message[:500], retryable=retryable, deliveryCount=delivery_count))


def started_payload(session_id: str, event_id: str, delivery_count: int, worker: str) -> dict:
    payload = {"schemaVersion": "1.0", "sessionId": session_id, "eventId": event_id, "startedAt": utc_now_iso(),
               "deliveryCount": delivery_count, "worker": worker[:128]}
    ensure_valid(ANALYSIS_STARTED, payload)
    return payload


def envelope(message_type: str, session_id: str, event_id: str, payload: dict) -> dict[str, str]:
    """The eight flat string fields of a stream entry (stream-envelope.v1.json)."""
    return {
        "messageType": message_type,
        "schemaVersion": "1",
        "messageId": str(uuid.uuid4()),
        "correlationId": event_id,
        "sessionId": session_id,
        "eventId": event_id,
        "producedAt": utc_now_iso(),
        "payload": json.dumps(payload, separators=(",", ":")),
    }
