"""The audio event message the Redis worker receives, validated against the stream contracts."""
import json

from pydantic import BaseModel

from app.core.errors import PermanentEventError
from app.schemas.ContractSchema import ENVELOPE, EVENT_MESSAGE, validation_errors

SUBMITTED = "audio.event.submitted"
SUPPORTED_ENVELOPE_VERSION = "1"


class AudioRef(BaseModel):
    storage: str
    uri: str
    sha256: str
    sizeBytes: int
    contentType: str
    key: str | None = None


class AudioEventMessage(BaseModel):
    """Envelope + payload of one event, after contract validation."""
    sessionId: str
    eventId: str
    candidateId: str
    examId: str
    audioRef: AudioRef


def parse_event_entry(fields: dict[str, str]) -> AudioEventMessage:
    """Validates a stream entry against the envelope and payload schemas.
    Raises PermanentEventError(INVALID_MESSAGE) for anything that does not match the contract."""
    errors = validation_errors(ENVELOPE, fields)
    if errors:
        raise PermanentEventError("INVALID_MESSAGE", "Envelope violates the contract: " + "; ".join(errors[:3]))
    if fields["messageType"] != SUBMITTED:
        raise PermanentEventError("INVALID_MESSAGE", f"Unexpected messageType '{fields['messageType']}'")
    if fields["schemaVersion"] != SUPPORTED_ENVELOPE_VERSION:
        raise PermanentEventError("INVALID_MESSAGE", f"Unsupported schemaVersion '{fields['schemaVersion']}'")
    try:
        payload = json.loads(fields["payload"])
    except json.JSONDecodeError as exc:
        raise PermanentEventError("INVALID_MESSAGE", "Payload is not valid JSON") from exc
    errors = validation_errors(EVENT_MESSAGE, payload)
    if errors:
        raise PermanentEventError("INVALID_MESSAGE", "Payload violates the contract: " + "; ".join(errors[:3]))
    if payload["sessionId"] != fields["sessionId"] or payload["event"]["eventId"] != fields["eventId"]:
        raise PermanentEventError("INVALID_MESSAGE", "Payload sessionId/eventId do not match the envelope")
    if not payload["audioRef"].get("key"):
        raise PermanentEventError("INVALID_MESSAGE", "audioRef.key is required by this service")
    return AudioEventMessage(sessionId=fields["sessionId"], eventId=fields["eventId"],
                             candidateId=payload["candidateId"], examId=payload["examId"],
                             audioRef=AudioRef(**payload["audioRef"]))
