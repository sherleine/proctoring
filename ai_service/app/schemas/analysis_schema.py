"""Structured result returned for every processed clip.

Every field is filled from actual model output. Optional components that are
disabled or unavailable report ``available=False`` with a reason instead of
inventing values.
"""

from typing import Literal

from pydantic import BaseModel, Field

from app.schemas.audio_schema import AudioStats
from app.schemas.event_schema import AudioEvent


class ComponentStatus(BaseModel):
    available: bool
    provider: str | None = None
    model: str | None = None
    reason: str | None = None  # why the component was skipped/unavailable


class SpeechRegion(BaseModel):
    start: float
    end: float


class VadAnalysis(ComponentStatus):
    speech_detected: bool = False
    speech_regions: list[SpeechRegion] = Field(default_factory=list)
    speech_duration_seconds: float = 0.0
    speech_ratio: float = 0.0
    # Comparison with the browser VAD for the same clip, when the frontend sent its stats.
    frontend_speech_ratio: float | None = None
    agrees_with_frontend: bool | None = None


class TranscriptWord(BaseModel):
    start: float
    end: float
    word: str
    probability: float  # Whisper's per-token probability, as reported by the model
    speaker: str | None = None


class TranscriptSegment(BaseModel):
    start: float
    end: float
    text: str
    speaker: str | None = None
    # Raw Whisper decoder statistics. These are model outputs, not calibrated confidences.
    avg_logprob: float | None = None
    no_speech_prob: float | None = None
    words: list[TranscriptWord] = Field(default_factory=list)


class Transcription(ComponentStatus):
    text: str = ""
    language: str | None = None
    language_probability: float | None = None
    segments: list[TranscriptSegment] = Field(default_factory=list)


class SpeakerTurn(BaseModel):
    speaker_id: str
    start: float
    end: float


class OverlapRegion(BaseModel):
    start: float
    end: float
    speakers: list[str]


class Diarization(ComponentStatus):
    number_of_speakers: int | None = None
    speakers: list[SpeakerTurn] = Field(default_factory=list)
    overlaps: list[OverlapRegion] = Field(default_factory=list)
    speaker_changes: int | None = None


class SoundLabel(BaseModel):
    label: str
    score: float  # classifier output score (mean over frames), as produced by the model
    start: float | None = None
    end: float | None = None


class AudioClassification(ComponentStatus):
    top_labels: list[SoundLabel] = Field(default_factory=list)


class LLMInterpretation(ComponentStatus):
    observation: str | None = None
    possible_external_conversation: bool | None = None
    requires_review: bool | None = None
    reason: str | None = None


class AudioAnalysisSummary(BaseModel):
    speech_detected: bool
    number_of_speakers: int | None  # None when diarization was unavailable
    multiple_speakers_detected: bool | None
    overlapping_speech: bool | None


class StageTiming(BaseModel):
    stage: str
    duration_ms: float


class AnalysisResult(BaseModel):
    session_id: str
    clip_id: str
    status: Literal["completed", "failed"]
    audio_stats: AudioStats | None = None
    audio_analysis: AudioAnalysisSummary | None = None
    vad: VadAnalysis | None = None
    transcription: Transcription | None = None
    diarization: Diarization | None = None
    audio_classification: AudioClassification | None = None
    interpretation: LLMInterpretation | None = None
    events: list[AudioEvent] = Field(default_factory=list)
    stage_timings: list[StageTiming] = Field(default_factory=list)
    processing_time_ms: float
    error: str | None = None
