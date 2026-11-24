"""One event in, one contract result out - no Redis, no HTTP, so it can be tested and debugged on its own.

    read WAV (store) -> validate/decode -> levels + quiet-audio gain -> Silero VAD
    -> faster-whisper (optional: only when enabled and speech was verified) -> rule-based findings
    -> AnalysisResult (schema-checked)

Errors: PermanentEventError (bad/missing audio) and TransientEventError (model failure) propagate to the
worker, which turns them into a failed result or a retry. Nothing is ever filled in with guessed values.
"""
import logging
import time

from app.schemas.analysis_result import AnalysisResult, AudioInfo, SpeechStage, Span, TranscriptionStage, TranscriptSegment
from app.schemas.audio_event import AudioEventMessage
from app.services import analysis_service
from app.services.audio_loader import AudioStore, decode_wav
from app.services.transcription_service import TranscriptionService
from app.services.vad_service import VadService
from app.utils import audio_utils

log = logging.getLogger(__name__)


class ProcessingPipeline:
    def __init__(self, store: AudioStore, vad: VadService, transcriber: TranscriptionService | None):
        self.store = store
        self.vad = vad
        self.transcriber = transcriber

    @property
    def ready(self) -> bool:
        return self.vad.loaded and (self.transcriber is None or self.transcriber.loaded)

    def process(self, message: AudioEventMessage) -> AnalysisResult:
        started = time.perf_counter()
        audio = decode_wav(self.store.read(message.audioRef))
        samples = audio.samples
        rms, peak = audio_utils.rms_dbfs(samples), audio_utils.peak_dbfs(samples)
        clipped = audio_utils.clipped_ratio(samples)
        model_input, gain_db = audio_utils.normalize_quiet(samples)  # copy; the stored file is untouched

        vad = self.vad.detect(model_input)
        transcription = self.transcriber.transcribe(model_input) if vad.verified and self.transcriber else None
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
                    "diarization": None},
            audio=AudioInfo(durationSeconds=round(audio.duration_seconds, 3), sampleRate=audio.sample_rate,
                            rmsDbfs=rms, peakDbfs=peak),
            speech=SpeechStage(available=True, verified=vad.verified, speechSeconds=vad.speech_seconds,
                               speechRatio=vad.speech_ratio, segments=[Span(start=s, end=e) for s, e in vad.segments]),
            transcription=transcription_stage,
            findings=findings,
        )
        log.info("Event analysed", extra={"eventId": message.eventId, "sessionId": message.sessionId,
                                           "speechVerified": vad.verified, "speechSeconds": vad.speech_seconds,
                                           "transcriptChars": len(transcription.text) if transcription else 0,
                                           "processingMs": result.processingMs})
        return result
