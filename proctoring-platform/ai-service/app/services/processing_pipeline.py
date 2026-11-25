"""One event in, one contract result out - no Redis, no HTTP, so it can be tested and debugged on its own.

    read WAV (store) -> validate/decode -> levels + quiet-audio gain -> Silero VAD
    enrolment event: -> CAM++ enrolment (quality checks) -> result with the reference embedding, no observations
    speech event:    -> CAM++ comparison of each speech region with the session's reference (if any)
                     -> faster-whisper (optional: only when enabled and speech was verified)
                     -> rule-based findings (v1) + observations (detection_rules)
    -> AnalysisResult (schema-checked)

Errors: PermanentEventError (bad/missing audio) and TransientEventError (model failure) propagate to the
worker, which turns them into a failed result or a retry. Nothing is ever filled in with guessed values.
"""
import logging
import time

import numpy as np

from app.schemas.analysis_result import (AnalysisResult, AudioInfo, DiarizationStage, SpeakerStage, SpeechStage, Span,
                                         TranscriptionStage, TranscriptSegment)
from app.schemas.audio_event import AudioEventMessage
from app.services import analysis_service, detection_rules
from app.services.audio_loader import AudioStore, decode_wav
from app.services.diarization_service import DiarizationResult, DiarizationService
from app.services.speaker_service import Comparison, SpeakerService, reference_from_list
from app.services.transcription_service import TranscriptionService
from app.services.vad_service import VadService

MERGE_GAP_SECONDS = 0.5   # VAD segments closer than this form one region
CHUNK_SECONDS = 3.0       # long regions are compared in chunks of about this length
from app.utils import audio_utils

log = logging.getLogger(__name__)


class ProcessingPipeline:
    def __init__(self, store: AudioStore, vad: VadService, transcriber: TranscriptionService | None,
                 speaker: SpeakerService | None = None, diarizer: DiarizationService | None = None,
                 diarization_min_speech_seconds: float = 1.0, rules: "detection_rules.RulesConfig | None" = None):
        self.store = store
        self.vad = vad
        self.transcriber = transcriber
        self.speaker = speaker
        self.diarizer = diarizer
        self.diarization_min_speech_seconds = diarization_min_speech_seconds
        self.rules = rules or detection_rules.RulesConfig()

    @property
    def ready(self) -> bool:
        return (self.vad.loaded and (self.transcriber is None or self.transcriber.loaded)
                and (self.speaker is None or self.speaker.loaded) and (self.diarizer is None or self.diarizer.loaded))

    def process(self, message: AudioEventMessage) -> AnalysisResult:
        started = time.perf_counter()
        audio = decode_wav(self.store.read(message.audioRef))
        samples = audio.samples
        rms, peak = audio_utils.rms_dbfs(samples), audio_utils.peak_dbfs(samples)
        clipped = audio_utils.clipped_ratio(samples)
        model_input, gain_db = audio_utils.normalize_quiet(samples)  # copy; the stored file is untouched

        vad = self.vad.detect(model_input)
        enrolment, speaker_stage, observations = None, SpeakerStage(), []
        diarization_stage, diarization = DiarizationStage(), None
        if message.is_enrolment:
            enrolment, speaker_stage = self._enrol(samples, vad)
            diarization_stage = DiarizationStage(reason="Voice sample (enrolment) event: not diarized")
            transcription = None
        else:
            diarization_stage, diarization = self._diarize(model_input, vad)
            # optional transcript first: the assistance rule may use it (never required)
            transcription = self.transcriber.transcribe(model_input) if vad.verified and self.transcriber else None
            speaker_stage, observations = self._compare(message, model_input, vad, diarization, diarization_stage,
                                                        transcription)
            observations = [detection_rules.speech_observation(message.sessionId, message.eventId, vad,
                                                               audio.duration_seconds, "Silero VAD",
                                                               self.vad.model_id)] + observations
        findings = analysis_service.analyse(vad, transcription, rms, peak, clipped, gain_db)

        if message.is_enrolment:
            transcription_stage = TranscriptionStage(available=False, reason="Voice sample: not transcribed")
        elif self.transcriber is None:
            transcription_stage = TranscriptionStage(available=False, reason="Transcription is disabled (TRANSCRIPTION_ENABLED=false)")
        elif transcription is None:
            transcription_stage = TranscriptionStage(available=False, reason="No speech verified; transcription skipped")
        else:
            transcription_stage = TranscriptionStage(
                available=True, language=transcription.language,
                languageProbability=transcription.language_probability, text=transcription.text,
                segments=[TranscriptSegment(start=s.start, end=s.end, text=s.text, avgLogprob=s.avg_logprob,
                                            noSpeechProb=s.no_speech_prob) for s in transcription.segments])

        result = AnalysisResult(
            eventId=message.eventId, sessionId=message.sessionId, candidateId=message.candidateId,
            status="completed",
            processingMs=round((time.perf_counter() - started) * 1000, 1),
            models={"vad": self.vad.model_id, "asr": self.transcriber.model_id if transcription else None,
                    "diarization": self.diarizer.model_id if diarization is not None else None},
            audio=AudioInfo(durationSeconds=round(audio.duration_seconds, 3), sampleRate=audio.sample_rate,
                            rmsDbfs=rms, peakDbfs=peak),
            speech=SpeechStage(available=True, verified=vad.verified, speechSeconds=vad.speech_seconds,
                               speechRatio=vad.speech_ratio, segments=[Span(start=s, end=e) for s, e in vad.segments]),
            transcription=transcription_stage,
            diarization=diarization_stage,
            findings=findings,
            enrolment=enrolment,
            speaker=speaker_stage,
            observations=observations,
        )
        log.info("Event analysed", extra={"eventId": message.eventId, "sessionId": message.sessionId,
                                           "speechVerified": vad.verified, "speechSeconds": vad.speech_seconds,
                                           "transcriptChars": len(transcription.text) if transcription else 0,
                                           "eventType": message.event_type,
                                           "enrolmentStatus": enrolment["status"] if enrolment else None,
                                           "comparisons": len(speaker_stage.comparisons),
                                           "speakers": diarization.speaker_count if diarization else None,
                                           "overlapSeconds": diarization.overlap_seconds if diarization else None,
                                           "observations": [o["observationType"] for o in observations],
                                           "processingMs": result.processingMs})
        return result

    # ------------------------------------------------------------------ speaker

    def _enrol(self, samples, vad) -> tuple[dict, SpeakerStage]:
        if self.speaker is None:
            return ({"status": "rejected", "reason": "Voice comparison is disabled on the server (SPEAKER_ENABLED=false)",
                     "speechSeconds": vad.speech_seconds, "embedding": None, "embeddingModel": "none"},
                    SpeakerStage(reason="Voice comparison is disabled"))
        enrolment = self.speaker.enrol(samples, vad.segments)
        return enrolment.to_dict(), SpeakerStage(available=False, reason="Voice sample (enrolment) event",
                                                 model=self.speaker.model_id)

    def _diarize(self, samples, vad) -> tuple[DiarizationStage, DiarizationResult | None]:
        if self.diarizer is None:
            return DiarizationStage(reason="Speaker diarization is disabled (DIARIZATION_ENABLED=false)"), None
        if not vad.verified or vad.speech_seconds < self.diarization_min_speech_seconds:
            return DiarizationStage(reason=f"Less than {self.diarization_min_speech_seconds:.1f} s of verified speech: "
                                           "not diarized"), None
        d = self.diarizer.diarize(samples)
        return DiarizationStage(
            available=True, reason=None, speakerCount=d.speaker_count,
            turns=[{"start": s, "end": e, "speaker": spk} for s, e, spk in d.turns],
            overlaps=[Span(start=s, end=e) for s, e in d.overlaps],
            speakers=[sp.to_dict() for sp in d.speakers]), d

    def _reference(self, message: AudioEventMessage):
        """The session's enrolled voice, or None with the reason why voices are not compared with it."""
        if self.speaker is None:
            return None, "Voice comparison is disabled (SPEAKER_ENABLED=false)"
        if message.speakerReference is None:
            return None, "No accepted voice sample for this session: voices are not compared with the candidate"
        if message.speakerReference.model != self.speaker.model_id:
            return None, "The voice sample was made with a different model version; please enrol again"
        reference = reference_from_list(message.speakerReference.embedding)
        return (reference, None) if reference is not None else (None, "The stored voice sample is not usable")

    def _thresholds(self) -> dict | None:
        if self.speaker is None:
            return None
        t = self.speaker.thresholds
        return {"same": t.same, "different": t.different, "minSegmentSeconds": t.min_segment_seconds,
                "minNonCandidateSeconds": t.min_non_candidate_seconds}

    def _compare(self, message: AudioEventMessage, samples, vad, diarization: DiarizationResult | None = None,
                 diarization_stage: DiarizationStage | None = None, transcription=None) -> tuple[SpeakerStage, list[dict]]:
        reference, reason = self._reference(message)
        if diarization is not None and diarization.speakers:
            return self._analyse_diarized(message, samples, diarization, diarization_stage, reference, reason,
                                          transcription)
        # No diarization (disabled, or too little speech): previous region-based comparison
        if self.speaker is None:
            return SpeakerStage(reason=reason), []
        model, thresholds = self.speaker.model_id, self._thresholds()
        if reference is None:
            return SpeakerStage(reason=reason, model=model, thresholds=thresholds), []
        if not vad.verified:
            return SpeakerStage(available=True, reason="No verified speech", reference="present", model=model,
                                thresholds=thresholds), []
        comparisons = [self.speaker.compare(samples, start, end, reference) for start, end in speech_regions(vad.segments)]
        t = self.speaker.thresholds
        observations = detection_rules.speaker_observations(
            message.sessionId, message.eventId, comparisons, detection_rules.ScoreScale(same=t.same, different=t.different),
            model_name="CAM++ (3D-Speaker, VoxCeleb)", model_version=model)
        return SpeakerStage(available=True, reference="present", model=model, thresholds=thresholds,
                            comparisons=[c.to_dict() for c in comparisons]), observations

    def _analyse_diarized(self, message, samples, diarization: DiarizationResult, stage: DiarizationStage | None,
                          reference, reason: str | None, transcription) -> tuple[SpeakerStage, list[dict]]:
        """Diarized speakers -> one CAM++ embedding each (non-overlapping speech only) -> comparison with the enrolled
        voice (if any) and with each other -> roles -> clip-level observation rules."""
        sp_service = self.speaker
        t = sp_service.thresholds if sp_service else None
        scale = detection_rules.ScoreScale(same=t.same, different=t.different) if t else \
            detection_rules.ScoreScale(same=1.0, different=-1.0)  # no voice model: voices are never "different"
        views, embeddings, comparisons = [], {}, []
        for sp in diarization.speakers:
            pieces = [samples[int(a * 16000):int(b * 16000)] for a, b in sp.exclusive]
            audio = np.concatenate(pieces).astype(np.float32) if pieces else np.zeros(0, np.float32)
            turns = [(s0, e0) for s0, e0, who in diarization.turns if who == sp.label]
            view = detection_rules.SpeakerView(label=sp.label, speech_seconds=sp.speech_seconds,
                                              exclusive_seconds=sp.exclusive_seconds, rms_dbfs=sp.rms_dbfs, turns=turns)
            seconds = len(audio) / 16000
            if sp_service is not None and seconds >= t.min_segment_seconds:
                try:
                    embeddings[sp.label] = sp_service.embed(audio)
                except ValueError:
                    pass
            if reference is not None:
                start = sp.exclusive[0][0] if sp.exclusive else (turns[0][0] if turns else 0.0)
                end = sp.exclusive[-1][1] if sp.exclusive else (turns[-1][1] if turns else 0.0)
                if sp.label in embeddings:
                    similarity = float(embeddings[sp.label] @ reference)
                    decision = sp_service.decide(similarity, seconds)
                    why = (f"low similarity, but less than {t.min_non_candidate_seconds:.1f} s of speech: not decided"
                           if decision == "UNCERTAIN" and similarity <= t.different else None)
                    comparison = Comparison(start, end, similarity, decision, why, {"speaker": sp.label, "speechSeconds": round(seconds, 2)})
                else:
                    comparison = Comparison(start, end, None, "UNCERTAIN",
                                            f"only {seconds:.1f} s of non-overlapping speech: too short to compare",
                                            {"speaker": sp.label, "speechSeconds": round(seconds, 2)})
                comparisons.append(comparison)
                view.voice, view.similarity = comparison.decision, comparison.similarity
            views.append(view)

        labels = sorted(embeddings)
        pair_similarity = {(a, b): float(embeddings[a] @ embeddings[b]) for i, a in enumerate(labels) for b in labels[i + 1:]}
        overlap_pieces = [samples[int(a * 16000):int(b * 16000)] for a, b in diarization.overlaps]
        overlap_dbfs = audio_utils.rms_dbfs(np.concatenate(overlap_pieces)) if overlap_pieces else None
        main = detection_rules.assign_roles(views, pair_similarity, scale, self.rules, overlap_dbfs)
        observations = detection_rules.clip_observations(
            message.sessionId, message.eventId, views, main, diarization.overlaps, pair_similarity,
            transcription.segments if transcription else None, scale, self.rules, reference is not None,
            model_name="pyannote segmentation + CAM++",
            model_version=f"{self.diarizer.model_id if self.diarizer else 'none'}; {sp_service.model_id if sp_service else 'none'}")

        if stage is not None:
            by_label = {v.label: v for v in views}
            stage.speakers = [{**s0, "voice": by_label[s0["speaker"]].voice, "role": by_label[s0["speaker"]].role,
                               "main": by_label[s0["speaker"]].main,
                               "similarityToCandidate": None if by_label[s0["speaker"]].similarity is None
                               else round(by_label[s0["speaker"]].similarity, 4)} for s0 in stage.speakers]
            stage.distinctVoices = _distinct_voices(views, pair_similarity, scale, main)

        speaker_stage = SpeakerStage(
            available=reference is not None, reason=reason, reference="present" if reference is not None else "missing",
            model=sp_service.model_id if sp_service else None, thresholds=self._thresholds(),
            comparisons=[c.to_dict() for c in comparisons])
        return speaker_stage, observations


def _distinct_voices(views, pair_similarity, scale, main) -> int | None:
    """Voices supported by evidence: the candidate (or main voice) counts once; every other speaker counts when its
    voice is clearly different from the candidate/main voice. None when nothing could be compared."""
    if main is None:
        return None
    compared = [v for v in views if v is not main and (
        v.similarity is not None or detection_rules._pair(pair_similarity, v.label, main.label) is not None)]
    if not compared:
        return 1 if len(views) == 1 else None
    different = [v for v in compared if v.voice == "LIKELY_NON_CANDIDATE" or (
        v.voice != "LIKELY_CANDIDATE" and (sim := detection_rules._pair(pair_similarity, v.label, main.label)) is not None
        and sim <= scale.different)]
    return 1 + len(different)


def speech_regions(segments: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """Merges VAD segments separated by < MERGE_GAP_SECONDS and cuts long regions into ~CHUNK_SECONDS pieces, so
    one comparison never averages over a long stretch that could contain two voices."""
    merged: list[list[float]] = []
    for start, end in segments:
        if merged and start - merged[-1][1] < MERGE_GAP_SECONDS:
            merged[-1][1] = end
        else:
            merged.append([start, end])
    regions: list[tuple[float, float]] = []
    for start, end in merged:
        length = end - start
        pieces = max(1, int(length // CHUNK_SECONDS))
        step = length / pieces
        for i in range(pieces):
            regions.append((round(start + i * step, 3), round(start + (i + 1) * step if i < pieces - 1 else end, 3)))
    return regions
