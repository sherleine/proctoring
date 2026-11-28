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
import logging
import uuid

import numpy as np
import redis

from app.core.errors import AudioToolMissingError, InvalidAudioError, ServiceUnavailableError
from app.services.ReviewSummaryService import summarize
from app.utils import audio_utils

log = logging.getLogger(__name__)

VIOLATION_NO_SPEECH = "No speech could be verified in this request."
VIOLATION_MULTIPLE = "Two or more distinct voices were detected in this request."
VIOLATION_MISMATCH = "The detected voice does not match the voice registered for this exam."
VALID_REGISTERED = "First valid request for this exam/candidate: this voice is now the registered reference."
VALID_MATCH = "Voice matches the registered reference voice for this exam."


def decide(distinct_voices: int | None, embedding: np.ndarray | None, reference: np.ndarray | None,
           same_threshold: float) -> dict:
    """Pure decision, never touches storage. reference: the registered embedding, or None if nothing is registered.
    Returns {"valid": bool, "reason": str, "register": bool}; the caller registers only when "register" is true,
    so a race between two "first" requests is resolved by the store's atomic SET NX, not by this logic."""
    if not distinct_voices or embedding is None:
        return {"valid": False, "reason": VIOLATION_NO_SPEECH, "register": False}
    if distinct_voices > 1:
        return {"valid": False, "reason": VIOLATION_MULTIPLE, "register": False}
    if reference is None:
        return {"valid": True, "reason": VALID_REGISTERED, "register": True}
    similarity = float(embedding @ reference)
    if similarity >= same_threshold:
        return {"valid": True, "reason": VALID_MATCH, "register": False}
    return {"valid": False, "reason": f"{VIOLATION_MISMATCH} (similarity {similarity:.3f}, threshold {same_threshold:.3f})",
            "register": False}


def _check_voice(runtime, exam_id: str, email_id: str, distinct_voices, embedding) -> dict:
    store, same = runtime.voice_reference_store, runtime.scale.same
    decision = decide(distinct_voices, embedding, store.get(exam_id, email_id), same)
    if decision["register"] and not store.register_if_absent(exam_id, email_id, embedding, runtime.speaker.model_id):
        # another request for the same examid+emailid registered first, a moment ago - re-decide against
        # whatever it registered rather than claiming "valid" against a reference that lost the race
        decision = decide(distinct_voices, embedding, store.get(exam_id, email_id), same)
    return decision


def verify(runtime, exam_id: str, email_id: str, raw: bytes, filename: str, verbose: bool = False) -> dict:
    """Raises InvalidAudioError, AudioToolMissingError, ServiceUnavailableError, and lets the pipeline's
    PermanentEventError/TransientEventError through. Nothing is written to disk: the upload is analysed in memory."""
    if runtime.diarizer is None or runtime.speaker is None:
        raise ServiceUnavailableError("Voice verification needs diarization and the speaker model both enabled "
                                      "(DIARIZATION_ENABLED, speaker model) - at least one is off")
    if not raw:
        raise InvalidAudioError("voice file is empty")
    try:
        wav_bytes = audio_utils.to_16k_mono_wav(raw, filename) if audio_utils.needs_conversion(raw, filename) else raw
    except FileNotFoundError as exc:
        raise AudioToolMissingError("ffmpeg is not installed / not on PATH - install ffmpeg to accept uploads here, "
                                    "or upload a 16 kHz mono PCM16 .wav directly") from exc
    except ValueError as exc:
        raise InvalidAudioError(str(exc)) from exc

    result, main_embedding = runtime.pipeline.analyse(wav_bytes, f"ses_{uuid.uuid4().hex}", str(uuid.uuid4()), email_id)
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
