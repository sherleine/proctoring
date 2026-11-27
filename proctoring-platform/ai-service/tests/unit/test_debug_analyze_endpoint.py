"""POST /debug/analyze end to end, with the same fakes used in test_llm_escalation_pipeline.py (no network,
no downloads, no ffmpeg - the uploaded file is already 16 kHz mono PCM16 so no conversion is needed)."""
import numpy as np
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.api.debug_analyze import router
from app.services.audio_loader import AudioStore
from app.services.processing_pipeline import ProcessingPipeline
from tests.helpers import FakeVad, wav_bytes
from tests.unit.test_llm_escalation_pipeline import CLIP, RESULT, FakeDiarizer, Transcriber, speaker
from tests.unit.test_llm_review_service import VALID_RESPONSE, FakeLlm


class FakeRuntime:
    def __init__(self, store, pipeline):
        self.store = store
        self.pipeline = pipeline


def make_client(store_root, llm=None, transcriber=None) -> TestClient:
    store = AudioStore(store_root)
    pipeline = ProcessingPipeline(store, FakeVad(), transcriber, speaker(), FakeDiarizer(RESULT),
                                  escalation_enabled=llm is not None, llm=llm)
    app = FastAPI()
    app.include_router(router)
    app.state.runtime = FakeRuntime(store, pipeline)
    return TestClient(app)


CLIP_WAV = wav_bytes(np.clip(CLIP, -1, 1))


def test_upload_with_escalation_off_stays_cautious_on_a_flagged_clip(store_root):
    transcriber = Transcriber([(0.0, 2.5, "what is the capital of france"), (2.5, 5.0, "psst, it's paris")])
    client = make_client(store_root, llm=None, transcriber=transcriber)
    resp = client.post("/debug/analyze", data={"verbose": "true"}, files={"file": ("clip.wav", CLIP_WAV, "audio/wav")})
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is False
    assert body["result"]["aiReview"]["attempted"] is False
    assert "MULTIPLE_SPEAKERS_DETECTED" in [o["observationType"] for o in body["result"]["observations"]]


def test_upload_with_a_manual_question_clears_the_flag(store_root):
    transcriber = Transcriber([(0.0, 2.5, "what is the capital of france"), (2.5, 5.0, "psst, it's paris")])
    client = make_client(store_root, llm=FakeLlm(VALID_RESPONSE), transcriber=transcriber)
    resp = client.post("/debug/analyze", data={"question_text": "What is the capital of France?", "verbose": "true"},
                       files={"file": ("clip.wav", CLIP_WAV, "audio/wav")})
    assert resp.status_code == 200
    body = resp.json()
    assert body["valid"] is True
    assert body["result"]["aiReview"]["classification"] == "likely_reading_question"


def test_upload_rejects_an_empty_file(store_root):
    client = make_client(store_root)
    resp = client.post("/debug/analyze", files={"file": ("clip.wav", b"", "audio/wav")})
    assert resp.status_code == 400


def test_upload_rejects_an_undecodable_file_without_ffmpeg(store_root):
    client = make_client(store_root)
    resp = client.post("/debug/analyze", files={"file": ("clip.mp4", b"not actually a video", "video/mp4")})
    assert resp.status_code in (400, 500)  # ffmpeg missing -> 500; ffmpeg present but input garbage -> 400


def test_response_is_just_the_verdict_by_default(store_root):
    transcriber = Transcriber([(0.0, 3.0, "just me talking")])
    client = make_client(store_root, llm=None, transcriber=transcriber)
    resp = client.post("/debug/analyze", files={"file": ("clip.wav", CLIP_WAV, "audio/wav")})
    assert set(resp.json()) == {"valid", "reason"}


def test_verbose_response_includes_the_full_analysis_result_alongside_the_verdict(store_root):
    transcriber = Transcriber([(0.0, 3.0, "just me talking")])
    client = make_client(store_root, llm=None, transcriber=transcriber)
    resp = client.post("/debug/analyze", data={"verbose": "true"}, files={"file": ("clip.wav", CLIP_WAV, "audio/wav")})
    body = resp.json()
    assert set(body) == {"valid", "reason", "result"}
    assert body["result"]["schemaVersion"] == "1.2"
    assert "observations" in body["result"] and "aiReview" in body["result"]


def test_upload_rejects_a_malformed_session_id_with_400(store_root):
    client = make_client(store_root)
    resp = client.post("/debug/analyze", data={"session_id": "string"},
                       files={"file": ("clip.wav", CLIP_WAV, "audio/wav")})
    assert resp.status_code == 400
    assert "session_id" in resp.json()["detail"]
