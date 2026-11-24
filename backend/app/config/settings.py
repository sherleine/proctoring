"""Central configuration for the backend API.

Every tunable value is read from environment variables (or the repository-root
``.env`` file). Nothing else in the backend should read ``os.environ`` directly.
"""

from functools import lru_cache
from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

REPO_ROOT = Path(__file__).resolve().parents[3]


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=REPO_ROOT / ".env",
        env_file_encoding="utf-8",
        extra="ignore",
        case_sensitive=False,
    )

    # Server
    backend_host: str = "0.0.0.0"
    backend_port: int = 8000
    log_level: str = "INFO"
    cors_origins: list[str] = Field(default_factory=lambda: ["http://localhost:5173"])

    # AI service communication
    ai_service_url: str = "http://127.0.0.1:8001"
    ai_health_timeout_seconds: float = 3.0
    ai_request_timeout_seconds: float = 120.0

    # Audio contract shared with the frontend
    audio_sample_rate: int = 16000
    audio_channels: int = 1
    max_clip_duration: float = 10.0  # seconds

    # Storage / retention
    data_dir: Path = REPO_ROOT / "data" / "backend"


@lru_cache
def get_settings() -> Settings:
    return Settings()
