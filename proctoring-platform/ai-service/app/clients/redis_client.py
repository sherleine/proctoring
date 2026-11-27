
import logging

import redis
from redis.backoff import ExponentialBackoff
from redis.retry import Retry

from app.core.config import Settings
from app.utils.time_utils import utc_now_iso

log = logging.getLogger(__name__)
PROCESSED_TTL_SECONDS = 7 * 24 * 3600


UNAVAILABLE = (redis.ConnectionError, redis.TimeoutError)


class RedisStreams:
    def __init__(self, settings: Settings, client: redis.Redis | None = None):
        self.s = settings
        if client is None:
            host, _, port = settings.redis_connection.rpartition(":")
            client = redis.Redis(
                host=host or "localhost", port=int(port or 6379),
                password=settings.redis_password.get_secret_value() if settings.redis_password else None,
                decode_responses=True, socket_connect_timeout=3, socket_timeout=10, health_check_interval=30,
                
                retry=Retry(ExponentialBackoff(cap=1.0, base=0.2), retries=1))
        self.client = client

    def ping(self) -> bool:
        try:
            return bool(self.client.ping())
        except (redis.RedisError, OSError):
            return False

    def ensure_group(self) -> None:
        """XGROUP CREATE ... 0 MKSTREAM: start from the first entry so nothing published earlier is skipped."""
        try:
            self.client.xgroup_create(self.s.events_stream, self.s.consumer_group, id="0", mkstream=True)
            log.info("Consumer group created", extra={"stream": self.s.events_stream, "group": self.s.consumer_group})
        except redis.ResponseError as exc:
            if not str(exc).startswith("BUSYGROUP"):
                raise

    def read_new(self, consumer: str, block_ms: int = 2000) -> list[tuple[str, dict]]:
        response = self.client.xreadgroup(self.s.consumer_group, consumer, {self.s.events_stream: ">"},
                                          count=1, block=block_ms)
        return [(entry_id, fields) for _, entries in response or [] for entry_id, fields in entries if fields]

    def claim_stale(self, consumer: str, count: int = 10) -> list[tuple[str, dict]]:
        """XAUTOCLAIM entries idle longer than AI_CLAIM_IDLE_MS (their worker crashed or gave up for now)."""
        _, entries, _deleted = self.client.xautoclaim(self.s.events_stream, self.s.consumer_group, consumer,
                                                      min_idle_time=self.s.claim_idle_ms, start_id="0-0", count=count)
        return [(entry_id, fields) for entry_id, fields in entries if fields]

    def delivery_count(self, entry_id: str) -> int:
        pending = self.client.xpending_range(self.s.events_stream, self.s.consumer_group, min=entry_id, max=entry_id, count=1)
        return int(pending[0]["times_delivered"]) if pending else 1

    def publish_result(self, fields: dict[str, str]) -> str:
        maxlen = self.s.stream_max_length or None
        return self.client.xadd(self.s.results_stream, fields, maxlen=maxlen, approximate=True)

    def ack(self, entry_id: str) -> None:
        self.client.xack(self.s.events_stream, self.s.consumer_group, entry_id)

    def dead_letter(self, entry_id: str, fields: dict, reason: str, delivery_count: int) -> None:
        """Original fields + dlq.* fields (same layout as the backend's results DLQ)."""
        entry = {k: v for k, v in fields.items() if not k.startswith("dlq.")}
        entry.update({"dlq.reason": reason[:500], "dlq.deliveryCount": str(delivery_count),
                      "dlq.originalStream": self.s.events_stream, "dlq.originalId": entry_id,
                      "dlq.consumerGroup": self.s.consumer_group, "dlq.failedAt": utc_now_iso()})
        self.client.xadd(self.s.dlq_stream, entry, maxlen=self.s.stream_max_length or None, approximate=True)

    
    
    def _processed_key(self, session_id: str, event_id: str) -> str:
        return f"{self.s.events_stream}:processed:{self.s.consumer_group}:{session_id}:{event_id}"

    def already_processed(self, session_id: str, event_id: str) -> bool:
        return bool(self.client.exists(self._processed_key(session_id, event_id)))

    def mark_processed(self, session_id: str, event_id: str) -> None:
        self.client.set(self._processed_key(session_id, event_id), utc_now_iso(), ex=PROCESSED_TTL_SECONDS)
