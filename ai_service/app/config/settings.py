"""Central configuration for the AI processing service.

All model selection, thresholds, URLs and credentials come from environment
variables (or the repository-root ``.env``). Nothing else reads ``os.environ``.
"""

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import SecretStr
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
    ai_service_host: str = "0.0.0.0"
    ai_service_port: int = 8001
    log_level: str = "INFO"
    # Never log transcripts unless explicitly enabled for debugging.
    log_transcripts: bool = False

    # Audio contract
    audio_sample_rate: int = 16000
    audio_channels: int = 1
    max_clip_duration: float = 10.0

    # Compute
    torch_num_threads: int = 4

    # VAD (server-side verification)
    vad_provider: Literal["silero"] = "silero"
    server_vad_threshold: float = 0.5
    server_vad_min_speech_ms: int = 250
    server_vad_min_silence_ms: int = 100

    # Transcription
    transcription_provider: Literal["faster_whisper"] = "faster_whisper"
    whisper_model: str = "base"
    whisper_device: Literal["cpu", "cuda", "auto"] = "cpu"
    whisper_compute_type: str = "int8"
    whisper_language: str | None = None  # None = auto-detect
    whisper_beam_size: int = 5

    # Diarization
    diarization_enabled: bool = True
    diarization_provider: Literal["pyannote"] = "pyannote"
    pyannote_pipeline: str = "pyannote/speaker-diarization-community-1"
    hf_token: SecretStr | None = None

    # Environmental audio classification
    audio_classification_enabled: bool = True
    audio_classification_provider: Literal["yamnet", "panns", "ast"] = "yamnet"

    # Company LLM (optional contextual interpretation)
    llm_enabled: bool = False
    llm_provider: Literal["company_local"] = "company_local"
    llm_base_url: str | None = None
    llm_model: str | None = None
    llm_api_key: SecretStr | None = None
    llm_timeout_seconds: float = 30.0

    # Model cache location (Hugging Face / whisper downloads)
    model_cache_dir: Path = REPO_ROOT / "data" / "models"


@lru_cache
def get_settings() -> Settings:
    return Settings()
