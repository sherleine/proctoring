"""Tier 2 escalation wired into the real ProcessingPipeline, with fake diarizer/speaker/transcriber/LLM models
(no network, no downloads) - this is the thing main.py actually assembles from Settings."""
import numpy as np

from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, validation_errors
from app.services.audio_loader import AudioStore
from app.services.diarization_service import DiarizationResult, SpeakerInfo
from app.services.processing_pipeline import ProcessingPipeline
from app.services.question_context import QuestionContext
from app.services.speaker_service import SpeakerService
from app.services.transcription_service import TranscriptionData, TranscriptSegmentData
from tests.helpers import FakeVad, event_entry, put_audio, wav_bytes
from tests.unit.test_llm_review_service import VALID_RESPONSE, FakeLlm

SR = 16_000
A, B = np.eye(16, dtype=np.float32)[0], np.eye(16, dtype=np.float32)[1]


class FakeDiarizer:
    model_id = "fake-diarizer"
    loaded = True

    def __init__(self, result):
        self.result = result

    def diarize(self, samples):
        return self.result


class Transcriber:
    model_id = "faster-whisper:fake"
    loaded = True

    def __init__(self, segments):
        self.segments = segments

    def transcribe(self, samples):
        return TranscriptionData("en", 0.99, [TranscriptSegmentData(s, e, t, -0.2, 0.01) for s, e, t in self.segments])


def speaker():
    svc = SpeakerService("unused.onnx")
    svc._extractor = object()
    svc.model_id = "campplus-test"
    svc.embed = lambda x: A if float(np.mean(x)) >= 0 else B
    return svc


def voice(seconds, sign=1.0, level=0.2):
    t = np.arange(int(SR * seconds)) / SR
    return (sign * level + 0.5 * level * np.sin(2 * np.pi * 220 * t)).astype(np.float32)


def sp(label, exclusive, rms=-15.0):
    secs = sum(e - s for s, e in exclusive)
    return SpeakerInfo(label, secs, secs, rms, exclusive)


class FakeQuestionProvider:
    def __init__(self, question):
        self.question = question
        self.calls = []

    def get(self, *, session_id, exam_id, start_at, end_at):
        self.calls.append((session_id, exam_id, start_at, end_at))
        return self.question


def run(store_root, clip, result, transcriber, llm=None, question=None, escalation_enabled=True):
    data = wav_bytes(np.clip(clip, -1, 1))
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data, duration=round(len(clip) / SR, 3))
    provider = FakeQuestionProvider(question)
    pipeline = ProcessingPipeline(AudioStore(store_root), FakeVad(), transcriber, speaker(), FakeDiarizer(result),
                                 escalation_enabled=escalation_enabled, llm=llm, question_provider=provider)
    payload = pipeline.process(parse_event_entry(entry)).to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    return payload, provider


# S1 is the louder, main speaker; S2 is a second, clearly different voice (opposite sign => different embedding).
CLIP = np.concatenate([voice(2.5, sign=1.0), voice(2.5, sign=-1.0)])
RESULT = DiarizationResult([(0.0, 2.5, "S1"), (2.5, 5.0, "S2")], [],
                           [sp("S1", [(0.0, 2.5)]), sp("S2", [(2.5, 5.0)])])


def test_escalation_is_not_attempted_when_disabled(store_root):
    transcriber = Transcriber([(0.0, 2.5, "what is the capital of France"), (2.5, 5.0, "psst, it's Paris")])
    payload, _ = run(store_root, CLIP, RESULT, transcriber, llm=FakeLlm(VALID_RESPONSE), escalation_enabled=False)
    assert payload["aiReview"] == {"attempted": False, "reason": "Tier 2 escalation is disabled (ESCALATION_ENABLED=false)",
                                   "modelValid": None, "similarityScore": None, "classification": None,
                                   "attributedTo": None, "matchedQuestionSpan": None, "confidence": None,
                                   "rationale": None, "recommendedReviewPriority": None}


def test_escalation_runs_and_tags_the_transcript_by_speaker_role(store_root):
    transcriber = Transcriber([(0.0, 2.5, "what is the capital of france"), (2.5, 5.0, "psst, it's paris")])
    llm = FakeLlm(VALID_RESPONSE)
    payload, provider = run(store_root, CLIP, RESULT, transcriber, llm=llm,
                            question=QuestionContext("What is the capital of France?"))
    assert "MULTIPLE_SPEAKERS_DETECTED" in [o["observationType"] for o in payload["observations"]]
    assert payload["aiReview"]["attempted"] is True
    assert payload["aiReview"]["modelValid"] is True
    assert payload["aiReview"]["classification"] == "likely_reading_question"
    # the backend's examId/sessionId reached the question provider
    assert provider.calls and provider.calls[0][1] == "exam-1"
    # both speakers' words reached the LLM prompt, each tagged with its own role (main vs other), never an identity
    (prompt, _schema), = llm.calls
    assert "(main): what is the capital of france" in prompt
    assert "(other): psst, it's paris" in prompt


def test_escalation_is_not_attempted_without_a_review_flag(store_root):
    single_speaker_clip = voice(3.0)
    single_result = DiarizationResult([(0.0, 3.0, "S1")], [], [sp("S1", [(0.0, 3.0)])])
    transcriber = Transcriber([(0.0, 3.0, "talking to myself")])
    payload, _ = run(store_root, single_speaker_clip, single_result, transcriber, llm=FakeLlm(VALID_RESPONSE))
    assert payload["aiReview"]["attempted"] is False
    assert "no overlap/multiple-speaker flag" in payload["aiReview"]["reason"]
