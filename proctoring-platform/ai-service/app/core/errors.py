

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
