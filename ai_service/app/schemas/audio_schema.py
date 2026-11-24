from datetime import datetime

from pydantic import BaseModel, Field


class FrontendVadInfo(BaseModel):
    """What the browser VAD measured for this clip. Used for comparison only, never trusted."""

    speech_frames: int = Field(ge=0)
    total_frames: int = Field(ge=0)
    max_probability: float = Field(ge=0, le=1)
    mean_speech_probability: float | None = Field(default=None, ge=0, le=1)
    noise_floor_db: float | None = None
    start_threshold: float | None = None
    end_threshold: float | None = None


class ClipMetadata(BaseModel):
    """Metadata the backend sends alongside each clip."""

    session_id: str
    clip_id: str
    sequence_number: int = Field(ge=0)
    start_timestamp: datetime
    end_timestamp: datetime
    audio_format: str
    sample_rate: int = Field(gt=0)
    channels: int = Field(gt=0)
    frontend_vad: FrontendVadInfo | None = None


class AudioStats(BaseModel):
    """Measured properties of the decoded clip (after resampling to 16 kHz mono)."""

    duration_seconds: float
    original_sample_rate: int
    original_channels: int
    rms_dbfs: float
    peak_dbfs: float
    clipping_ratio: float
    normalization_gain_db: float
