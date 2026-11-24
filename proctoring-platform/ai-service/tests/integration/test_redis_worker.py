"""The worker against a REAL Redis (Docker, redis:7.4-alpine via testcontainers) with FAKE models.

Needs Docker running. Covers: group creation, receiving an event, reading the WAV through the store,
started + final results, acknowledgement, duplicates, retries/dead letters, publish failures, Redis outage,
and the health endpoints.
"""
import json
import threading
import time

import pytest
import redis
from redis.backoff import NoBackoff
from redis.retry import Retry
from fastapi.testclient import TestClient
from testcontainers.redis import RedisContainer  # noqa: E402

from app.clients.redis_client import RedisStreams
from app.core.config import Settings
from app.schemas.contracts import ANALYSIS_RESULT, ANALYSIS_STARTED, ENVELOPE, validation_errors
from app.services.audio_loader import AudioStore
from app.services.event_worker import EventWorker
from app.services.processing_pipeline import ProcessingPipeline
from tests.helpers import FakeTranscriber, FakeVad, event_entry, put_audio, silence, speech_samples, wav_bytes
from app.core.errors import TransientEventError

pytestmark = pytest.mark.integration


@pytest.fixture(scope="module")
def redis_container():
    with RedisContainer("redis:7.4-alpine") as container:
        yield container


@pytest.fixture
def settings(redis_container, store_root, request):
    prefix = f"t{abs(hash(request.node.name)) % 10**8}"
    host = redis_container.get_container_host_ip()
    port = redis_container.get_exposed_port(6379)
    return Settings(REDIS_CONNECTION=f"{host}:{port}", REDIS_EVENTS_STREAM=f"{prefix}.events",
                    REDIS_RESULTS_STREAM=f"{prefix}.results", REDIS_DLQ_STREAM=f"{prefix}.dlq",
                    REDIS_AI_GROUP=f"{prefix}-ai", AUDIO_STORE_PATH=str(store_root), AI_CLAIM_IDLE_MS=1000,
                    AI_MAX_DELIVERIES=2)


@pytest.fixture
def streams(settings):
    s = RedisStreams(settings)
    yield s
    s.client.close()


def make_worker(streams, store_root, vad=None, asr=None, name="w1"):
    pipeline = ProcessingPipeline(AudioStore(store_root), vad or FakeVad(), asr or FakeTranscriber("hello there"))
    return EventWorker(streams, pipeline, name, streams.s.max_deliveries)


def publish_event(streams, store_root, samples=None, data=None):
    data = data if data is not None else wav_bytes(samples)
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data)
    streams.client.xadd(streams.s.events_stream, entry)
    return entry


def results(streams):
    return [fields for _, fields in streams.client.xrange(streams.s.results_stream)]


def pending(streams):
    return streams.client.xpending(streams.s.events_stream, streams.s.consumer_group)["pending"]


def drain(worker, rounds=5):
    for _ in range(rounds):
        worker.run_once(block_ms=200)


# ------------------------------------------------------------------ consuming

def test_group_is_created_from_the_first_entry_so_earlier_events_are_processed(streams, store_root):
    entry = publish_event(streams, store_root, speech_samples())  # before the group exists
    streams.ensure_group()
    streams.ensure_group()  # BUSYGROUP is fine
    worker = make_worker(streams, store_root)
    drain(worker)
    out = results(streams)
    assert [r["messageType"] for r in out] == ["audio.analysis.started", "audio.analysis.completed"]
    assert all(r["eventId"] == entry["eventId"] for r in out)
    assert pending(streams) == 0


def test_published_messages_follow_the_contract_exactly(streams, store_root):
    streams.ensure_group()
    entry = publish_event(streams, store_root, speech_samples())
    drain(make_worker(streams, store_root))
    started, completed = results(streams)
    for r in (started, completed):
        assert validation_errors(ENVELOPE, r) == []
    assert validation_errors(ANALYSIS_STARTED, json.loads(started["payload"])) == []
    payload = json.loads(completed["payload"])
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    assert payload["sessionId"] == entry["sessionId"] and payload["transcription"]["text"] == "hello there"
    # no audio bytes and no local paths in Redis
    raw = json.dumps(results(streams))
    assert "RIFF" not in raw and str(store_root).replace("\\", "\\\\") not in raw


def test_duplicate_delivery_produces_one_final_result(streams, store_root):
    streams.ensure_group()
    entry = publish_event(streams, store_root, speech_samples())
    streams.client.xadd(streams.s.events_stream, {**entry, "messageId": "1b2c3d4e-0000-4000-8000-000000000001"})  # backend re-publish
    asr = FakeTranscriber()
    drain(make_worker(streams, store_root, asr=asr))
    types = [r["messageType"] for r in results(streams)]
    assert types.count("audio.analysis.completed") == 1 and asr.calls == 1
    assert pending(streams) == 0


def test_missing_audio_produces_a_failed_result_and_is_acknowledged(streams, store_root):
    streams.ensure_group()
    entry = publish_event(streams, store_root, speech_samples())
    session_dir = store_root / entry["sessionId"]
    for f in session_dir.iterdir():
        f.unlink()
    drain(make_worker(streams, store_root))
    final = json.loads(results(streams)[-1]["payload"])
    assert final["status"] == "failed" and final["error"]["code"] == "AUDIO_NOT_FOUND"
    assert pending(streams) == 0


def test_transient_failures_are_retried_then_dead_lettered(streams, store_root):
    streams.ensure_group()
    entry = publish_event(streams, store_root, speech_samples())
    asr = FakeTranscriber(fail=TransientEventError("Transcription failed (RuntimeError)"))
    worker = make_worker(streams, store_root, asr=asr)
    worker.run_once(block_ms=200)  # delivery 1 -> retry
    assert pending(streams) == 1
    time.sleep(1.2)
    worker._next_claim = 0
    worker.run_once(block_ms=200)  # reclaimed, delivery 2 (= limit) -> dead-letter
    assert asr.calls == 2
    assert pending(streams) == 0
    dlq = streams.client.xrange(streams.s.dlq_stream)
    assert len(dlq) == 1 and dlq[0][1]["eventId"] == entry["eventId"]
    assert dlq[0][1]["dlq.reason"].startswith("PROCESSING_FAILED")
    final = json.loads(results(streams)[-1]["payload"])
    assert final["error"]["code"] == "DEAD_LETTERED"


def test_invalid_entry_goes_to_the_dead_letter_stream(streams, store_root):
    streams.ensure_group()
    streams.client.xadd(streams.s.events_stream, {"messageType": "garbage"})
    drain(make_worker(streams, store_root))
    assert len(streams.client.xrange(streams.s.dlq_stream)) == 1
    assert pending(streams) == 0 and results(streams) == []


def test_crashed_workers_entries_are_recovered_by_another_worker(streams, store_root):
    streams.ensure_group()
    publish_event(streams, store_root, speech_samples())
    streams.client.xreadgroup(streams.s.consumer_group, "crashed", {streams.s.events_stream: ">"}, count=1)
    read_at = time.monotonic()
    survivor = make_worker(streams, store_root, name="survivor")
    survivor.run_once(block_ms=200)
    if time.monotonic() - read_at < 0.9:  # on a slow machine the entry may already be idle long enough
        assert results(streams) == []  # not idle long enough yet
    time.sleep(1.2)
    survivor._next_claim = 0
    survivor.run_once(block_ms=200)
    assert [r["messageType"] for r in results(streams)][-1] == "audio.analysis.completed"
    assert json.loads(results(streams)[0]["payload"])["deliveryCount"] == 2
    assert pending(streams) == 0


def test_result_publication_failure_keeps_the_entry_pending_until_it_succeeds(streams, store_root):
    streams.ensure_group()
    publish_event(streams, store_root, speech_samples())
    streams.client.set(streams.s.results_stream, "not a stream")  # XADD fails with WRONGTYPE
    worker = make_worker(streams, store_root)
    with pytest.raises(redis.ResponseError):
        worker.run_once(block_ms=200)
    assert pending(streams) == 1
    streams.client.delete(streams.s.results_stream)
    time.sleep(1.2)
    worker._next_claim = 0
    worker.run_once(block_ms=200)
    assert [r["messageType"] for r in results(streams)][-1] == "audio.analysis.completed"
    assert pending(streams) == 0


def test_worker_survives_a_redis_outage_and_resumes(redis_container, streams, store_root):
    streams.ensure_group()
    streams.client.close()
    streams.client = redis.Redis(host=redis_container.get_container_host_ip(),
                                 port=int(redis_container.get_exposed_port(6379)),
                                 decode_responses=True, socket_connect_timeout=1, socket_timeout=3,
                                 retry=Retry(NoBackoff(), retries=0))
    worker = make_worker(streams, store_root)
    stop = threading.Event()
    thread = threading.Thread(target=worker.run, args=(stop,), daemon=True)
    docker_container = redis_container.get_wrapped_container()
    thread.start()
    try:
        time.sleep(0.5)
        docker_container.pause()  # Redis stops answering
        deadline = time.time() + 30
        while worker.state != "redis-unavailable" and time.time() < deadline:
            time.sleep(0.2)
        assert worker.state == "redis-unavailable"
        docker_container.unpause()
        entry = publish_event(streams, store_root, speech_samples())
        deadline = time.time() + 30
        while not any(r["messageType"] == "audio.analysis.completed" for r in results(streams)) and time.time() < deadline:
            time.sleep(0.3)
        assert any(r["messageType"] == "audio.analysis.completed" and r["eventId"] == entry["eventId"] for r in results(streams))
        assert worker.state == "running"
    finally:
        docker_container.reload()
        if docker_container.status == "paused":
            docker_container.unpause()
        stop.set()
        thread.join(timeout=10)


# ------------------------------------------------------------------ health endpoints

class FakeRuntime:
    def __init__(self, streams, store_root, loaded=True):
        self.streams = streams
        self.store = AudioStore(store_root)
        self.vad = FakeVad()
        self.transcriber = FakeTranscriber()
        self.vad.loaded = self.transcriber.loaded = loaded
        self.workers = [make_worker(streams, store_root)]
        self.model_error = None


def health_client(runtime):
    from app.main import app
    app.state.runtime = runtime
    return TestClient(app)  # not used as a context manager: no real models are loaded


def test_health_ready_reports_checks_without_secrets_or_paths(streams, store_root):
    client = health_client(FakeRuntime(streams, store_root))
    assert client.get("/health/live").json() == {"status": "live"}
    response = client.get("/health/ready")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "ready"
    assert body["checks"]["redis"] == "ok" and body["checks"]["audioStore"] == "ok"
    text = response.text
    assert str(store_root).replace("\\", "\\\\") not in text and "password" not in text.lower()


def test_health_not_ready_while_models_load_or_redis_is_down(streams, store_root, tmp_path):
    loading = health_client(FakeRuntime(streams, store_root, loaded=False)).get("/health/ready")
    assert loading.status_code == 503 and loading.json()["checks"]["whisperModel"] == "loading"

    down = RedisStreams(Settings(REDIS_CONNECTION="127.0.0.1:1", REDIS_PASSWORD="s3cret-pass"))
    down.client = redis.Redis(host="127.0.0.1", port=1, password="s3cret-pass", socket_connect_timeout=0.5)
    response = health_client(FakeRuntime(down, tmp_path / "missing")).get("/health/ready")
    assert response.status_code == 503
    assert response.json()["checks"]["redis"] == "unreachable" and response.json()["checks"]["audioStore"] == "unavailable"
    assert "s3cret-pass" not in response.text


def test_health_ready_with_transcription_disabled(streams, store_root):
    runtime = FakeRuntime(streams, store_root)
    runtime.transcriber = None
    body = health_client(runtime).get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["checks"]["whisperModel"] == "disabled" and body["transcription"] is False
