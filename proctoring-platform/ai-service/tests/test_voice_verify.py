"""POST /voice/verify end to end with a fake pipeline (no real models) + the FakeRedis from tests/helpers.py.
Walks the example table from the requirements across repeated calls against the SAME FastAPI app (so the
registered reference persists between requests), and checks the proctoring verdict is combined in."""
import numpy as np
from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.api.voice_verify import router
from app.services.DetectionRulesService import ScoreScale
from app.services.VoiceReferenceStoreService import VoiceReferenceStore
from tests.helpers import FakeRedis, wav_bytes

A, B = np.array([1.0, 0.0], dtype=np.float32), np.array([0.0, 1.0], dtype=np.float32)
CLIP = wav_bytes(np.zeros(16_000, dtype=np.float32))
FLAGGED = {"observationType": "multiple_speakers", "category": "review"}


class FakeDiarizationStage:
    def __init__(self, distinct_voices):
        self.distinctVoices = distinct_voices


class FakeResult:
    def __init__(self, distinct_voices, observations):
        self.diarization = FakeDiarizationStage(distinct_voices)
        self._observations = observations

    def to_payload(self):
        return {"observations": self._observations, "aiReview": {"attempted": False, "reason": "disabled"}}


class FakePipeline:
    """Returns whatever the test sets: (distinctVoices, main embedding, observations)."""
    def __init__(self):
        self.set(1, A)

    def set(self, distinct_voices, embedding, observations=()):
        self.distinct_voices, self.embedding, self.observations = distinct_voices, embedding, list(observations)

    def analyse(self, wav, session_id, event_id, candidate_id):
        assert candidate_id  # emailid reaches the pipeline as the candidate id
        return FakeResult(self.distinct_voices, self.observations), self.embedding


class FakeSpeaker:
    model_id = "campplus-test"


class FakeRuntime:
    def __init__(self, redis_client=None):
        self.pipeline = FakePipeline()
        self.diarizer = object()
        self.speaker = FakeSpeaker()
        self.scale = ScoreScale(same=0.4028, different=0.3188)
        self.voice_reference_store = VoiceReferenceStore(redis_client or FakeRedis(), ttl_seconds=3600)


def make_client(runtime=None) -> tuple[TestClient, FakeRuntime]:
    app = FastAPI()
    app.include_router(router)
    app.state.runtime = runtime or FakeRuntime()
    return TestClient(app), app.state.runtime


def post(client, exam_id="EXAM_001", email_id="candidate@example.com", verbose=False):
    return client.post("/voice/verify", data={"examid": exam_id, "emailid": email_id, "verbose": str(verbose).lower()},
                       files={"voice": ("clip.wav", CLIP, "audio/wav")})


def test_the_full_example_table_across_repeated_requests():
    client, runtime = make_client()
    pipeline = runtime.pipeline

    assert post(client).json()["valid"] is True   # 1: Voice A, nothing registered -> valid, registers A
    assert post(client).json()["valid"] is True   # 2: Voice A again -> valid

    pipeline.set(1, B)                            # 3: Voice B -> violation, reference must NOT change
    assert post(client).json()["valid"] is False
    ref = runtime.voice_reference_store.get("EXAM_001", "candidate@example.com")
    np.testing.assert_allclose(ref, A, atol=1e-6)

    pipeline.set(2, A)                            # 4: A + B together -> violation even though A matches
    assert post(client).json()["valid"] is False

    pipeline.set(1, A)                            # 5: back to Voice A alone -> valid again
    assert post(client).json()["valid"] is True


def test_a_different_examid_or_emailid_gets_its_own_independent_reference():
    client, runtime = make_client()
    post(client, email_id="a@example.com")
    runtime.pipeline.set(1, B)
    assert post(client, email_id="b@example.com").json()["valid"] is True


def test_matching_voice_but_flagged_by_proctoring_is_not_valid():
    client, runtime = make_client()
    post(client)  # registers A
    runtime.pipeline.set(1, A, observations=[FLAGGED])
    body = post(client).json()
    assert body["valid"] is False and "multiple_speakers" in body["reason"]


def test_no_speech_is_not_valid():
    client, runtime = make_client()
    runtime.pipeline.set(None, None)
    body = post(client).json()
    assert body["valid"] is False and body["reason"].startswith("No speech could be verified")


def test_verbose_returns_the_full_result():
    client, _ = make_client()
    body = post(client, verbose=True).json()
    assert set(body) == {"valid", "reason", "result"}
    assert set(post(client).json()) == {"valid", "reason"}


def test_missing_required_fields_is_a_client_error():
    client, _ = make_client()
    assert client.post("/voice/verify", data={"examid": "EXAM_001"}).status_code == 422


def test_returns_503_when_redis_is_unavailable():
    import redis

    class BrokenRedis(FakeRedis):
        def get(self, key):
            raise redis.ConnectionError("down")

    client, _ = make_client(FakeRuntime(BrokenRedis()))
    assert post(client).status_code == 503


def test_returns_503_when_a_required_model_is_disabled():
    runtime = FakeRuntime()
    runtime.speaker = None
    client, _ = make_client(runtime)
    assert post(client).status_code == 503
