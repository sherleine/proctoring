"""/health/live and /health/ready."""
from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

router = APIRouter(tags=["health"])


@router.get("/health/live")
def live() -> dict:
    return {"status": "live"}


@router.get("/health/ready")
def ready(request: Request) -> JSONResponse:
    runtime = request.app.state.runtime

    def model_state(model) -> str:
        if model is None:
            return "disabled"
        return "loaded" if model.loaded else ("failed" if runtime.model_error else "loading")

    checks = {
        "redis": "ok" if runtime.streams.ping() else "unreachable",
        "audioStore": "ok" if runtime.store.is_available() else "unavailable",
        "vadModel": model_state(runtime.vad),
        "whisperModel": model_state(runtime.transcriber),
        "speakerModel": model_state(runtime.speaker),
        "diarizationModel": model_state(runtime.diarizer),
        "workers": sorted({w.state for w in runtime.workers}),
    }
    is_ready = (checks["redis"] == "ok" and checks["audioStore"] == "ok" and checks["vadModel"] == "loaded"
                and all(checks[m] in ("loaded", "disabled") for m in ("whisperModel", "speakerModel", "diarizationModel")))
    body = {
        "status": "ready" if is_ready else "not-ready",
        "checks": checks,
        "models": {"vad": runtime.vad.model_id, "asr": runtime.transcriber.model_id if runtime.transcriber else None,
                   "speaker": runtime.speaker.model_id if runtime.speaker else None,
                   "diarization": runtime.diarizer.model_id if runtime.diarizer else None},
        "transcription": runtime.transcriber is not None,
        "whisperDevice": runtime.transcriber.device if runtime.transcriber else None,
        "whisperComputeType": runtime.transcriber.compute_type if runtime.transcriber else None,
    }
    if runtime.model_error:
        body["reason"] = runtime.model_error
    return JSONResponse(body, status_code=200 if is_ready else 503)
