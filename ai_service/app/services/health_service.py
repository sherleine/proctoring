import time

import psutil

from app.config import Settings
from app.utils.dependency_check import package_status

SERVICE_VERSION = "0.1.0"

# component -> (import name, distribution name)
_PACKAGES = {
    "vad": ("silero_vad", "silero-vad"),
    "transcription": ("faster_whisper", "faster-whisper"),
    "diarization": ("pyannote.audio", "pyannote.audio"),
    "audio_classification": ("tensorflow", "tensorflow"),
}


class HealthService:
    """Reports service health, resource usage and the state of each AI component.

    Model loading is added in Phase 6; until then each component reports
    ``loaded: false`` together with whether its package is installed.
    """

    def __init__(self, settings: Settings):
        self._settings = settings
        self._started = time.monotonic()
        self._process = psutil.Process()
        self._process.cpu_percent(None)

    def _components(self) -> dict[str, dict]:
        s = self._settings
        enabled = {
            "vad": True,
            "transcription": True,
            "diarization": s.diarization_enabled,
            "audio_classification": s.audio_classification_enabled,
        }
        provider = {
            "vad": s.vad_provider,
            "transcription": f"{s.transcription_provider}:{s.whisper_model}",
            "diarization": f"{s.diarization_provider}:{s.pyannote_pipeline}",
            "audio_classification": s.audio_classification_provider,
        }
        components = {}
        for name, (import_name, dist) in _PACKAGES.items():
            components[name] = {
                "enabled": enabled[name],
                "provider": provider[name],
                "package": package_status(import_name, dist),
                "loaded": False,
            }
        components["diarization"]["credentials_configured"] = bool(s.hf_token and s.hf_token.get_secret_value().strip())
        components["llm"] = {
            "enabled": s.llm_enabled,
            "provider": s.llm_provider,
            "configured": bool(s.llm_base_url and s.llm_model),
            "loaded": False,
        }
        return components

    def get_health(self) -> dict:
        return {
            "service": "ai_service",
            "status": "ok",
            "version": SERVICE_VERSION,
            "uptime_seconds": round(time.monotonic() - self._started, 1),
            "process_memory_mb": round(self._process.memory_info().rss / 2**20, 1),
            "process_cpu_percent": self._process.cpu_percent(None),
            "components": self._components(),
        }
