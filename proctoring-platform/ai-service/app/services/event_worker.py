"""The Redis Streams consumer loop (one thread per AI_WORKER_CONCURRENCY, default 1).

For every entry of proctoring.audio.events.v1 (docs/contracts/redis-streams.v1.md §5):

  1. validate it; an entry that breaks the contract can never succeed -> dead-letter
  2. delivered more than AI_MAX_DELIVERIES times -> dead-letter
  3. (sessionId, eventId) already answered -> acknowledge, nothing else (duplicate delivery)
  4. publish audio.analysis.started (backend: PROCESSING)
  5. run the pipeline
       ok                -> publish audio.analysis.completed
       PermanentEventError -> publish audio.analysis.failed with its code (backend: FAILED)
       TransientEventError -> leave pending; retried after AI_CLAIM_IDLE_MS, dead-lettered at the limit
  6. remember (sessionId, eventId), then XACK - only after the result is in Redis.

Dead-lettering = XADD to the DLQ, publish audio.analysis.failed with error.code DEAD_LETTERED (backend:
DEAD_LETTERED), then XACK. If any Redis write fails, the entry is not acknowledged and comes back later.
"""
import logging
import os
import socket
import threading
import time

import redis

from app.clients.redis_client import UNAVAILABLE, RedisStreams
from app.core.errors import PermanentEventError, TransientEventError
from app.schemas.analysis_result import DEAD_LETTERED, STARTED, AnalysisResult, envelope, failed_result, started_payload
from app.schemas.audio_event import parse_event_entry
from app.services.processing_pipeline import ProcessingPipeline

log = logging.getLogger(__name__)
CLAIM_INTERVAL_SECONDS = 15


def default_consumer_name() -> str:
    return f"{socket.gethostname()}-{os.getpid()}"


class EventWorker:
    def __init__(self, streams: RedisStreams, pipeline: ProcessingPipeline, consumer: str, max_deliveries: int):
        self.streams = streams
        self.pipeline = pipeline
        self.consumer = consumer
        self.max_deliveries = max_deliveries
        self.state = "starting"
        self._next_claim = 0.0

    # ------------------------------------------------------------------ loop

    def run(self, stop: threading.Event) -> None:
        group_ready = False
        failures = 0
        log.info("Worker started", extra={"consumer": self.consumer})
        while not stop.is_set():
            try:
                if not group_ready:
                    self.streams.ensure_group()
                    group_ready = True
                    self.state = "running"
                self.run_once()
                if failures:
                    log.info("Redis reachable again", extra={"consumer": self.consumer})
                failures = 0
                self.state = "running"
            except UNAVAILABLE as exc:
                failures += 1
                self.state = "redis-unavailable"
                delay = min(30.0, 0.5 * 2 ** min(failures, 6))
                if failures == 1 or failures % 20 == 0:
                    log.warning("Redis unavailable; retrying", extra={"error": type(exc).__name__, "retryInSeconds": delay})
                stop.wait(delay)
            except redis.ResponseError as exc:
                if str(exc).startswith("NOGROUP"):  # stream/group vanished (Redis lost its data)
                    log.warning("Consumer group missing; recreating it")
                    group_ready = False
                else:
                    log.exception("Unexpected Redis error")
                    stop.wait(2)
        self.state = "stopped"
        log.info("Worker stopped", extra={"consumer": self.consumer})

    def run_once(self, block_ms: int = 2000) -> int:
        """Reclaims stale entries (every CLAIM_INTERVAL_SECONDS) and handles at most one new entry."""
        handled = 0
        if time.monotonic() >= self._next_claim:
            self._next_claim = time.monotonic() + CLAIM_INTERVAL_SECONDS
            for entry_id, fields in self.streams.claim_stale(self.consumer):
                self.handle(entry_id, fields, self.streams.delivery_count(entry_id))
                handled += 1
        for entry_id, fields in self.streams.read_new(self.consumer, block_ms):
            self.handle(entry_id, fields, 1)
            handled += 1
        return handled

    # ------------------------------------------------------------------ one entry

    def handle(self, entry_id: str, fields: dict, delivery_count: int) -> str:
        """Returns what happened (for logs and tests): completed / failed / duplicate / retry / dead-lettered."""
        context = {"entryId": entry_id, "eventId": fields.get("eventId"), "sessionId": fields.get("sessionId"),
                   "deliveryCount": delivery_count}
        try:
            message = parse_event_entry(fields)
        except PermanentEventError as exc:
            log.warning("Invalid event message", extra={**context, "code": exc.code})
            return self._dead_letter(entry_id, fields, f"{exc.code}: {exc.message}", delivery_count, candidate_id="")

        if delivery_count > self.max_deliveries:
            return self._dead_letter(entry_id, fields, f"delivered {delivery_count} times (limit {self.max_deliveries})",
                                     delivery_count, candidate_id=message.candidateId)

        if self.streams.already_processed(message.sessionId, message.eventId):
            self.streams.ack(entry_id)
            log.info("Duplicate delivery acknowledged without reprocessing", extra=context)
            return "duplicate"

        self.streams.publish_result(envelope(STARTED, message.sessionId, message.eventId,
                                             started_payload(message.sessionId, message.eventId, delivery_count, self.consumer)))
        try:
            result: AnalysisResult = self.pipeline.process(message)
        except PermanentEventError as exc:
            log.warning("Event cannot be analysed", extra={**context, "code": exc.code})
            result = failed_result(message.sessionId, message.eventId, message.candidateId, exc.code, exc.message,
                                   retryable=False, delivery_count=delivery_count)
        except TransientEventError as exc:
            if delivery_count >= self.max_deliveries:
                return self._dead_letter(entry_id, fields, f"PROCESSING_FAILED: {exc}", delivery_count,
                                         candidate_id=message.candidateId)
            log.warning("Processing failed; the entry will be retried", extra={**context, "reason": str(exc)})
            return "retry"  # not acknowledged: comes back via XAUTOCLAIM after AI_CLAIM_IDLE_MS

        self._publish_final(entry_id, result)
        return result.status

    def _publish_final(self, entry_id: str, result: AnalysisResult) -> None:
        self.streams.publish_result(envelope(result.message_type, result.sessionId, result.eventId, result.to_payload()))
        self.streams.mark_processed(result.sessionId, result.eventId)
        self.streams.ack(entry_id)
        log.info("Result published", extra={"entryId": entry_id, "eventId": result.eventId, "status": result.status})

    def _dead_letter(self, entry_id: str, fields: dict, reason: str, delivery_count: int, candidate_id: str) -> str:
        self.streams.dead_letter(entry_id, fields, reason, delivery_count)
        session_id, event_id = fields.get("sessionId"), fields.get("eventId")
        if _usable_ids(session_id, event_id):
            # Tell the backend, so the event shows DEAD_LETTERED instead of waiting forever.
            result = failed_result(session_id, event_id, candidate_id, DEAD_LETTERED, reason,
                                   retryable=False, delivery_count=delivery_count)
            self.streams.publish_result(envelope(result.message_type, session_id, event_id, result.to_payload()))
            self.streams.mark_processed(session_id, event_id)
        self.streams.ack(entry_id)
        log.warning("Entry dead-lettered", extra={"entryId": entry_id, "eventId": event_id, "reason": reason[:200]})
        return "dead-lettered"


def _usable_ids(session_id: str | None, event_id: str | None) -> bool:
    import uuid
    if not session_id or not event_id:
        return False
    try:
        uuid.UUID(event_id)
    except ValueError:
        return False
    return True
