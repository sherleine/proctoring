"""Debug/demo endpoint: upload a single audio (or video) clip directly and get back the same AnalysisResult
the Redis worker would have produced for it, plus a simple valid/invalid + reason verdict for a quick look.

This is NOT how the real system works: in production, audio only ever arrives through the backend and Redis
(see app/main.py's docstring) - there is no real "question context" or session continuity here either, each
upload is reviewed on its own. This endpoint exists purely so the pipeline's output can be demoed/tested
through Swagger without standing up the backend + Redis + a real exam session. Only registered when
DEBUG_ANALYZE_ENDPOINT_ENABLED=true (see app/main.py).
"""
import hashlib
import io
import json
import re
import secrets
import subprocess
import tempfile
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import soundfile as sf
from fastapi import APIRouter, Form, HTTPException, Request, UploadFile

from app.core.errors import PermanentEventError, TransientEventError
from app.schemas.audio_event import parse_event_entry
from app.services.question_context import QuestionContext
from app.services.review_summary import summarize

router = APIRouter(prefix="/debug", tags=["debug"])

TARGET_SAMPLE_RATE = 16_000
SESSION_ID_PATTERN = re.compile(r"^ses_[A-Za-z0-9]{20,36}$")  # same shape audio_loader.KEY_PATTERN expects


def _new_session_id() -> str:
    alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    return "ses_" + "".join(secrets.choice(alphabet) for _ in range(26))


def _iso(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def _needs_conversion(data: bytes, filename: str) -> bool:
    if Path(filename or "").suffix.lower() != ".wav":
        return True
    try:
        info = sf.info(io.BytesIO(data))
    except Exception:
        return True
    return info.samplerate != TARGET_SAMPLE_RATE or info.channels != 1 or info.subtype != "PCM_16"


def _to_16k_mono_wav(data: bytes, filename: str) -> bytes:
    """ffmpeg converts whatever was uploaded (wav of any rate/channels, mp3, mp4, mov, webm ...) into the
    16 kHz mono PCM16 WAV the pipeline expects. Requires ffmpeg on PATH."""
    suffix = Path(filename or "").suffix or ".bin"
    with tempfile.TemporaryDirectory() as tmp:
        src, dst = Path(tmp) / f"input{suffix}", Path(tmp) / "output.wav"
        src.write_bytes(data)
        try:
            subprocess.run(["ffmpeg", "-y", "-i", str(src), "-ar", str(TARGET_SAMPLE_RATE), "-ac", "1", "-f", "wav", str(dst)],
                          check=True, capture_output=True, timeout=120)
        except FileNotFoundError:
            raise HTTPException(500, "ffmpeg is not installed / not on PATH - install ffmpeg to accept uploads here, "
                                     "or upload a 16 kHz mono PCM16 .wav directly")
        except subprocess.CalledProcessError as exc:
            detail = exc.stderr.decode(errors="replace")[-500:] if exc.stderr else str(exc)
            raise HTTPException(400, f"Could not decode the uploaded file as audio/video: {detail}")
        return dst.read_bytes()


def _build_event_entry(session_id: str, candidate_id: str, exam_id: str, event_id: str,
                       audio_bytes: bytes, duration_seconds: float) -> tuple[dict, str]:
    sha = hashlib.sha256(audio_bytes).hexdigest()
    key = f"{session_id}/{secrets.token_hex(16)}.wav"
    start = datetime.now(timezone.utc)
    payload = {
        "sessionId": session_id, "candidateId": candidate_id, "examId": exam_id,
        "event": {
            "schemaVersion": "1.0", "eventId": event_id, "eventType": "speech", "sequenceNumber": 0,
            "part": {"groupId": str(uuid.uuid4()), "index": 0, "isFinal": True, "overlapSeconds": 0},
            "startedAt": _iso(start), "endedAt": _iso(start + timedelta(seconds=duration_seconds)),
            "speechStartedAt": _iso(start), "durationSeconds": round(duration_seconds, 3), "preEventSeconds": 0,
            "trigger": {"type": "vad", "model": "manual-upload", "startThreshold": 0.5, "endThreshold": 0.35,
                       "maxProbability": 1.0, "speechSeconds": round(duration_seconds, 3), "noiseFloorDbfs": -60.0},
            "audio": {"format": "wav", "codec": "pcm_s16le", "sampleRate": TARGET_SAMPLE_RATE, "channels": 1,
                     "sizeBytes": len(audio_bytes), "sha256": sha},
            "video": {"recordedLocally": False, "uploaded": False},
        },
        "audioRef": {"storage": "file", "uri": f"file:///manual-upload/{key}", "sha256": sha,
                    "sizeBytes": len(audio_bytes), "contentType": "audio/wav", "key": key},
    }
    entry = {"messageType": "audio.event.submitted", "schemaVersion": "1", "messageId": str(uuid.uuid4()),
            "correlationId": event_id, "sessionId": session_id, "eventId": event_id,
            "producedAt": _iso(datetime.now(timezone.utc)), "payload": json.dumps(payload)}
    return entry, key


@router.post("/analyze")
async def analyze_clip(request: Request, file: UploadFile,
                       question_text: str | None = Form(None, description="Exam question on screen during this clip (optional - stands in for the real question-context backend, which doesn't exist yet)"),
                       session_id: str | None = Form(None), candidate_id: str | None = Form(None),
                       exam_id: str | None = Form(None),
                       verbose: bool = Form(False, description="Also return the full AnalysisResult under `result`")) -> dict:
    """Upload one audio or video clip; get back {valid, reason}. With verbose=true, also `result`: the full
    AnalysisResult the real pipeline would have produced - `valid`/`reason` are a convenience summary of it
    (see app/services/review_summary.py), not a separate judgement."""
    runtime = request.app.state.runtime
    if not runtime.store.is_available():
        raise HTTPException(503, "Audio store is not available")

    raw = await file.read()
    if not raw:
        raise HTTPException(400, "Uploaded file is empty")

    wav_bytes = _to_16k_mono_wav(raw, file.filename or "upload") if _needs_conversion(raw, file.filename or "") else raw
    try:
        duration_seconds = sf.info(io.BytesIO(wav_bytes)).duration
    except Exception as exc:
        raise HTTPException(400, f"Converted file is not valid audio: {exc}")

    session_id = session_id or _new_session_id()
    if not SESSION_ID_PATTERN.fullmatch(session_id):
        raise HTTPException(400, "session_id must look like 'ses_' + 20-36 letters/digits, or be left empty to "
                                 "generate one (in Swagger, untick/clear the 'string' placeholder)")
    candidate_id = candidate_id or "manual-upload"
    exam_id = exam_id or "manual-upload"
    event_id = str(uuid.uuid4())

    entry, key = _build_event_entry(session_id, candidate_id, exam_id, event_id, wav_bytes, duration_seconds)
    try:
        path = runtime.store.resolve(key)
    except PermanentEventError as exc:
        raise HTTPException(400, f"{exc.code}: {exc.message}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(wav_bytes)

    try:
        message = parse_event_entry(entry)
    except PermanentEventError as exc:
        raise HTTPException(400, f"{exc.code}: {exc.message}")

    question = QuestionContext(question_text) if question_text else None
    try:
        result = runtime.pipeline.process(message, question_override=question)
    except PermanentEventError as exc:
        raise HTTPException(422, f"{exc.code}: {exc.message}")
    except TransientEventError as exc:
        raise HTTPException(503, str(exc))

    payload = result.to_payload()
    summary = summarize(payload)
    return {**summary, "result": payload} if verbose else summary
