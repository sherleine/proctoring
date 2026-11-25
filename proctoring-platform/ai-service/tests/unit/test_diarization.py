"""Diarization logic without the model: synthetic segmentation output -> turns, pauses, overlap, speaker roles."""
from dataclasses import dataclass

import numpy as np
import pytest

from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, validation_errors
from app.services.audio_loader import AudioStore
from app.services.diarization_service import DiarizationConfig, DiarizationResult, DiarizationService, SpeakerInfo
from app.services.processing_pipeline import ProcessingPipeline
from app.services.speaker_service import SpeakerService, SpeakerThresholds
from tests.helpers import FakeVad, event_entry, put_audio, wav_bytes

SR = 16_000
FRAMES = 589  # frames per 10 s window of the pyannote segmentation model (~17 ms each)


@dataclass
class Window:
    start: float
    duration: float = 10.0


class Windows:
    def __init__(self, starts):
        self.starts = starts

    def __getitem__(self, i):
        return Window(self.starts[i])


def activity(*ranges, duration=10.0):
    """One local speaker's frame activity for the given (start, end) ranges in seconds."""
    x = np.zeros(FRAMES)
    for s, e in ranges:
        x[int(s / duration * FRAMES):int(e / duration * FRAMES)] = 1
    return x


def interpret(*speakers, samples=None, **config):
    data = np.stack(list(speakers) + [np.zeros(FRAMES)] * (3 - len(speakers)), axis=-1)[None]
    svc = DiarizationService(DiarizationConfig(**config))
    samples = samples if samples is not None else np.full(SR * 10, 0.1, np.float32)
    return svc._interpret(data, Windows([0.0]), 10.0, samples)


def test_two_speakers_taking_turns():
    r = interpret(activity((0, 4.5)), activity((5.0, 10)))
    assert r.speaker_count == 2 and r.overlaps == []
    assert [(round(s, 1), round(e, 1), spk) for s, e, spk in r.turns] == [(0.0, 4.5, "S1"), (5.0, 10.0, "S2")]


def test_labels_follow_first_appearance_not_model_index():
    r = interpret(activity((6, 10)), activity((0, 4)))  # local index 0 speaks second
    assert [spk for _, _, spk in r.turns] == ["S1", "S2"] and r.turns[0][0] == 0.0


def test_short_pause_inside_a_speakers_speech_is_closed_but_a_long_one_is_not():
    short = interpret(activity((0, 2), (2.3, 4)))
    long = interpret(activity((0, 2), (3.0, 4)))
    assert len(short.turns) == 1 and len(long.turns) == 2 and long.speaker_count == 1


def test_overlap_is_reported_and_excluded_from_each_speakers_exclusive_speech():
    r = interpret(activity((0, 10)), activity((3, 7)))
    assert r.speaker_count == 2
    (start, end), = r.overlaps
    assert start == pytest.approx(3, abs=0.05) and end == pytest.approx(7, abs=0.05)
    s1, s2 = r.speakers
    assert s1.exclusive_seconds == pytest.approx(6, abs=0.1) and s2.exclusive_seconds == pytest.approx(0, abs=0.1)


def test_brief_activity_is_not_a_speaker_and_tiny_overlap_is_ignored():
    r = interpret(activity((0, 9)), activity((4, 4.6)))  # 0.6 s < 1.0 s minimum
    assert r.speaker_count == 1 and r.overlaps == []
    r = interpret(activity((0, 5)), activity((4.8, 10)))  # 0.2 s overlap < 0.5 s minimum
    assert r.speaker_count == 2 and r.overlaps == []


def test_silence_has_no_speakers():
    r = interpret()
    assert r.speaker_count == 0 and r.turns == [] and r.overlaps == []


def test_speaker_level_is_measured_on_exclusive_speech():
    samples = np.concatenate([np.full(SR * 5, 0.2, np.float32), np.full(SR * 5, 0.02, np.float32)])
    r = interpret(activity((0, 5)), activity((5, 10)), samples=samples)
    loud, quiet = r.speakers
    assert loud.rms_dbfs == pytest.approx(-14.0, abs=0.5) and quiet.rms_dbfs == pytest.approx(-34.0, abs=0.5)


# ------------------------------------------------------------------ pipeline with a fake diarizer

CANDIDATE, OTHER = np.eye(16, dtype=np.float32)[0], np.eye(16, dtype=np.float32)[1]
REFERENCE = {"embedding": CANDIDATE.tolist(), "model": "campplus-test", "enrolmentEventId": "00000000-0000-4000-8000-000000000001"}


class FakeDiarizer:
    model_id = "fake-diarizer"
    loaded = True

    def __init__(self, result: DiarizationResult):
        self.result = result
        self.calls = 0

    def diarize(self, samples):
        self.calls += 1
        return self.result


def speaker():
    svc = SpeakerService("unused.onnx", SpeakerThresholds(same=0.4028, different=0.3188))
    svc._extractor = object()
    svc.model_id = "campplus-test"
    svc.embed = lambda x: CANDIDATE if float(np.mean(x)) >= 0 else OTHER
    return svc


def voice(seconds, sign=1.0):
    t = np.arange(int(SR * seconds)) / SR
    return (sign * 0.2 + 0.1 * np.sin(2 * np.pi * 220 * t)).astype(np.float32)


def run(store_root, clip, diarization: DiarizationResult):
    data = wav_bytes(np.clip(clip, -1, 1))
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data, duration=round(len(clip) / SR, 3), speaker_reference=REFERENCE)
    diarizer = FakeDiarizer(diarization)
    payload = ProcessingPipeline(AudioStore(store_root), FakeVad(), None, speaker(), diarizer).process(
        parse_event_entry(entry)).to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    return payload, diarizer


def info(label, exclusive):
    secs = sum(e - s for s, e in exclusive)
    return SpeakerInfo(label, secs, secs, -20.0, exclusive)


def test_pause_split_candidate_speech_is_compared_as_one_speaker(store_root):
    # A speaks 0-1.4 and 2.2-3.4 (two pieces < 1.5 s each), then B 4.4-8.0 - the case region-based comparison missed
    clip = np.concatenate([voice(1.4), np.zeros(int(SR * 0.8), np.float32), voice(1.2), np.zeros(SR, np.float32),
                           voice(3.6, sign=-1)])
    d = DiarizationResult(turns=[(0.0, 1.4, "S1"), (2.2, 3.4, "S1"), (4.4, 8.0, "S2")], overlaps=[],
                          speakers=[info("S1", [(0.0, 1.4), (2.2, 3.4)]), info("S2", [(4.4, 8.0)])])
    payload, diarizer = run(store_root, clip, d)
    assert diarizer.calls == 1
    roles = {s["speaker"]: s["voice"] for s in payload["diarization"]["speakers"]}
    assert roles == {"S1": "LIKELY_CANDIDATE", "S2": "LIKELY_NON_CANDIDATE"}
    assert payload["diarization"]["distinctVoices"] == 2 and payload["diarization"]["speakerCount"] == 2
    review = sorted(o["observationType"] for o in payload["observations"] if o["category"] == "review")
    assert review == ["MULTIPLE_SPEAKERS_DETECTED", "NON_CANDIDATE_VOICE"]
    assert payload["models"]["diarization"] == "fake-diarizer"


def test_two_diarized_speakers_with_the_candidate_voice_are_one_person(store_root):
    clip = np.concatenate([voice(4), np.zeros(SR, np.float32), voice(4)])
    d = DiarizationResult(turns=[(0.0, 4.0, "S1"), (5.0, 9.0, "S2")], overlaps=[],
                          speakers=[info("S1", [(0.0, 4.0)]), info("S2", [(5.0, 9.0)])])
    payload, _ = run(store_root, clip, d)
    assert payload["diarization"]["speakerCount"] == 2 and payload["diarization"]["distinctVoices"] == 1
    assert [o for o in payload["observations"] if o["category"] == "review"] == []


def test_a_speaker_heard_only_in_overlap_is_not_identified(store_root):
    clip = voice(8)
    d = DiarizationResult(turns=[(0.0, 8.0, "S1"), (3.0, 6.0, "S2")], overlaps=[(3.0, 6.0)],
                          speakers=[info("S1", [(0.0, 3.0), (6.0, 8.0)]), SpeakerInfo("S2", 3.0, 0.0, None, [])])
    payload, _ = run(store_root, clip, d)
    roles = {s["speaker"]: s["voice"] for s in payload["diarization"]["speakers"]}
    assert roles == {"S1": "LIKELY_CANDIDATE", "S2": "UNCERTAIN"}
    assert payload["diarization"]["overlaps"] == [{"start": 3.0, "end": 6.0}]


def test_too_little_speech_is_not_diarized(store_root):
    d = DiarizationResult(turns=[], overlaps=[], speakers=[])
    payload, diarizer = run(store_root, np.concatenate([voice(0.6), np.zeros(SR * 2, np.float32)]), d)
    assert diarizer.calls == 0 and payload["diarization"]["available"] is False
    assert "not diarized" in payload["diarization"]["reason"]
