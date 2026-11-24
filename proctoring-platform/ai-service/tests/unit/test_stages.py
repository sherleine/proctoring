"""Transcription mapping, CUDA fallback and rule-based analysis (fake data, no models)."""
from types import SimpleNamespace

import pytest

from app.services import analysis_service, transcription_service
from app.services.transcription_service import TranscriptionData, TranscriptionService, TranscriptSegmentData, map_segments
from app.services.vad_service import VadResult


def seg(start, end, text, logprob=-0.2, no_speech=0.01):
    return SimpleNamespace(start=start, end=end, text=text, avg_logprob=logprob, no_speech_prob=no_speech)


# ------------------------------------------------------------------ transcription mapping

def test_segments_are_mapped_in_time_order_with_timestamps_and_probabilities():
    data = map_segments([seg(2.0, 3.5, " world "), seg(0.0, 1.9, " Hello")], "en", 0.98765)
    assert [(s.start, s.end, s.text) for s in data.segments] == [(0.0, 1.9, "Hello"), (2.0, 3.5, "world")]
    assert data.text == "Hello world"
    assert data.language == "en" and data.language_probability == 0.988
    assert data.segments[0].avg_logprob == -0.2 and data.segments[0].no_speech_prob == 0.01


def test_empty_and_likely_hallucinated_segments_are_dropped_not_rewritten():
    data = map_segments([seg(0, 1, "   "), seg(1, 2, "Thank you.", logprob=-1.4, no_speech=0.9), seg(2, 3, "real")], "en", 0.5)
    assert [s.text for s in data.segments] == ["real"]


def test_unintelligible_audio_gives_an_empty_transcript():
    data = map_segments([], None, None)
    assert data.text == "" and data.segments == [] and data.language is None


def test_cuda_request_without_a_cuda_device_falls_back_to_cpu(monkeypatch):
    created = {}

    class FakeWhisperModel:
        def __init__(self, size, device, compute_type, num_workers):
            created.update(size=size, device=device, compute_type=compute_type)

    monkeypatch.setattr(transcription_service, "cuda_device_count", lambda: 0)
    monkeypatch.setitem(__import__("sys").modules, "faster_whisper", SimpleNamespace(WhisperModel=FakeWhisperModel))
    service = TranscriptionService("base", "cuda", "float16", None)
    service.load()
    assert created == {"size": "base", "device": "cpu", "compute_type": "int8"}
    assert service.device == "cpu" and service.loaded


def test_transcription_failure_is_transient_and_never_returns_text(monkeypatch):
    from app.core.errors import TransientEventError

    service = TranscriptionService("base", "cpu", "int8", None)
    service._model = SimpleNamespace(transcribe=lambda *a, **k: (_ for _ in ()).throw(RuntimeError("boom")))
    with pytest.raises(TransientEventError):
        service.transcribe(__import__("numpy").zeros(16000, dtype="float32"))


# ------------------------------------------------------------------ analysis

def vad(segments, duration=4.0, max_p=0.95):
    speech = round(sum(e - s for s, e in segments), 3)
    return VadResult(segments, speech, round(speech / duration, 3), max_p, 0.9 if segments else None, 0.5)


TRANSCRIPT = TranscriptionData("en", 0.99, [TranscriptSegmentData(0.5, 2.0, "can you help me", -0.2, 0.01)])


def test_no_speech_gives_only_an_informational_no_speech_finding():
    findings = analysis_service.analyse(vad([], max_p=0.1), None, rms_dbfs=-120, peak_dbfs=-120, clipped_ratio=0, gain_db=0)
    assert [(f.type, f.severity) for f in findings] == [("NO_SPEECH_VERIFIED", "info")]
    assert findings[0].start is None and findings[0].evidence["speechSeconds"] == 0


def test_speech_is_an_informational_observation_not_a_violation():
    findings = analysis_service.analyse(vad([(0.5, 2.0), (2.5, 3.0)]), TRANSCRIPT, -20, -3, 0, 0)
    assert [(f.type, f.severity) for f in findings] == [("CANDIDATE_SPEECH", "info")]
    f = findings[0]
    assert (f.start, f.end) == (0.5, 3.0)
    assert f.evidence["wordCount"] == 4 and f.evidence["segmentCount"] == 2
    assert "not evidence of misconduct" in f.description


def test_quality_problems_are_reported_as_unusual_audio_info():
    clipped = analysis_service.analyse(vad([(0, 2)]), TRANSCRIPT, -5, 0, clipped_ratio=0.05, gain_db=0)
    quiet = analysis_service.analyse(vad([(0, 2)]), TRANSCRIPT, rms_dbfs=-60, peak_dbfs=-40, clipped_ratio=0, gain_db=30)
    assert [f.type for f in clipped] == ["CANDIDATE_SPEECH", "UNUSUAL_AUDIO"]
    assert [f.type for f in quiet] == ["CANDIDATE_SPEECH", "UNUSUAL_AUDIO"]
    assert all(f.severity == "info" for f in clipped + quiet)


def test_silence_is_never_flagged_as_unusual_audio():
    findings = analysis_service.analyse(vad([]), None, rms_dbfs=-120, peak_dbfs=-120, clipped_ratio=0, gain_db=0)
    assert "UNUSUAL_AUDIO" not in [f.type for f in findings]


def test_findings_that_need_diarization_or_meaning_are_never_produced():
    types = {f.type for f in analysis_service.analyse(vad([(0, 4)]), TRANSCRIPT, -10, -1, 0.5, 0)}
    assert not types & {"MULTIPLE_VOICES", "BACKGROUND_CONVERSATION", "OVERLAPPING_SPEECH", "POSSIBLE_VERBAL_ASSISTANCE"}
