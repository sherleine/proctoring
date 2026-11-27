"""Service entry point: FastAPI (health only) + the Redis worker thread(s).

    uvicorn app.main:app --port 8090        (or: python -m app.main)

Startup: configure logging -> load Silero VAD and faster-whisper once (background thread, so /health/live
answers immediately) -> start AI_WORKER_CONCURRENCY worker threads. There is no endpoint that accepts audio:
audio only arrives through the backend and Redis.
"""
import logging
import threading
from contextlib import asynccontextmanager

from fastapi import FastAPI

from app.api.debug_analyze import router as debug_analyze_router
from app.api.health import router as health_router
from app.clients.redis_client import RedisStreams
from app.core.config import Settings, get_settings
from app.core.logging import configure_logging
from app.services.audio_loader import AudioStore
from app.services.event_worker import EventWorker, default_consumer_name
from app.services.detection_rules import RulesConfig, ScoreScale
from app.services.diarization_service import DiarizationConfig, DiarizationService
from app.services.llm_review_service import VllmClient
from app.services.processing_pipeline import ProcessingPipeline
from app.services.similarity_service import SimilarityService
from app.services.speaker_service import SpeakerService
from app.services.transcription_service import TranscriptionService
from app.services.vad_service import VadService

log = logging.getLogger("app")


class Runtime:
    """Everything the process holds: settings, Redis client, models, workers."""

    def __init__(self, settings: Settings):
        self.settings = settings
        self.streams = RedisStreams(settings)
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
        self.similarity = SimilarityService(settings.similarity_model) if settings.similarity_enabled else None
        self.llm = VllmClient(settings.llm_base_url, settings.llm_model,
                              settings.llm_timeout_seconds, api_key=settings.llm_api_key,
                              max_tokens=settings.llm_max_tokens) if settings.escalation_enabled else None
        self.pipeline = ProcessingPipeline(
            self.store, self.vad, self.transcriber, self.speaker, self.diarizer, settings.diarization_min_speech_seconds,
            RulesConfig(overlap_min_seconds=settings.overlap_min_seconds, multiple_from_overlap=settings.multiple_from_overlap,
                        background_level_db=settings.background_level_db,
                        assistance_min_indicators=settings.assistance_min_indicators,
                        assistance_require_transcript=settings.assistance_require_transcript,
                        min_voice_seconds=settings.speaker_min_segment_seconds),
            ScoreScale(same=settings.speaker_same_threshold, different=settings.speaker_different_threshold),
            escalation_enabled=settings.escalation_enabled, llm=self.llm, similarity=self.similarity)
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
            if self.similarity:
                self.similarity.load()
        except Exception as exc:  # the API stays up and reports not-ready with the reason
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
        "similarity": settings.similarity_enabled, "audioStoreAvailable": runtime.store.is_available(),
        "debugAnalyzeEndpoint": settings.debug_analyze_endpoint_enabled})
    if settings.debug_analyze_endpoint_enabled:
        log.warning("DEBUG_ANALYZE_ENDPOINT_ENABLED=true: POST /debug/analyze accepts audio directly - "
                   "do not enable this in a real deployment")
    app.state.runtime = runtime
    runtime.start()
    yield
    runtime.stop()


_debug_enabled = get_settings().debug_analyze_endpoint_enabled
app = FastAPI(title="Proctoring AI service", version="1.0.0", lifespan=lifespan,
             docs_url="/docs" if _debug_enabled else None, redoc_url=None)
app.include_router(health_router)
if _debug_enabled:
    app.include_router(debug_analyze_router)


if __name__ == "__main__":
    import uvicorn
    uvicorn.run("app.main:app", host="127.0.0.1", port=8090)
