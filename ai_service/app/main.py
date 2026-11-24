"""AI processing service entry point.

Run (from the ``ai_service`` folder):
    .\\.venv\\Scripts\\python -m uvicorn app.main:app --port 8001
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.config import get_settings
from app.exceptions.handlers import register_exception_handlers
from app.middleware.request_context import RequestContextMiddleware
from app.routes import health_routes
from app.services.health_service import HealthService
from app.utils.logging_utils import configure_logging

logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    settings.model_cache_dir.mkdir(parents=True, exist_ok=True)
    app.state.health_service = HealthService(settings)
    # Phase 6: a ModelRegistry will load every enabled adapter here, once.
    logger.info("AI service started")
    yield


def create_app() -> FastAPI:
    settings = get_settings()
    configure_logging(settings.log_level)
    app = FastAPI(title="Audio Monitoring AI Service", version="0.1.0", lifespan=lifespan)
    app.add_middleware(RequestContextMiddleware)
    register_exception_handlers(app)
    app.include_router(health_routes.router)
    return app


app = create_app()
