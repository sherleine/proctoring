"""Phase 4 observation rules through the pipeline, with a fake diarizer and fake voice embeddings (no models).

Voices are encoded in the audio: a positive DC offset = voice A (the candidate when enrolled), negative = voice B.
"""
import numpy as np

from app.schemas.audio_event import parse_event_entry
from app.schemas.contracts import ANALYSIS_RESULT, validation_errors
from app.services.audio_loader import AudioStore
from app.services.detection_rules import RulesConfig
from app.services.diarization_service import DiarizationResult, SpeakerInfo
from app.services.processing_pipeline import ProcessingPipeline
from app.services.speaker_service import SpeakerService, SpeakerThresholds
from app.services.transcription_service import TranscriptionData, TranscriptSegmentData
from app.utils.audio_utils import rms_dbfs
from tests.helpers import FakeVad, event_entry, put_audio, wav_bytes

SR = 16_000
A, B = np.eye(16, dtype=np.float32)[0], np.eye(16, dtype=np.float32)[1]
REFERENCE = {"embedding": A.tolist(), "model": "campplus-test", "enrolmentEventId": "00000000-0000-4000-8000-000000000001"}


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
    svc = SpeakerService("unused.onnx", SpeakerThresholds(same=0.4028, different=0.3188))
    svc._extractor = object()
    svc.model_id = "campplus-test"
    svc.embed = lambda x: A if float(np.mean(x)) >= 0 else B
    return svc


def voice(seconds, sign=1.0, level=0.2):
    t = np.arange(int(SR * seconds)) / SR
    return (sign * level + 0.5 * level * np.sin(2 * np.pi * 220 * t)).astype(np.float32)


def sp(label, exclusive, rms=-15.0, speech=None):
    secs = sum(e - s for s, e in exclusive)
    return SpeakerInfo(label, speech if speech is not None else secs, secs, rms, exclusive)


def run(store_root, clip, result, reference=REFERENCE, transcriber=None, rules=None):
    data = wav_bytes(np.clip(clip, -1, 1))
    sid, key = put_audio(store_root, data)
    entry = event_entry(sid, key, data, duration=round(len(clip) / SR, 3), speaker_reference=reference)
    payload = ProcessingPipeline(AudioStore(store_root), FakeVad(), transcriber, speaker(), FakeDiarizer(result),
                                 rules=rules).process(parse_event_entry(entry)).to_payload()
    assert validation_errors(ANALYSIS_RESULT, payload) == []
    return payload


def review(payload):
    return sorted(o["observationType"] for o in payload["observations"] if o["category"] == "review")


def obs(payload, kind):
    return next(o for o in payload["observations"] if o["observationType"] == kind)


def test_single_candidate_produces_only_the_informational_record(store_root):
    p = run(store_root, voice(8), DiarizationResult([(0.0, 8.0, "S1")], [], [sp("S1", [(0.0, 8.0)])]))
    assert review(p) == []
    assert [o["observationType"] for o in p["observations"]] == ["SPEECH_DETECTED"]
    (s1,) = p["diarization"]["speakers"]
    assert s1["role"] == "candidate" and s1["main"] is True and p["diarization"]["distinctVoices"] == 1


def tone(seconds, level, freq=330.0):
    """An independent (uncorrelated) voice-like signal without DC, used as the second voice in an overlap."""
    t = np.arange(int(SR * seconds)) / SR
    return (level * np.sin(2 * np.pi * freq * t)).astype(np.float32)


def overlap_clip(second_db_below_main: float):
    clip = voice(9)
    main_db = rms_dbfs(clip)
    main_rms = 10 ** (main_db / 20)
    clip[int(3 * SR):int(6 * SR)] += tone(3, main_rms * np.sqrt(2) * 10 ** (-second_db_below_main / 20))
    result = DiarizationResult([(0.0, 9.0, "S1"), (3.0, 6.0, "S2")], [(3.0, 6.0)],
                               [sp("S1", [(0.0, 3.0), (6.0, 9.0)], rms=main_db), SpeakerInfo("S2", 3.0, 0.0, None, [])])
    return clip, result


def test_quiet_voice_heard_only_during_overlap_is_background_with_an_estimated_level(store_root):
    clip, result = overlap_clip(second_db_below_main=20)
    p = run(store_root, clip, result)
    assert "BACKGROUND_CONVERSATION_DETECTED" in review(p)
    bg = obs(p, "BACKGROUND_CONVERSATION_DETECTED")["supportingEvidence"]
    assert bg["levelEstimatedFromOverlap"] is True and 15 <= bg["levelBelowMainSpeakerDb"] <= 25


def test_overlapping_voices_give_overlap_and_one_multiple_speakers_observation(store_root):
    clip, result = overlap_clip(second_db_below_main=0)  # equally loud second voice
    p = run(store_root, clip, result)
    assert review(p) == ["MULTIPLE_SPEAKERS_DETECTED", "OVERLAPPING_SPEECH_DETECTED"]
    overlap = obs(p, "OVERLAPPING_SPEECH_DETECTED")
    assert overlap["confidenceKind"] == "rule" and overlap["supportingEvidence"]["overlapSeconds"] == 3.0
    multiple = obs(p, "MULTIPLE_SPEAKERS_DETECTED")
    assert [b["basis"] for b in multiple["supportingEvidence"]["bases"]] == ["overlapping speech (two people speaking at once)"]
    roles = {s["speaker"]: s["role"] for s in p["diarization"]["speakers"]}
    assert roles == {"S1": "candidate", "S2": "uncertain"}  # heard only in overlap: not identified


def test_short_overlap_below_threshold_is_not_reported(store_root):
    clip = voice(9)
    result = DiarizationResult([(0.0, 9.0, "S1"), (4.0, 4.6, "S2")], [(4.0, 4.6)],
                               [sp("S1", [(0.0, 4.0), (4.6, 9.0)]), SpeakerInfo("S2", 0.6, 0.0, None, [])])
    assert review(run(store_root, clip, result)) == []


def test_quiet_other_voice_is_background_conversation(store_root):
    clip = np.concatenate([voice(5), voice(4, sign=-1, level=0.02)])
    result = DiarizationResult([(0.0, 5.0, "S1"), (5.0, 9.0, "S2")], [],
                               [sp("S1", [(0.0, 5.0)], rms=-14.0), sp("S2", [(5.0, 9.0)], rms=-34.0)])
    p = run(store_root, clip, result)
    assert "BACKGROUND_CONVERSATION_DETECTED" in review(p)
    bg = obs(p, "BACKGROUND_CONVERSATION_DETECTED")
    assert bg["supportingEvidence"]["levelBelowMainSpeakerDb"] == 20.0 and bg["supportingEvidence"]["mainSpeakerRole"] == "candidate"
    assert {s["speaker"]: s["role"] for s in p["diarization"]["speakers"]} == {"S1": "candidate", "S2": "background"}


def test_without_a_voice_sample_two_different_voices_are_multiple_speakers_with_unknown_identity(store_root):
    clip = np.concatenate([voice(4), np.zeros(SR, np.float32), voice(4, sign=-1)])
    result = DiarizationResult([(0.0, 4.0, "S1"), (5.0, 9.0, "S2")], [], [sp("S1", [(0.0, 4.0)]), sp("S2", [(5.0, 9.0)])])
    p = run(store_root, clip, result, reference=None)
    assert review(p) == ["MULTIPLE_SPEAKERS_DETECTED"]  # no NON_CANDIDATE_VOICE without a voice sample
    m = obs(p, "MULTIPLE_SPEAKERS_DETECTED")["supportingEvidence"]
    assert m["bases"][0]["basis"] == "two diarized speakers with clearly different voices"
    assert "identity unknown" in m["identity"]
    roles = {s["speaker"]: s["role"] for s in p["diarization"]["speakers"]}
    assert "candidate" not in roles.values()  # never claimed without evidence


def test_two_diarized_parts_of_the_same_voice_without_enrolment_are_not_multiple(store_root):
    clip = np.concatenate([voice(4), np.zeros(SR, np.float32), voice(4)])
    result = DiarizationResult([(0.0, 4.0, "S1"), (5.0, 9.0, "S2")], [], [sp("S1", [(0.0, 4.0)]), sp("S2", [(5.0, 9.0)])])
    p = run(store_root, clip, result, reference=None)
    assert review(p) == [] and p["diarization"]["distinctVoices"] == 1


def _conversation():
    # A (candidate) - B - A, B says "the answer is B"
    clip = np.concatenate([voice(3), voice(3.5, sign=-1), voice(3)])
    result = DiarizationResult([(0.0, 3.0, "S1"), (3.0, 6.5, "S2"), (6.5, 9.5, "S1")], [],
                               [sp("S1", [(0.0, 3.0), (6.5, 9.5)]), sp("S2", [(3.0, 6.5)])])
    return clip, result


def test_assistance_needs_a_content_indicator(store_root):
    clip, result = _conversation()
    without = run(store_root, clip, result)
    assert "POSSIBLE_EXTERNAL_ASSISTANCE" not in review(without)  # other voice + back-and-forth only
    assert {"MULTIPLE_SPEAKERS_DETECTED", "NON_CANDIDATE_VOICE"} <= set(review(without))
    transcriber = Transcriber([(0.0, 3.0, "I am not sure about this one."), (3.0, 6.5, "The answer is B, choose B."),
                               (6.5, 9.5, "Okay, thanks.")])
    with_text = run(store_root, clip, result, transcriber=transcriber)
    assistance = obs(with_text, "POSSIBLE_EXTERNAL_ASSISTANCE")
    kinds = [i["indicator"] for i in assistance["supportingEvidence"]["indicators"]]
    assert kinds == ["another voice", "back-and-forth with the main speaker", "assistance-like phrase"]
    phrase = assistance["supportingEvidence"]["indicators"][2]
    assert phrase["phrase"] == "the answer is" and phrase["speakers"] == ["S2"]
    assert assistance["confidence"] == 1.0 and assistance["confidenceKind"] == "rule"


def test_assistance_phrase_spoken_by_the_candidate_does_not_count(store_root):
    clip, result = _conversation()
    transcriber = Transcriber([(0.0, 3.0, "Question 3, the answer is B."), (3.0, 6.5, "Mm."), (6.5, 9.5, "Done.")])
    assert "POSSIBLE_EXTERNAL_ASSISTANCE" not in review(run(store_root, clip, result, transcriber=transcriber))


def test_assistance_can_be_voice_only_when_configured(store_root):
    clip, result = _conversation()
    p = run(store_root, clip, result, rules=RulesConfig(assistance_require_transcript=False))
    assert "POSSIBLE_EXTERNAL_ASSISTANCE" in review(p)


def test_thresholds_are_configurable(store_root):
    clip = np.concatenate([voice(5), voice(4, sign=-1, level=0.02)])
    result = DiarizationResult([(0.0, 5.0, "S1"), (5.0, 9.0, "S2")], [],
                               [sp("S1", [(0.0, 5.0)], rms=-14.0), sp("S2", [(5.0, 9.0)], rms=-20.0)])  # only 6 dB quieter
    assert "BACKGROUND_CONVERSATION_DETECTED" not in review(run(store_root, clip, result))
    assert "BACKGROUND_CONVERSATION_DETECTED" in review(run(store_root, clip, result, rules=RulesConfig(background_level_db=5.0)))


def test_observation_ids_are_unique_per_clip(store_root):
    clip, result = _conversation()
    p = run(store_root, clip, result, transcriber=Transcriber([(3.0, 6.5, "the answer is b")]))
    ids = [o["observationId"] for o in p["observations"]]
    assert len(ids) == len(set(ids))
