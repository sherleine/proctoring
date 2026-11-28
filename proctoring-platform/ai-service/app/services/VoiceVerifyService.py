"""Voice verify: one uploaded clip for one exam/candidate -> {"valid", "reason"} (+ the full AnalysisResult).

One pass of the full pipeline (ProcessingPipelineService: VAD -> diarization -> CAM++ -> ASR -> Tier 1
observations -> Tier 2 LLM review when enabled) answers both questions at once:

  1. Is this still the registered candidate's voice? (decide() below)
     - Exactly one voice, nothing registered yet for this examid+emailid -> valid, and it becomes the
       registered reference for the rest of the exam.
     - Exactly one voice matching the reference -> valid. Not matching -> violation (reference never overwritten).
     - Two or more distinct voices -> violation, always, even if one of them is the registered voice.
     - No speech / nothing to embed -> violation (an unreadable request is never "valid").
  2. Did the proctoring checks flag the clip? (ReviewSummaryService.summarize)

The clip is valid only if both pass; the reason lists every check that failed.
"""
import json
import logging
import secrets
import uuid
from datetime import datetime, timedelta, timezone
from hashlib import sha256

import numpy as np
import redis

from app.core.errors import AudioToolMissingError, InvalidAudioError, ServiceUnavailableError
from app.schemas.AudioEventSchema import AudioEventMessage, parse_event_entry
from app.services.ReviewSummaryService import summarize
from app.utils import audio_utils

log = logging.getLogger(__name__)

VIOLATION_NO_SPEECH = "No speech could be verified in this request."
VIOLATION_MULTIPLE = "Two or more distinct voices were detected in this request."
VIOLATION_MISMATCH = "The detected voice does not match the voice registered for this exam."
VALID_REGISTERED = "First valid request for this exam/candidate: this voice is now the registered reference."
VALID_MATCH = "Voice matches the registered reference voice for this exam."


def decide(distinct_voices: int | None, embedding: np.ndarray | None, reference, same_threshold: float) -> dict:
    """Pure decision, never touches storage. reference: a ReferenceVoice, or None if nothing is registered yet.
    Returns {"valid": bool, "reason": str, "register": bool}; the caller registers only when "register" is true,
    so a race between two "first" requests is resolved by the store's atomic SET NX, not by this logic."""
    if not distinct_voices or embedding is None:
        return {"valid": False, "reason": VIOLATION_NO_SPEECH, "register": False}
    if distinct_voices > 1:
        return {"valid": False, "reason": VIOLATION_MULTIPLE, "register": False}
    if reference is None:
        return {"valid": True, "reason": VALID_REGISTERED, "register": True}
    similarity = float(embedding @ reference.embedding)
    if similarity >= same_threshold:
        return {"valid": True, "reason": VALID_MATCH, "register": False}
    return {"valid": False, "reason": f"{VIOLATION_MISMATCH} (similarity {similarity:.3f}, threshold {same_threshold:.3f})",
            "register": False}


def _iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def _build_message(exam_id: str, email_id: str, audio_bytes: bytes, duration_seconds: float) -> AudioEventMessage:
    """Wraps the upload in the same validated event message the Redis worker receives, so the pipeline runs
    unchanged. Nothing is written to the audio store: the bytes are analysed in memory."""
    session_id = "ses_" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(26))
    event_id = str(uuid.uuid4())
    digest = sha256(audio_bytes).hexdigest()
    key = f"{session_id}/{secrets.token_hex(16)}.wav"
    start = datetime.now(timezone.utc)
    payload = {
        "sessionId": session_id, "candidateId": email_id, "examId": exam_id,
        "event": {
            "schemaVersion": "1.0", "eventId": event_id, "eventType": "speech", "sequenceNumber": 0,
            "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
            "startedAt": _iso(start), "endedAt": _iso(start + timedelta(seconds=duration_seconds)),
            "speechStartedAt": _iso(start), "durationSeconds": round(duration_seconds, 3), "preEventSeconds": 0,
            "trigger": {"type": "vad", "model": "manual-upload", "startThreshold": 0.5, "endThreshold": 0.35,
                        "maxProbability": 1.0, "speechSeconds": round(duration_seconds, 3), "noiseFloorDbfs": -60.0},
            "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": audio_utils.TARGET_SAMPLE_RATE,
                      "channels": 1, "sizeBytes": len(audio_bytes), "sha256": digest},
            "video": {"recordedLocally": False, "uploaded": False},
        },
        "audioRef": {"storage": "file", "uri": f"file:///voice-verify/{key}", "sha256": digest,
                     "sizeBytes": len(audio_bytes), "contentType": "audio/wav", "key": key},
    }
    entry = {"messageType": "audio.event.submitted", "schemaVersion": "1", "messageId": str(uuid.uuid4()),
             "correlationId": event_id, "sessionId": session_id, "eventId": event_id,
             "producedAt": _iso(datetime.now(timezone.utc)), "payload": json.dumps(payload)}
    return parse_event_entry(entry)


def _check_voice(runtime, exam_id: str, email_id: str, distinct_voices, embedding) -> dict:
    store, same = runtime.voice_reference_store, runtime.scale.same
    decision = decide(distinct_voices, embedding, store.get(exam_id, email_id), same)
    if decision["register"] and not store.register_if_absent(exam_id, email_id, embedding, runtime.speaker.model_id):
        # another request for the same examid+emailid registered first, a moment ago - re-decide against
        # whatever it registered rather than claiming "valid" against a reference that lost the race
        decision = decide(distinct_voices, embedding, store.get(exam_id, email_id), same)
    return decision


def verify(runtime, exam_id: str, email_id: str, raw: bytes, filename: str, verbose: bool = False) -> dict:
    """Raises InvalidAudioError (bad upload), AudioToolMissingError (no ffmpeg), ServiceUnavailableError
    (model off / Redis down), and lets the pipeline's PermanentEventError/TransientEventError through."""
    if runtime.diarizer is None or runtime.speaker is None:
        raise ServiceUnavailableError("Voice verification needs diarization and the speaker model both enabled "
                                      "(DIARIZATION_ENABLED, speaker model) - at least one is off")
    if not raw:
        raise InvalidAudioError("voice file is empty")
    try:
        wav_bytes = audio_utils.to_16k_mono_wav(raw, filename) if audio_utils.needs_conversion(raw, filename) else raw
        duration_seconds = audio_utils.wav_duration_seconds(wav_bytes)
    except FileNotFoundError as exc:
        raise AudioToolMissingError("ffmpeg is not installed / not on PATH - install ffmpeg to accept uploads here, "
                                    "or upload a 16 kHz mono PCM16 .wav directly") from exc
    except ValueError as exc:
        raise InvalidAudioError(str(exc)) from exc

    message = _build_message(exam_id, email_id, wav_bytes, duration_seconds)
    result, main_embedding = runtime.pipeline.analyse(message, wav_bytes)
    payload = result.to_payload()
    try:
        voice_check = _check_voice(runtime, exam_id, email_id, result.diarization.distinctVoices, main_embedding)
    except redis.RedisError as exc:
        raise ServiceUnavailableError(f"Reference-voice storage (Redis) is unavailable: {type(exc).__name__}") from exc
    proctoring = summarize(payload)

    valid = voice_check["valid"] and proctoring["valid"]
    reason = voice_check["reason"] if valid else \
        " ".join(check["reason"] for check in (voice_check, proctoring) if not check["valid"])
    log.info("Voice verification decided", extra={"examId": exam_id, "emailId": email_id,
                                                   "distinctVoices": result.diarization.distinctVoices,
                                                   "voiceValid": voice_check["valid"],
                                                   "proctoringValid": proctoring["valid"], "valid": valid})
    response = {"valid": valid, "reason": reason}
    return {**response, "result": payload} if verbose else response
