"""/health/live and /health/ready. No processing logic here; nothing secret or path-like in the output."""
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

router = APIRouter(tags=["health"])


@router.get("/health/live")
def live() -> dict:
    return {"status": "live"}


@router.get("/health/ready")
def ready(request: Request) -> JSONResponse:
    runtime = request.app.state.runtime
    checks = {
        "configuration": "ok",  # settings were validated at startup
        "redis": "ok" if runtime.streams.ping() else "unreachable",
        "audioStore": "ok" if runtime.store.is_available() else "unavailable",
        "vadModel": "loaded" if runtime.vad.loaded else ("failed" if runtime.model_error else "loading"),
        "whisperModel": "disabled" if runtime.transcriber is None
        else "loaded" if runtime.transcriber.loaded else ("failed" if runtime.model_error else "loading"),
        "speakerModel": "disabled" if getattr(runtime, "speaker", None) is None
        else "loaded" if runtime.speaker.loaded else ("failed" if runtime.model_error else "loading"),
        "diarizationModel": "disabled" if getattr(runtime, "diarizer", None) is None
        else "loaded" if runtime.diarizer.loaded else ("failed" if runtime.model_error else "loading"),
        "workers": sorted({w.state for w in runtime.workers}),
    }
    is_ready = (checks["redis"] == "ok" and checks["audioStore"] == "ok"
                and checks["vadModel"] == "loaded" and checks["whisperModel"] in ("loaded", "disabled")
                and checks["speakerModel"] in ("loaded", "disabled")
                and checks["diarizationModel"] in ("loaded", "disabled"))
    body = {
        "status": "ready" if is_ready else "not-ready",
        "checks": checks,
        "models": {"vad": runtime.vad.model_id, "asr": runtime.transcriber.model_id if runtime.transcriber else None,
                   "speaker": runtime.speaker.model_id if getattr(runtime, "speaker", None) else None,
                   "diarization": runtime.diarizer.model_id if getattr(runtime, "diarizer", None) else None},
        "transcription": runtime.transcriber is not None,
        "whisperDevice": runtime.transcriber.device if runtime.transcriber else None,
        "whisperComputeType": runtime.transcriber.compute_type if runtime.transcriber else None,
    }
    if runtime.model_error:
        body["reason"] = runtime.model_error
    return JSONResponse(body, status_code=200 if is_ready else 503)
