"""POST /voice/verify - multipart form (examid, emailid, voice file[, verbose]) -> {"valid", "reason"}
(+ "result": the full AnalysisResult when verbose=true). The logic lives in app/services/VoiceVerifyService.py."""
from fastapi import APIRouter, Form, HTTPException, Request, UploadFile

from app.core.errors import (AudioToolMissingError, InvalidAudioError, PermanentEventError, ServiceUnavailableError,
                             TransientEventError)
from app.schemas.VoiceVerifySchema import VoiceVerifyResponse
from app.services import VoiceVerifyService

router = APIRouter(prefix="/voice", tags=["voice"])


@router.post("/verify", response_model=VoiceVerifyResponse, response_model_exclude_none=True)
async def verify(request: Request, examid: str = Form(...), emailid: str = Form(...),
                 voice: UploadFile = None,
                 verbose: bool = Form(False, description="Also return the full AnalysisResult under `result`")) -> dict:
    if voice is None:
        raise HTTPException(422, "voice file is required")
    raw = await voice.read()
    try:
        return VoiceVerifyService.verify(request.app.state.runtime, examid, emailid, raw, voice.filename or "upload",
                                         verbose)
    except InvalidAudioError as exc:
        raise HTTPException(400, str(exc))
    except AudioToolMissingError as exc:
        raise HTTPException(500, str(exc))
    except PermanentEventError as exc:
        raise HTTPException(422, f"{exc.code}: {exc.message}")
    except (ServiceUnavailableError, TransientEventError) as exc:
        raise HTTPException(503, str(exc))
