"""Backend API entry point.

Run (from the ``backend`` folder):
    .\\.venv\\Scripts\\python -m uvicorn app.main:app --port 8000 --reload
"""

import logging
from contextlib import asynccontextmanager

import httpx
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.config import get_settings
from app.exceptions.handlers import register_exception_handlers
from app.middleware.request_context import RequestContextMiddleware
from app.routes import health_routes
from app.services.ai_client_service import AIClientService
from app.services.health_service import HealthService
from app.utils.logging_utils import configure_logging

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    settings.data_dir.mkdir(parents=True, exist_ok=True)
    http_client = httpx.AsyncClient()
    ai_client = AIClientService(settings, http_client)
    app.state.ai_client = ai_client
    app.state.health_service = HealthService(ai_client)
    logger.info("Backend started; AI service at %s", settings.ai_service_url)
    try:
        yield
    finally:
        await http_client.aclose()


def create_app() -> FastAPI:
    settings = get_settings()
    configure_logging(settings.log_level)
    app = FastAPI(title="Audio Monitoring Backend API", version="0.1.0", lifespan=lifespan)
    app.add_middleware(RequestContextMiddleware)
    app.add_middleware(
        CORSMiddleware,
        allow_origins=settings.cors_origins,
        allow_methods=["*"],
        allow_headers=["*"],
        expose_headers=["X-Request-ID"],
    )
    register_exception_handlers(app)
    app.include_router(health_routes.router)
    return app


app = create_app()
