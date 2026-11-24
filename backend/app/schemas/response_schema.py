from typing import Any, Literal

from pydantic import BaseModel, Field


class ErrorResponse(BaseModel):
    error_code: str
    message: str
    details: dict[str, Any] = Field(default_factory=dict)
    request_id: str | None = None


class DependencyStatus(BaseModel):
    name: str
    status: Literal["ok", "unavailable", "degraded"]
    url: str | None = None
    latency_ms: float | None = None
    detail: str | None = None
    # Raw health payload reported by the dependency (e.g. AI service model status)
    info: dict[str, Any] | None = None


class HealthResponse(BaseModel):
    service: str
    status: Literal["ok", "degraded"]
    version: str
    uptime_seconds: float
    process_memory_mb: float
    process_cpu_percent: float
    dependencies: list[DependencyStatus]
