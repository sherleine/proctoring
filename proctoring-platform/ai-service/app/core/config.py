"""Service settings, read from environment variables (and ai-service/.env for local development).

Relative paths are resolved against the ai-service folder, never against the current working directory,
so the service behaves the same wherever it is started from.
"""
from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parents[2]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=SERVICE_ROOT / ".env", env_file_encoding="utf-8", extra="ignore")

    # Redis (names from docs/contracts/redis-streams.v1.md)
    redis_connection: str = Field("localhost:6379", alias="REDIS_CONNECTION")
    redis_password: SecretStr | None = Field(None, alias="REDIS_PASSWORD")
    events_stream: str = Field("proctoring.audio.events.v1", alias="REDIS_EVENTS_STREAM")
    results_stream: str = Field("proctoring.audio.results.v1", alias="REDIS_RESULTS_STREAM")
    dlq_stream: str = Field("proctoring.audio.events.dlq.v1", alias="REDIS_DLQ_STREAM")
    consumer_group: str = Field("ai-audio-processors", alias="REDIS_AI_GROUP")
    stream_max_length: int = Field(100_000, alias="REDIS_STREAM_MAXLEN", ge=0)

    # Worker
    consumer_name: str | None = Field(None, alias="AI_CONSUMER_NAME")
    worker_concurrency: int = Field(1, alias="AI_WORKER_CONCURRENCY", ge=1, le=8)
    max_deliveries: int = Field(3, alias="AI_MAX_DELIVERIES", ge=1, le=20)
    claim_idle_ms: int = Field(60_000, alias="AI_CLAIM_IDLE_MS", ge=1000)

    # Shared audio storage (the backend's AUDIO_STORE_PATH, mounted read-only)
    audio_store_path: Path = Field(Path("../backend/src/Proctoring.Api/data/audio"), alias="AUDIO_STORE_PATH")

    # Models
    vad_threshold: float = Field(0.5, alias="VAD_THRESHOLD", gt=0, lt=1)
    # Optional speech-to-text. Off by default: the POC only needs the speech audio; events complete without it
    # and the Whisper model is then not even loaded.
    transcription_enabled: bool = Field(False, alias="TRANSCRIPTION_ENABLED")
    whisper_model: str = Field("base", alias="WHISPER_MODEL")
    whisper_device: Literal["cpu", "cuda"] = Field("cpu", alias="WHISPER_DEVICE")
    whisper_compute_type: str = Field("int8", alias="WHISPER_COMPUTE_TYPE")
    whisper_language: str | None = Field(None, alias="WHISPER_LANGUAGE")

    log_level: str = Field("INFO", alias="LOG_LEVEL")

    @property
    def audio_root(self) -> Path:
        path = self.audio_store_path
        return (path if path.is_absolute() else SERVICE_ROOT / path).resolve()

    @property
    def language(self) -> str | None:
        value = (self.whisper_language or "").strip().lower()
        return None if value in ("", "auto") else value


@lru_cache
def get_settings() -> Settings:
    return Settings()
