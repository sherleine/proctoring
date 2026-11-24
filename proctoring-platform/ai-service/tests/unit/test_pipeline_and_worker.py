"""Pipeline and worker logic with FAKE models and an in-memory fake Redis (no downloads, no Docker)."""
import json

import numpy as np
import pytest
import redis

from app.core.errors import TransientEventError
from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, ANALYSIS_STARTED, ENVELOPE, validation_errors
from app.services.audio_loader import AudioStore
from app.services.event_worker import EventWorker
from app.services.processing_pipeline import ProcessingPipeline
from tests.helpers import (INVALID_WAV, FakeTranscriber, FakeVad, event_entry, put_audio, quiet_speech, silence,
                           speech_samples, wav_bytes)


def stored_event(store_root, samples=None, data=None):
    data = data if data is not None else wav_bytes(samples)
    sid, key = put_audio(store_root, data)
    return event_entry(sid, key, data)


# ------------------------------------------------------------------ pipeline

def test_speech_event_is_transcribed_and_the_result_is_schema_valid(store_root):
    vad, asr = FakeVad(), FakeTranscriber("can you help me")
    entry = stored_event(store_root, speech_samples())
    result = ProcessingPipeline(AudioStore(store_root), vad, asr).process(parse_event_entry(entry))
    payload = result.to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    assert payload["status"] == "completed" and payload["error"] is None
    assert payload["speech"]["verified"] and payload["speech"]["segments"]
    assert payload["transcription"] == {**payload["transcription"], "available": True, "text": "can you help me", "language": "en"}
    assert payload["models"] == {"vad": "fake-vad", "asr": "faster-whisper:fake", "diarization": None}
    assert payload["audio"]["durationSeconds"] == pytest.approx(len(speech_samples()) / 16000, abs=0.001)
    assert [f["type"] for f in payload["findings"]] == ["CANDIDATE_SPEECH"]


def test_silence_is_not_transcribed_and_no_transcript_is_invented(store_root):
    vad, asr = FakeVad(), FakeTranscriber()
    result = ProcessingPipeline(AudioStore(store_root), vad, asr).process(parse_event_entry(stored_event(store_root, silence())))
    payload = result.to_payload()
    assert asr.calls == 0
    assert payload["status"] == "completed"
    assert payload["speech"]["verified"] is False
    assert payload["transcription"]["available"] is False and payload["transcription"]["text"] == ""
    assert payload["models"]["asr"] is None
    assert [f["type"] for f in payload["findings"]] == ["NO_SPEECH_VERIFIED"]


def test_quiet_audio_reaches_the_models_amplified_but_levels_are_reported_as_recorded(store_root):
    vad = FakeVad()
    result = ProcessingPipeline(AudioStore(store_root), vad, FakeTranscriber()).process(
        parse_event_entry(stored_event(store_root, quiet_speech())))
    assert np.max(np.abs(vad.calls[0])) > np.max(np.abs(quiet_speech())) * 5
    assert result.audio.peakDbfs < -30  # measured on the original audio


def test_model_failure_propagates_as_transient(store_root):
    pipeline = ProcessingPipeline(AudioStore(store_root), FakeVad(), FakeTranscriber(fail=TransientEventError("oom")))
    with pytest.raises(TransientEventError):
        pipeline.process(parse_event_entry(stored_event(store_root, speech_samples())))


# ------------------------------------------------------------------ worker (in-memory Redis double)

class MemoryStreams:
    """Records what the worker would write to Redis."""

    def __init__(self, fail_publish: bool = False):
        self.results: list[dict] = []
        self.dlq: list[dict] = []
        self.acked: list[str] = []
        self.processed: set[tuple[str, str]] = set()
        self.fail_publish = fail_publish

    def publish_result(self, fields):
        if self.fail_publish:
            raise redis.ConnectionError("down")
        assert validation_errors(ENVELOPE, fields) == []
        self.results.append(fields)

    def ack(self, entry_id):
        self.acked.append(entry_id)

    def dead_letter(self, entry_id, fields, reason, count):
        self.dlq.append({**fields, "dlq.reason": reason, "dlq.deliveryCount": str(count)})

    def already_processed(self, sid, eid):
        return (sid, eid) in self.processed

    def mark_processed(self, sid, eid):
        self.processed.add((sid, eid))

    def types(self):
        return [r["messageType"] for r in self.results]

    def payload(self, i=-1):
        return json.loads(self.results[i]["payload"])


def worker(store_root, streams, vad=None, asr=None, max_deliveries=3):
    pipeline = ProcessingPipeline(AudioStore(store_root), vad or FakeVad(), asr or FakeTranscriber())
    return EventWorker(streams, pipeline, "test-worker", max_deliveries)


def test_success_publishes_started_then_completed_then_acknowledges(store_root):
    streams = MemoryStreams()
    entry = stored_event(store_root, speech_samples())
    assert worker(store_root, streams).handle("1-0", entry, 1) == "completed"
    assert streams.types() == ["audio.analysis.started", "audio.analysis.completed"]
    assert validation_errors(ANALYSIS_STARTED, streams.payload(0)) == []
    assert streams.payload(0)["worker"] == "test-worker"
    assert validation_errors(ANALYSIS_RESULT, streams.payload()) == []
    assert streams.acked == ["1-0"]
    assert all(r["eventId"] == entry["eventId"] and r["correlationId"] == entry["eventId"] for r in streams.results)


def test_duplicate_delivery_is_acknowledged_without_reprocessing(store_root):
    streams, asr = MemoryStreams(), FakeTranscriber()
    entry = stored_event(store_root, speech_samples())
    w = worker(store_root, streams, asr=asr)
    w.handle("1-0", entry, 1)
    assert w.handle("2-0", entry, 1) == "duplicate"
    assert asr.calls == 1 and streams.types().count("audio.analysis.completed") == 1
    assert streams.acked == ["1-0", "2-0"]


def test_missing_or_invalid_audio_gives_a_failed_result_not_a_retry(store_root):
    streams = MemoryStreams()
    entry = stored_event(store_root, data=INVALID_WAV)
    assert worker(store_root, streams).handle("1-0", entry, 1) == "failed"
    assert streams.types() == ["audio.analysis.started", "audio.analysis.failed"]
    assert streams.payload()["error"]["code"] == "INVALID_AUDIO"
    assert streams.acked == ["1-0"]


def test_transient_failure_is_left_pending_and_dead_lettered_at_the_limit(store_root):
    streams = MemoryStreams()
    entry = stored_event(store_root, speech_samples())
    w = worker(store_root, streams, asr=FakeTranscriber(fail=TransientEventError("Transcription failed (RuntimeError)")),
               max_deliveries=2)
    assert w.handle("1-0", entry, 1) == "retry"
    assert streams.acked == [] and "audio.analysis.completed" not in streams.types()
    assert w.handle("1-0", entry, 2) == "dead-lettered"
    assert streams.dlq[0]["dlq.reason"].startswith("PROCESSING_FAILED")
    assert streams.payload()["error"]["code"] == "DEAD_LETTERED"
    assert validation_errors(ANALYSIS_RESULT, streams.payload()) == []
    assert streams.acked == ["1-0"]


def test_entry_delivered_too_often_is_dead_lettered_without_processing(store_root):
    streams, asr = MemoryStreams(), FakeTranscriber()
    entry = stored_event(store_root, speech_samples())
    assert worker(store_root, streams, asr=asr, max_deliveries=3).handle("1-0", entry, 4) == "dead-lettered"
    assert asr.calls == 0 and streams.types() == ["audio.analysis.failed"]


def test_invalid_message_is_dead_lettered_and_acknowledged(store_root):
    streams = MemoryStreams()
    entry = stored_event(store_root, speech_samples())
    entry["payload"] = "{broken"
    assert worker(store_root, streams).handle("1-0", entry, 1) == "dead-lettered"
    assert streams.dlq[0]["dlq.reason"].startswith("INVALID_MESSAGE")
    assert streams.payload()["error"]["code"] == "DEAD_LETTERED"  # ids were usable: the backend is told
    assert streams.acked == ["1-0"]


def test_garbage_without_ids_is_dead_lettered_without_a_result(store_root):
    streams = MemoryStreams()
    assert worker(store_root, streams).handle("1-0", {"foo": "bar"}, 1) == "dead-lettered"
    assert streams.results == [] and len(streams.dlq) == 1 and streams.acked == ["1-0"]


def test_publish_failure_raises_and_nothing_is_acknowledged(store_root):
    streams = MemoryStreams(fail_publish=True)
    with pytest.raises(redis.ConnectionError):
        worker(store_root, streams).handle("1-0", stored_event(store_root, speech_samples()), 1)
    assert streams.acked == [] and streams.processed == set()


def test_with_transcription_disabled_speech_events_complete_without_a_transcript(store_root):
    pipeline = ProcessingPipeline(AudioStore(store_root), FakeVad(), None)
    payload = pipeline.process(parse_event_entry(stored_event(store_root, speech_samples()))).to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    assert payload["status"] == "completed"
    assert payload["speech"]["verified"] and payload["speech"]["segments"]
    assert payload["transcription"]["available"] is False
    assert "disabled" in payload["transcription"]["reason"]
    assert payload["models"]["asr"] is None
    assert [f["type"] for f in payload["findings"]] == ["CANDIDATE_SPEECH"]
    assert pipeline.ready


def test_worker_completes_events_without_transcription(store_root):
    streams = MemoryStreams()
    w = EventWorker(streams, ProcessingPipeline(AudioStore(store_root), FakeVad(), None), "w", 3)
    assert w.handle("1-0", stored_event(store_root, speech_samples()), 1) == "completed"
    assert streams.types() == ["audio.analysis.started", "audio.analysis.completed"]
    assert streams.acked == ["1-0"]
