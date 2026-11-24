"""Central exception hierarchy for the AI service."""

from typing import Any


class AIServiceError(Exception):
    status_code = 500
    error_code = "AI_INTERNAL_ERROR"

    def __init__(self, message: str, *, details: dict[str, Any] | None = None):
        super().__init__(message)
        self.message = message
        self.details = details or {}


class InvalidAudioError(AIServiceError):
    """The clip could not be decoded, is empty, or violates the audio contract."""

    status_code = 422
    error_code = "INVALID_AUDIO"


class ModelNotAvailableError(AIServiceError):
    """A required model is not loaded (missing dependency, download or credentials)."""

    status_code = 503
    error_code = "MODEL_NOT_AVAILABLE"


class ModelInferenceError(AIServiceError):
    status_code = 500
    error_code = "MODEL_INFERENCE_FAILED"


class LLMUnavailableError(AIServiceError):
    """The optional company LLM could not be reached. The pipeline falls back to rules."""

    status_code = 503
    error_code = "LLM_UNAVAILABLE"
