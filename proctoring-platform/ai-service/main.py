"""Service entry point: creates the FastAPI app, mounts the routers and holds the Runtime (clients, models,
Redis worker threads). Models load in a background thread, so /health/live answers immediately.

    uvicorn main:app --port 8090
"""
import logging
import threading
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app import router
from app.clients.llm_client import get_llm_client
from app.clients.redis_client import get_redis_streams
from app.config import Settings, get_settings
from app.core.logging import configure_logging
from app.services.AudioLoaderService import AudioStore
from app.services.EventWorkerService import EventWorker, default_consumer_name
from app.services.DetectionRulesService import RulesConfig, ScoreScale
from app.services.DiarizationService import DiarizationConfig, DiarizationService
from app.services.ProcessingPipelineService import ProcessingPipeline
from app.services.SpeakerService import SpeakerService
from app.services.TranscriptionService import TranscriptionService
from app.services.VadService import VadService
from app.services.VoiceReferenceStoreService import VoiceReferenceStore

log = logging.getLogger("app")


class Runtime:
    """Everything the process holds: settings, Redis client, models, workers."""

    def __init__(self, settings: Settings):
        self.streams = get_redis_streams()
        self.store = AudioStore(settings.audio_root)
        self.vad = VadService(settings.vad_threshold)
        self.transcriber = TranscriptionService(settings.whisper_model, settings.whisper_device,
                                                settings.whisper_compute_type, settings.language,
                                                workers=settings.worker_concurrency,
                                                task=settings.whisper_task) if settings.transcription_enabled else None
        self.speaker = SpeakerService(settings.speaker_model_file, num_threads=settings.speaker_threads) \
            if settings.speaker_enabled else None
        self.diarizer = DiarizationService(
            DiarizationConfig(model=settings.diarization_model, revision=settings.diarization_revision,
                              cache_dir=settings.hf_home_dir, offline=settings.hf_hub_offline,
                              threads=settings.diarization_threads,
                              min_speaker_seconds=settings.diarization_min_speaker_seconds,
                              merge_gap_seconds=settings.diarization_merge_gap_seconds,
                              min_overlap_seconds=settings.diarization_min_overlap_seconds),
            token=settings.hf_token.get_secret_value() if settings.hf_token else None,
        ) if settings.diarization_enabled else None
        self.llm = get_llm_client()
        self.rules = RulesConfig(overlap_min_seconds=settings.overlap_min_seconds, multiple_from_overlap=settings.multiple_from_overlap,
                                 background_level_db=settings.background_level_db,
                                 assistance_min_indicators=settings.assistance_min_indicators,
                                 assistance_require_transcript=settings.assistance_require_transcript,
                                 min_voice_seconds=settings.speaker_min_segment_seconds)
        self.scale = ScoreScale(same=settings.speaker_same_threshold, different=settings.speaker_different_threshold)
        self.voice_reference_store = VoiceReferenceStore(self.streams.client, settings.voice_reference_ttl_seconds)
        self.pipeline = ProcessingPipeline(
            self.store, self.vad, self.transcriber, self.speaker, self.diarizer, settings.diarization_min_speech_seconds,
            self.rules, self.scale, llm=self.llm)
        base = settings.consumer_name or default_consumer_name()
        self.workers = [EventWorker(self.streams, self.pipeline,
                                    base if settings.worker_concurrency == 1 else f"{base}-{i + 1}",
                                    settings.max_deliveries)
                        for i in range(settings.worker_concurrency)]
        self.model_error: str | None = None
        self._stop = threading.Event()
        self._threads: list[threading.Thread] = []

    def start(self) -> None:
        threading.Thread(target=self._load_and_run, name="model-loader", daemon=True).start()

    def _load_and_run(self) -> None:
        try:
            self.vad.load()
            if self.speaker:
                self.speaker.load()
            if self.diarizer:
                self.diarizer.load()
            if self.transcriber:
                self.transcriber.load()
        except Exception as exc:  # the API stays up; /health/ready reports the reason
            self.model_error = f"Model loading failed ({type(exc).__name__})"
            log.exception("Model loading failed; the worker will not start")
            return
        for worker in self.workers:
            thread = threading.Thread(target=worker.run, args=(self._stop,), name=f"worker-{worker.consumer}", daemon=True)
            thread.start()
            self._threads.append(thread)

    def stop(self) -> None:
        self._stop.set()
        for thread in self._threads:
            thread.join(timeout=15)


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = get_settings()
    configure_logging(settings.log_level)
    runtime = Runtime(settings)
    log.info("AI service starting", extra={
        "redis": settings.redis_connection, "redisAuth": settings.redis_password is not None,
        "eventsStream": settings.events_stream, "resultsStream": settings.results_stream,
        "group": settings.consumer_group, "workers": [w.consumer for w in runtime.workers],
        "transcription": settings.transcription_enabled,
        "voiceComparison": settings.speaker_enabled,
        "diarization": settings.diarization_enabled, "hfOffline": settings.hf_hub_offline,
        "whisperModel": settings.whisper_model if settings.transcription_enabled else None, "whisperDevice": settings.whisper_device,
        "computeType": settings.whisper_compute_type, "whisperTask": settings.whisper_task,
        "language": settings.language or "auto", "escalation": settings.escalation_enabled,
        "audioStoreAvailable": runtime.store.is_available(),
        "swaggerDocs": settings.debug_analyze_endpoint_enabled})
    app.state.runtime = runtime
    runtime.start()
    yield
    runtime.stop()


app = FastAPI(title="Proctoring AI service", version="1.0.0", lifespan=lifespan,
             docs_url="/docs" if get_settings().debug_analyze_endpoint_enabled else None, redoc_url=None)
app.include_router(router)


if __name__ == "__main__":
    import uvicorn
    uvicorn.run("main:app", host="127.0.0.1", port=8090)
