"""One event in, one contract result out - no Redis, no HTTP, so it can be tested and debugged on its own.

    read WAV (store) -> validate/decode -> levels + quiet-audio gain -> Silero VAD
    -> pyannote segmentation (speaker turns, overlap; only with enough speech)
    -> CAM++ voice embedding per diarized speaker, compared between the speakers of the clip
    -> faster-whisper (optional: only when enabled and speech was verified)
    -> rule-based findings (v1) + observations (detection_rules)
    -> AnalysisResult (schema-checked)

Errors: PermanentEventError (bad/missing audio) and TransientEventError (model failure) propagate to the
worker, which turns them into a failed result or a retry. Nothing is ever filled in with guessed values.
"""
import logging
import time

import numpy as np

from app.schemas.analysis_result import (AnalysisResult, AudioInfo, DiarizationStage, SpeechStage, Span,
                                         TranscriptionStage, TranscriptSegment)
from app.schemas.audio_event import AudioEventMessage
from app.services import analysis_service, detection_rules
from app.services.audio_loader import AudioStore, decode_wav
from app.services.diarization_service import DiarizationResult, DiarizationService
from app.services.speaker_service import SpeakerService
from app.services.transcription_service import TranscriptionService
from app.services.vad_service import VadService
from app.utils import audio_utils

log = logging.getLogger(__name__)
SAMPLE_RATE = 16_000


class ProcessingPipeline:
    def __init__(self, store: AudioStore, vad: VadService, transcriber: TranscriptionService | None,
                 speaker: SpeakerService | None = None, diarizer: DiarizationService | None = None,
                 diarization_min_speech_seconds: float = 1.0, rules: detection_rules.RulesConfig | None = None,
                 scale: detection_rules.ScoreScale | None = None):
        self.store = store
        self.vad = vad
        self.transcriber = transcriber
        self.speaker = speaker
        self.diarizer = diarizer
        self.diarization_min_speech_seconds = diarization_min_speech_seconds
        self.rules = rules or detection_rules.RulesConfig()
        self.scale = scale or detection_rules.ScoreScale()

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
        diarization_stage, diarization = self._diarize(model_input, vad)
        # optional transcript first: the assistance rule may use it (never required)
        transcription = self.transcriber.transcribe(model_input) if vad.verified and self.transcriber else None
        observations = [detection_rules.speech_observation(message.sessionId, message.eventId, vad,
                                                           audio.duration_seconds, "Silero VAD", self.vad.model_id)]
        if diarization is not None and diarization.speakers:
            observations += self._analyse_speakers(message, model_input, diarization, diarization_stage, transcription)
        findings = analysis_service.analyse(vad, transcription, rms, peak, clipped, gain_db)

        if self.transcriber is None:
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
            observations=observations,
        )
        log.info("Event analysed", extra={"eventId": message.eventId, "sessionId": message.sessionId,
                                           "speechVerified": vad.verified, "speechSeconds": vad.speech_seconds,
                                           "transcriptChars": len(transcription.text) if transcription else 0,
                                           "speakers": diarization.speaker_count if diarization else None,
                                           "overlapSeconds": diarization.overlap_seconds if diarization else None,
                                           "observations": [o["observationType"] for o in observations],
                                           "processingMs": result.processingMs})
        return result

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

    def _analyse_speakers(self, message: AudioEventMessage, samples, diarization: DiarizationResult,
                          stage: DiarizationStage, transcription) -> list[dict]:
        """Diarized speakers -> one CAM++ embedding each (non-overlapping speech only) -> compared with each other ->
        roles -> clip-level observation rules. Speakers stay anonymous."""
        views, embeddings = [], {}
        for sp in diarization.speakers:
            pieces = [samples[int(a * SAMPLE_RATE):int(b * SAMPLE_RATE)] for a, b in sp.exclusive]
            audio = np.concatenate(pieces).astype(np.float32) if pieces else np.zeros(0, np.float32)
            if self.speaker is not None and len(audio) / SAMPLE_RATE >= self.rules.min_voice_seconds:
                try:
                    embeddings[sp.label] = self.speaker.embed(audio)
                except ValueError:
                    pass  # too short for an embedding: this speaker's voice is not compared
            views.append(detection_rules.SpeakerView(
                label=sp.label, speech_seconds=sp.speech_seconds, exclusive_seconds=sp.exclusive_seconds,
                rms_dbfs=sp.rms_dbfs, turns=[(s, e) for s, e, who in diarization.turns if who == sp.label]))

        labels = sorted(embeddings)
        pair_similarity = {(a, b): float(embeddings[a] @ embeddings[b]) for i, a in enumerate(labels) for b in labels[i + 1:]}
        overlap_pieces = [samples[int(a * SAMPLE_RATE):int(b * SAMPLE_RATE)] for a, b in diarization.overlaps]
        overlap_dbfs = audio_utils.rms_dbfs(np.concatenate(overlap_pieces)) if overlap_pieces else None
        main = detection_rules.assign_roles(views, pair_similarity, self.scale, self.rules, overlap_dbfs)

        by_label = {v.label: v for v in views}
        stage.speakers = [{**s, "role": by_label[s["speaker"]].role, "main": by_label[s["speaker"]].main}
                          for s in stage.speakers]
        stage.distinctVoices = detection_rules.distinct_voices(views, pair_similarity, self.scale, main)
        return detection_rules.clip_observations(
            message.sessionId, message.eventId, views, main, diarization.overlaps, pair_similarity,
            transcription.segments if transcription else None, self.scale, self.rules,
            model_name="pyannote segmentation + CAM++",
            model_version=f"{self.diarizer.model_id}; {self.speaker.model_id if self.speaker else 'no voice model'}")
