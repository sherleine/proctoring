"""The full analysis of one clip: VAD -> diarization -> CAM++ voices -> ASR -> Tier 1 observations -> Tier 2 review."""
import logging
import time

import numpy as np

from app.clients.llm_client import LlmClient
from app.schemas.AnalysisResultSchema import (AiReview, AnalysisResult, AudioInfo, DiarizationStage, SpeechStage, Span,
                                              TranscriptionStage, TranscriptSegment)
from app.schemas.AudioEventSchema import AudioEventMessage
from app.services import AnalysisService, DetectionRulesService, LlmReviewService
from app.services.AudioLoaderService import AudioStore, decode_wav
from app.services.DiarizationService import DiarizationResult, DiarizationService
from app.services.SpeakerService import SpeakerService
from app.services.TranscriptionService import TranscriptionService
from app.services.VadService import VadService
from app.utils import audio_utils
from app.utils.audio_utils import SAMPLE_RATE, slice_spans

log = logging.getLogger(__name__)


class ProcessingPipeline:
    def __init__(self, store: AudioStore, vad: VadService, transcriber: TranscriptionService | None,
                 speaker: SpeakerService | None = None, diarizer: DiarizationService | None = None,
                 diarization_min_speech_seconds: float = 1.0, rules: DetectionRulesService.RulesConfig | None = None,
                 scale: DetectionRulesService.ScoreScale | None = None, llm: LlmClient | None = None):
        self.store = store
        self.vad = vad
        self.transcriber = transcriber
        self.speaker = speaker
        self.diarizer = diarizer
        self.diarization_min_speech_seconds = diarization_min_speech_seconds
        self.rules = rules or DetectionRulesService.RulesConfig()
        self.scale = scale or DetectionRulesService.ScoreScale()
        self.llm = llm  # None: Tier 2 escalation is off

    def process(self, message: AudioEventMessage) -> AnalysisResult:
        """Redis worker path: reads the clip from the audio store, then analyses it."""
        wav_bytes = self.store.read(message.audioRef)
        return self.analyse(wav_bytes, message.sessionId, message.eventId, message.candidateId)[0]

    def analyse(self, wav_bytes: bytes, session_id: str, event_id: str,
                candidate_id: str) -> tuple[AnalysisResult, np.ndarray | None]:
        """Analyses in-memory WAV bytes. Also returns the main speaker's CAM++ embedding (None when no main voice
        was long enough to embed), which /voice/verify matches against the registered reference voice."""
        started = time.perf_counter()
        audio = decode_wav(wav_bytes)
        samples = audio.samples
        rms, peak = audio_utils.rms_dbfs(samples), audio_utils.peak_dbfs(samples)
        clipped = audio_utils.clipped_ratio(samples)
        model_input, gain_db = audio_utils.normalize_quiet(samples)

        vad = self.vad.detect(model_input)
        diarization_stage, diarization = self._diarize(model_input, vad)
        transcription = self.transcriber.transcribe(model_input) if vad.verified and self.transcriber else None
        observations = [DetectionRulesService.speech_observation(session_id, event_id, vad, audio.duration_seconds,
                                                                 "Silero VAD", self.vad.model_id)]
        main_embedding = None
        if diarization is not None and diarization.speakers:
            speaker_observations, main_embedding = self._analyse_speakers(session_id, event_id, model_input,
                                                                          diarization, diarization_stage, transcription)
            observations += speaker_observations
        findings = AnalysisService.analyse(vad, transcription, rms, peak, clipped, gain_db)
        ai_review = self._escalate(diarization, diarization_stage, transcription, observations)

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
            eventId=event_id, sessionId=session_id, candidateId=candidate_id,
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
            aiReview=AiReview(**ai_review),
        )
        log.info("Event analysed", extra={"eventId": event_id, "sessionId": session_id,
                                           "speechVerified": vad.verified, "speechSeconds": vad.speech_seconds,
                                           "transcriptChars": len(transcription.text) if transcription else 0,
                                           "speakers": diarization.speaker_count if diarization else None,
                                           "overlapSeconds": diarization.overlap_seconds if diarization else None,
                                           "observations": [o["observationType"] for o in observations],
                                           "processingMs": result.processingMs})
        return result, main_embedding

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

    def _analyse_speakers(self, session_id: str, event_id: str, samples, diarization: DiarizationResult,
                          stage: DiarizationStage, transcription) -> tuple[list[dict], np.ndarray | None]:
        """Diarized speakers -> one CAM++ embedding each (non-overlapping speech only) -> compared with each other ->
        roles -> clip-level observation rules. Speakers stay anonymous. Returns (observations, main speaker's
        embedding or None)."""
        views, embeddings = [], {}
        for sp in diarization.speakers:
            audio = slice_spans(samples, sp.exclusive)
            if self.speaker is not None and len(audio) / SAMPLE_RATE >= self.rules.min_voice_seconds:
                try:
                    embeddings[sp.label] = self.speaker.embed(audio)
                except ValueError:
                    pass  # too short for an embedding: this speaker's voice is not compared
            views.append(DetectionRulesService.SpeakerView(
                label=sp.label, speech_seconds=sp.speech_seconds, exclusive_seconds=sp.exclusive_seconds,
                rms_dbfs=sp.rms_dbfs, turns=[(s, e) for s, e, who in diarization.turns if who == sp.label]))

        labels = sorted(embeddings)
        pair_similarity = {(a, b): float(embeddings[a] @ embeddings[b]) for i, a in enumerate(labels) for b in labels[i + 1:]}
        overlap_dbfs = audio_utils.rms_dbfs(slice_spans(samples, diarization.overlaps)) if diarization.overlaps else None
        main = DetectionRulesService.assign_roles(views, pair_similarity, self.scale, self.rules, overlap_dbfs)

        by_label = {v.label: v for v in views}
        stage.speakers = [{**s, "role": by_label[s["speaker"]].role, "main": by_label[s["speaker"]].main}
                          for s in stage.speakers]
        stage.distinctVoices = DetectionRulesService.distinct_voices(views, pair_similarity, self.scale, main)
        main_embedding = embeddings.get(main.label) if main is not None else None
        observations = DetectionRulesService.clip_observations(
            session_id, event_id, views, main, diarization.overlaps, pair_similarity,
            transcription.segments if transcription else None, self.scale, self.rules,
            model_name="pyannote segmentation + CAM++",
            model_version=f"{self.diarizer.model_id}; {self.speaker.model_id if self.speaker else 'no voice model'}")
        return observations, main_embedding

    def _escalate(self, diarization: DiarizationResult | None, diarization_stage: DiarizationStage, transcription,
                  observations: list[dict]) -> dict:
        """Tier 2 on every transcribed clip; "attempted": false comes with the reason it was skipped."""
        if self.llm is None:
            return {"attempted": False, "reason": "Tier 2 escalation is disabled (ESCALATION_ENABLED=false)"}
        if transcription is None:
            return {"attempted": False, "reason": "No speech verified in this clip, so there is no transcript to review"}
        if diarization is not None:
            role_by_label = {s["speaker"]: s.get("role", "uncertain") for s in diarization_stage.speakers}
            tagged = LlmReviewService.tag_transcript(transcription.segments, diarization.turns, role_by_label)
            diarization_summary = {"speakerCount": diarization.speaker_count,
                                   "overlapSeconds": diarization.overlap_seconds,
                                   "distinctVoices": diarization_stage.distinctVoices}
        else:
            # not diarized: every line is "uncertain", and the LLM is told why
            tagged = LlmReviewService.tag_transcript(transcription.segments, [], {})
            diarization_summary = {"speakerCount": None, "overlapSeconds": None, "distinctVoices": None,
                                   "notDiarized": diarization_stage.reason}
        return LlmReviewService.review_clip(
            tagged_segments=tagged, observation_types=[o["observationType"] for o in observations],
            diarization_summary=diarization_summary, llm=self.llm)
