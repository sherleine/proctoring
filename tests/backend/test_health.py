import httpx
import pytest
from fastapi.testclient import TestClient

from app.config import get_settings
from app.main import create_app
from app.services.ai_client_service import AIClientService
from app.services.health_service import HealthService


def _client_with_ai_transport(transport: httpx.MockTransport) -> TestClient:
    app = create_app()
    client = TestClient(app)
    client.__enter__()  # run lifespan
    ai_client = AIClientService(get_settings(), httpx.AsyncClient(transport=transport))
    app.state.ai_client = ai_client
    app.state.health_service = HealthService(ai_client)
    return client


def test_health_reports_ai_service_ok():
    transport = httpx.MockTransport(lambda req: httpx.Response(200, json={"status": "ok", "service": "ai_service"}))
    client = _client_with_ai_transport(transport)
    res = client.get("/api/health")
    assert res.status_code == 200
    body = res.json()
    assert body["status"] == "ok"
    assert body["dependencies"][0]["status"] == "ok"
    assert res.headers["X-Request-ID"]


def test_health_degraded_when_ai_service_down():
    def refuse(req: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=req)

    client = _client_with_ai_transport(httpx.MockTransport(refuse))
    body = client.get("/api/health").json()
    assert body["status"] == "degraded"
    assert body["dependencies"][0]["status"] == "unavailable"
    assert "refused" in body["dependencies"][0]["detail"]


@pytest.mark.parametrize("path", ["/api/does-not-exist"])
def test_unknown_route_404(path):
    client = TestClient(create_app())
    assert client.get(path).status_code == 404
