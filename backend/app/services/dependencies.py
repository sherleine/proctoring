"""FastAPI dependency providers.

Services are created once in the application lifespan and stored on
``app.state``; routes obtain them through these functions so they can be
overridden in tests.
"""

from fastapi import Request

from app.services.ai_client_service import AIClientService
from app.services.health_service import HealthService


def get_health_service(request: Request) -> HealthService:
    return request.app.state.health_service


def get_ai_client(request: Request) -> AIClientService:
    return request.app.state.ai_client
