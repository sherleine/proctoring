import logging

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.exceptions import AppError
from app.schemas.response_schema import ErrorResponse

logger = logging.getLogger(__name__)


def _request_id(request: Request) -> str | None:
    return getattr(request.state, "request_id", None)


def register_exception_handlers(app: FastAPI) -> None:
    @app.exception_handler(AppError)
    async def handle_app_error(request: Request, exc: AppError) -> JSONResponse:
        logger.warning("%s: %s", exc.error_code, exc.message)
        body = ErrorResponse(
            error_code=exc.error_code,
            message=exc.message,
            details=exc.details,
            request_id=_request_id(request),
        )
        return JSONResponse(status_code=exc.status_code, content=body.model_dump())

    @app.exception_handler(RequestValidationError)
    async def handle_validation(request: Request, exc: RequestValidationError) -> JSONResponse:
        body = ErrorResponse(
            error_code="VALIDATION_FAILED",
            message="Request validation failed",
            details={"errors": exc.errors()},
            request_id=_request_id(request),
        )
        return JSONResponse(status_code=422, content=body.model_dump(mode="json"))

    @app.exception_handler(Exception)
    async def handle_unexpected(request: Request, exc: Exception) -> JSONResponse:
        logger.exception("Unhandled error")
        body = ErrorResponse(
            error_code="INTERNAL_ERROR",
            message="An unexpected error occurred",
            request_id=_request_id(request),
        )
        return JSONResponse(status_code=500, content=body.model_dump())
