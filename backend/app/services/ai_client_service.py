"""HTTP client for the AI processing service.

The only place in the backend that knows the AI service's URLs and wire format.
Phase 1 implements the health check; clip forwarding is added in Phase 5.
"""

import logging
import time

import httpx

from app.config import Settings
from app.schemas.response_schema import DependencyStatus

logger = logging.getLogger(__name__)


class AIClientService:
    def __init__(self, settings: Settings, client: httpx.AsyncClient):
        self._settings = settings
        self._client = client
        self._base_url = settings.ai_service_url.rstrip("/")

    async def check_health(self) -> DependencyStatus:
        url = f"{self._base_url}/health"
        started = time.perf_counter()
        try:
            response = await self._client.get(url, timeout=self._settings.ai_health_timeout_seconds)
            latency = (time.perf_counter() - started) * 1000
            response.raise_for_status()
            payload = response.json()
        except (httpx.HTTPError, ValueError) as exc:
            logger.warning("AI service health check failed: %s", exc)
            return DependencyStatus(
                name="ai_service", status="unavailable", url=self._base_url, detail=str(exc) or type(exc).__name__
            )
        status = "ok" if payload.get("status") == "ok" else "degraded"
        return DependencyStatus(
            name="ai_service", status=status, url=self._base_url, latency_ms=round(latency, 1), info=payload
        )
