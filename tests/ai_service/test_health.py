from fastapi.testclient import TestClient

from app.main import create_app
from app.schemas.analysis_schema import AnalysisResult


def test_health_lists_components():
    with TestClient(create_app()) as client:
        res = client.get("/health")
    assert res.status_code == 200
    body = res.json()
    assert body["service"] == "ai_service"
    for name in ("vad", "transcription", "diarization", "audio_classification", "llm"):
        assert name in body["components"]
    # These packages are part of requirements.txt and must be importable.
    for name in ("vad", "transcription", "diarization"):
        assert body["components"][name]["package"]["installed"] is True


def test_analysis_schema_failed_result_is_valid():
    result = AnalysisResult(session_id="s", clip_id="c", status="failed", processing_time_ms=1.0, error="x")
    dumped = result.model_dump()
    assert dumped["events"] == []
    assert dumped["transcription"] is None
