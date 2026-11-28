"""Service settings, read from environment variables (and ai-service/.env for local development).

Relative paths are resolved against the ai-service folder, never against the current working directory,
so the service behaves the same wherever it is started from.
"""
from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parents[1]


def _from_root(path: Path) -> Path:
    return (path if path.is_absolute() else SERVICE_ROOT / path).resolve()


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=SERVICE_ROOT / ".env", env_file_encoding="utf-8", extra="ignore")

    redis_connection: str = Field("localhost:6379", alias="REDIS_CONNECTION")
    redis_password: SecretStr | None = Field(None, alias="REDIS_PASSWORD")
    events_stream: str = Field("proctoring.audio.events.v1", alias="REDIS_EVENTS_STREAM")
    results_stream: str = Field("proctoring.audio.results.v1", alias="REDIS_RESULTS_STREAM")
    dlq_stream: str = Field("proctoring.audio.events.dlq.v1", alias="REDIS_DLQ_STREAM")
    consumer_group: str = Field("ai-audio-processors", alias="REDIS_AI_GROUP")
    stream_max_length: int = Field(100_000, alias="REDIS_STREAM_MAXLEN", ge=0)

    consumer_name: str | None = Field(None, alias="AI_CONSUMER_NAME")
    worker_concurrency: int = Field(1, alias="AI_WORKER_CONCURRENCY", ge=1, le=8)
    max_deliveries: int = Field(3, alias="AI_MAX_DELIVERIES", ge=1, le=20)
    claim_idle_ms: int = Field(60_000, alias="AI_CLAIM_IDLE_MS", ge=1000)

    audio_store_path: Path = Field(Path("../backend/src/Proctoring.Api/data/audio"), alias="AUDIO_STORE_PATH")

    vad_threshold: float = Field(0.5, alias="VAD_THRESHOLD", gt=0, lt=1)

    transcription_enabled: bool = Field(False, alias="TRANSCRIPTION_ENABLED")
    whisper_model: str = Field("base", alias="WHISPER_MODEL")
    whisper_device: Literal["cpu", "cuda"] = Field("cpu", alias="WHISPER_DEVICE")
    whisper_compute_type: str = Field("int8", alias="WHISPER_COMPUTE_TYPE")
    whisper_language: str | None = Field(None, alias="WHISPER_LANGUAGE")
    # "translate": Whisper always outputs English, whatever language was spoken.
    whisper_task: Literal["transcribe", "translate"] = Field("translate", alias="WHISPER_TASK")


    # Tier 2: every clip with transcribed speech is reviewed by an LLM.
    escalation_enabled: bool = Field(False, alias="ESCALATION_ENABLED")
    llm_base_url: str = Field("http://localhost:8000/v1", alias="LLM_BASE_URL")
    llm_model: str = Field("", alias="LLM_MODEL")
    llm_api_key: SecretStr | None = Field(None, alias="LLM_API_KEY")
    llm_timeout_seconds: float = Field(20.0, alias="LLM_TIMEOUT_SECONDS", gt=0)
    # Turns on Swagger at /docs.
    debug_analyze_endpoint_enabled: bool = Field(False, alias="DEBUG_ANALYZE_ENDPOINT_ENABLED")

    # How long a registered reference voice is kept in Redis.
    voice_reference_ttl_seconds: int = Field(7 * 24 * 3600, alias="VOICE_REFERENCE_TTL_SECONDS", gt=0)

    speaker_enabled: bool = Field(True, alias="SPEAKER_ENABLED")
    speaker_model_path: Path = Field(Path("models/speaker/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx"),
                                     alias="SPEAKER_MODEL_PATH")
    speaker_same_threshold: float = Field(0.4028, alias="SPEAKER_SAME_THRESHOLD", ge=-1, le=1)
    speaker_different_threshold: float = Field(0.3188, alias="SPEAKER_DIFFERENT_THRESHOLD", ge=-1, le=1)
    speaker_min_segment_seconds: float = Field(1.5, alias="SPEAKER_MIN_SEGMENT_SECONDS", gt=0)
    speaker_threads: int = Field(2, alias="SPEAKER_THREADS", ge=1, le=8)

    diarization_enabled: bool = Field(True, alias="DIARIZATION_ENABLED")
    diarization_model: str = Field("pyannote/speaker-diarization-community-1", alias="DIARIZATION_MODEL")
    diarization_revision: str = Field("3533c8cf8e369892e6b79ff1bf80f7b0286a54ee", alias="DIARIZATION_REVISION")
    diarization_min_speech_seconds: float = Field(1.0, alias="DIARIZATION_MIN_SPEECH_SECONDS", ge=0)
    diarization_min_speaker_seconds: float = Field(1.0, alias="DIARIZATION_MIN_SPEAKER_SECONDS", gt=0)
    diarization_merge_gap_seconds: float = Field(0.5, alias="DIARIZATION_MERGE_GAP_SECONDS", ge=0)
    diarization_min_overlap_seconds: float = Field(0.5, alias="DIARIZATION_MIN_OVERLAP_SECONDS", gt=0)
    diarization_threads: int = Field(4, alias="DIARIZATION_THREADS", ge=1, le=16)
    hf_home: Path = Field(Path("models/hf"), alias="HF_HOME")
    hf_hub_offline: bool = Field(False, alias="HF_HUB_OFFLINE")

    overlap_min_seconds: float = Field(1.0, alias="OVERLAP_MIN_SECONDS", gt=0)
    multiple_from_overlap: bool = Field(True, alias="MULTIPLE_FROM_OVERLAP")
    background_level_db: float = Field(10.0, alias="BACKGROUND_LEVEL_DB", gt=0)
    assistance_min_indicators: int = Field(2, alias="ASSISTANCE_MIN_INDICATORS", ge=2, le=3)
    assistance_require_transcript: bool = Field(True, alias="ASSISTANCE_REQUIRE_TRANSCRIPT")

    hf_token: SecretStr | None = Field(None, alias="HF_TOKEN")

    log_level: str = Field("INFO", alias="LOG_LEVEL")

    @property
    def hf_home_dir(self) -> Path:
        return _from_root(self.hf_home)

    @property
    def speaker_model_file(self) -> Path:
        return _from_root(self.speaker_model_path)

    @property
    def audio_root(self) -> Path:
        return _from_root(self.audio_store_path)

    @property
    def language(self) -> str | None:
        value = (self.whisper_language or "").strip().lower()
        return None if value in ("", "auto") else value


@lru_cache
def get_settings() -> Settings:
    return Settings()
