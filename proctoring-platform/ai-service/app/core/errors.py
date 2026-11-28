

class PermanentEventError(Exception):
    """Retrying cannot help (missing or invalid audio, checksum mismatch, invalid message).
    The worker publishes `audio.analysis.failed` with this code and acknowledges the message."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


class TransientEventError(Exception):
    """May succeed later (model error, out of memory). The message is not acknowledged; it is retried
    until AI_MAX_DELIVERIES and then dead-lettered."""


class InvalidAudioError(Exception):
    """An uploaded file is empty or could not be decoded as audio/video (the caller's input is wrong)."""


class AudioToolMissingError(Exception):
    """ffmpeg is needed to convert an upload but is not installed / not on PATH (a server setup problem)."""


class ServiceUnavailableError(Exception):
    """A dependency this request needs is off or unreachable (a model is disabled, Redis is down)."""
