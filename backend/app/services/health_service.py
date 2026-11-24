import time

import psutil

from app.schemas.response_schema import HealthResponse
from app.services.ai_client_service import AIClientService

SERVICE_VERSION = "0.1.0"


class HealthService:
    def __init__(self, ai_client: AIClientService):
        self._ai_client = ai_client
        self._started = time.monotonic()
        self._process = psutil.Process()
        self._process.cpu_percent(None)  # prime the CPU counter

    async def get_health(self) -> HealthResponse:
        ai_status = await self._ai_client.check_health()
        return HealthResponse(
            service="backend",
            # The backend itself is up; an unreachable AI service degrades it.
            status="ok" if ai_status.status == "ok" else "degraded",
            version=SERVICE_VERSION,
            uptime_seconds=round(time.monotonic() - self._started, 1),
            process_memory_mb=round(self._process.memory_info().rss / 2**20, 1),
            process_cpu_percent=self._process.cpu_percent(None),
            dependencies=[ai_status],
        )
