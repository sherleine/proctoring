from fastapi import APIRouter, Depends

from app.schemas.response_schema import HealthResponse
from app.services.dependencies import get_health_service
from app.services.health_service import HealthService

router = APIRouter(prefix="/api", tags=["health"])


@router.get("/health", response_model=HealthResponse)
async def health(service: HealthService = Depends(get_health_service)) -> HealthResponse:
    return await service.get_health()
