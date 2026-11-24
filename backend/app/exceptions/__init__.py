"""Central exception hierarchy for the backend.

Services raise these; ``app.exceptions.handlers`` converts them into a
consistent JSON error response. Routes never build error payloads themselves.
"""

from typing import Any


class AppError(Exception):
    status_code = 500
    error_code = "INTERNAL_ERROR"

    def __init__(self, message: str, *, details: dict[str, Any] | None = None):
        super().__init__(message)
        self.message = message
        self.details = details or {}


class ValidationFailedError(AppError):
    status_code = 422
    error_code = "VALIDATION_FAILED"


class NotFoundError(AppError):
    status_code = 404
    error_code = "NOT_FOUND"


class ConflictError(AppError):
    status_code = 409
    error_code = "CONFLICT"


class PayloadTooLargeError(AppError):
    status_code = 413
    error_code = "PAYLOAD_TOO_LARGE"


class UnsupportedMediaError(AppError):
    status_code = 415
    error_code = "UNSUPPORTED_MEDIA_TYPE"


class UpstreamUnavailableError(AppError):
    """The AI service could not be reached or returned a server error."""

    status_code = 503
    error_code = "AI_SERVICE_UNAVAILABLE"
