"""Contract handling: incoming event validation and outgoing message/result schemas (no models, no Redis)."""
import json
from pathlib import Path

import pytest

from app.core.errors import PermanentEventError
from app.schemas.analysis_result import (AnalysisResult, AudioInfo, Finding, SpeechStage, TranscriptionStage, envelope,
                                         failed_result, started_payload)
from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, CONTRACTS_DIR, ENVELOPE, validation_errors
from tests.helpers import event_entry, new_session_id, put_audio, silence, wav_bytes

DOCS_SCHEMAS = Path(__file__).resolve().parents[3] / "docs" / "contracts" / "schemas"


def valid_entry(store_root):
    data = wav_bytes(silence())
    sid, key = put_audio(store_root, data)
    return event_entry(sid, key, data)


def test_contract_valid_entry_is_parsed(store_root):
    entry = valid_entry(store_root)
    message = parse_event_entry(entry)
    assert message.sessionId == entry["sessionId"]
    assert message.eventId == entry["eventId"]
    assert message.candidateId == "cand-1"
    assert message.audioRef.key.startswith(message.sessionId + "/")


@pytest.mark.parametrize("mutate", [
    lambda e: e.pop("messageId"),
    lambda e: e.update(messageType="audio.analysis.completed"),
    lambda e: e.update(schemaVersion="2"),
    lambda e: e.update(eventId="not-a-uuid"),
    lambda e: e.update(payload="{not json"),
    lambda e: e.update(extra="field"),
    lambda e: e.update(payload=json.dumps({**json.loads(e["payload"]), "sessionId": new_session_id()})),
    lambda e: e.update(payload=json.dumps({k: v for k, v in json.loads(e["payload"]).items() if k != "audioRef"})),
])
def test_entries_that_break_the_contract_are_rejected(store_root, mutate):
    entry = valid_entry(store_root)
    mutate(entry)
    with pytest.raises(PermanentEventError) as err:
        parse_event_entry(entry)
    assert err.value.code == "INVALID_MESSAGE"


def test_entry_without_audioref_key_is_rejected(store_root):
    entry = valid_entry(store_root)
    payload = json.loads(entry["payload"])
    del payload["audioRef"]["key"]
    entry["payload"] = json.dumps(payload)
    with pytest.raises(PermanentEventError, match="audioRef.key"):
        parse_event_entry(entry)


def test_started_payload_and_envelope_follow_the_contract():
    sid, eid = new_session_id(), "7f3c2a1e-5b6d-4c8e-9f0a-1b2c3d4e5f60"
    fields = envelope("audio.analysis.started", sid, eid, started_payload(sid, eid, 2, "host-1"))
    assert validation_errors(ENVELOPE, fields) == []
    assert set(fields) == {"messageType", "schemaVersion", "messageId", "correlationId", "sessionId", "eventId",
                           "producedAt", "payload"}
    assert json.loads(fields["payload"])["deliveryCount"] == 2


def test_completed_result_matches_analysis_result_schema():
    result = AnalysisResult(
        eventId="7f3c2a1e-5b6d-4c8e-9f0a-1b2c3d4e5f60", sessionId=new_session_id(), candidateId="c", status="completed",
        processingMs=12.5, models={"vad": "silero", "asr": "faster-whisper:base", "diarization": None},
        audio=AudioInfo(durationSeconds=2.0, sampleRate=16000, rmsDbfs=-20.0, peakDbfs=-3.0),
        speech=SpeechStage(available=True, verified=True, speechSeconds=1.0, speechRatio=0.5),
        transcription=TranscriptionStage(available=True, language="en", languageProbability=0.9, text="hi"),
        findings=[Finding(type="CANDIDATE_SPEECH", severity="info", description="d", start=0.1, end=1.1, evidence={})])
    payload = result.to_payload()
    assert payload["diarization"] == {"available": False, "reason": "Speaker diarization is not included in this version",
                                      "speakerCount": None, "turns": [], "overlaps": []}
    assert result.message_type == "audio.analysis.completed"


def test_failed_result_matches_the_schema_and_invents_no_stage_output():
    result = failed_result(new_session_id(), "7f3c2a1e-5b6d-4c8e-9f0a-1b2c3d4e5f60", "c", "AUDIO_NOT_FOUND",
                           "missing", retryable=False, delivery_count=1)
    payload = result.to_payload()
    assert payload["status"] == "failed" and payload["audio"] is None and payload["findings"] == []
    assert not payload["speech"]["available"] and not payload["transcription"]["available"]
    assert payload["error"] == {"code": "AUDIO_NOT_FOUND", "message": "missing", "retryable": False, "deliveryCount": 1}
    assert result.message_type == "audio.analysis.failed"


def test_results_violating_the_schema_are_refused_before_publishing():
    result = failed_result(new_session_id(), "not-a-uuid", "c", "X", "m", False, 1)
    with pytest.raises(ValueError, match=ANALYSIS_RESULT):
        result.to_payload()


@pytest.mark.skipif(not DOCS_SCHEMAS.is_dir(), reason="docs/contracts not present (service copied elsewhere)")
def test_bundled_schemas_are_identical_to_the_published_contracts():
    for bundled in CONTRACTS_DIR.glob("*.json"):
        assert json.loads(bundled.read_text("utf-8")) == json.loads((DOCS_SCHEMAS / bundled.name).read_text("utf-8")), bundled.name
