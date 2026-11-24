import logging

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.exceptions import AIServiceError
from app.schemas.event_schema import ErrorResponse

logger = logging.getLogger(__name__)


def register_exception_handlers(app: FastAPI) -> None:
    @app.exception_handler(AIServiceError)
    async def handle_service_error(request: Request, exc: AIServiceError) -> JSONResponse:
        logger.warning("%s: %s", exc.error_code, exc.message)
        body = ErrorResponse(
            error_code=exc.error_code,
            message=exc.message,
            details=exc.details,
            request_id=getattr(request.state, "request_id", None),
        )
        return JSONResponse(status_code=exc.status_code, content=body.model_dump())

    @app.exception_handler(RequestValidationError)
    async def handle_validation(request: Request, exc: RequestValidationError) -> JSONResponse:
        body = ErrorResponse(
            error_code="VALIDATION_FAILED",
            message="Request validation failed",
            details={"errors": exc.errors()},
            request_id=getattr(request.state, "request_id", None),
        )
        return JSONResponse(status_code=422, content=body.model_dump(mode="json"))

    @app.exception_handler(Exception)
    async def handle_unexpected(request: Request, exc: Exception) -> JSONResponse:
        logger.exception("Unhandled error")
        body = ErrorResponse(
            error_code="AI_INTERNAL_ERROR",
            message="An unexpected error occurred",
            request_id=getattr(request.state, "request_id", None),
        )
        return JSONResponse(status_code=500, content=body.model_dump())
