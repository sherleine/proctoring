# Redis Streams contract v1

The Redis contract between the backend (.NET) and the processing service (Python). It is the **only**
integration point between the two besides the shared audio storage: neither service depends on the
other's code. Names are configuration (`REDIS_*` variables) so they can follow the company's Redis
conventions later; field names and payload schemas are versioned here.

Implemented by the backend in Phase 4 (`backend/src/Proctoring.Infrastructure/Redis`). The processing
side is specified here and built in Phase 5.

## 1. Streams and groups

| Stream (default name, env var) | Producer | Consumer group (env var) | Content |
|---|---|---|---|
| `proctoring.audio.events.v1` (`REDIS_EVENTS_STREAM`) | backend | `ai-audio-processors` (`REDIS_AI_GROUP`) | `audio.event.submitted`: work to analyse |
| `proctoring.audio.results.v1` (`REDIS_RESULTS_STREAM`) | processing service | `backend-results` (`REDIS_BACKEND_GROUP`) | `audio.analysis.started` / `.completed` / `.failed` |
| `proctoring.audio.events.dlq.v1` (`REDIS_DLQ_STREAM`) | processing service | – (operators) | events the processor gave up on |
| `proctoring.audio.results.dlq.v1` (`REDIS_RESULTS_DLQ_STREAM`) | backend | – (operators) | result messages the backend could not apply |

| Channel | Publisher | Subscriber | Content |
|---|---|---|---|
| `proctoring.audio.updates.v1` (`REDIS_UPDATES_CHANNEL`) | backend | backend | pub/sub wake-up: a session id whose events changed (no data) |

- **Group creation** uses `XGROUP CREATE <stream> <group> 0 MKSTREAM` (from the **first** entry), ignoring
  `BUSYGROUP`. *(Changed from `$` in the Phase 1 draft: with `$`, events published before the group
  existed would be skipped.)* The backend creates `ai-audio-processors` on its first publish
  (`REDIS_CREATE_AI_GROUP=true`) so no event published before the processing service first starts is
  missed; the processing service may create it too (same command, idempotent).
- **Trimming:** `XADD … MAXLEN ~ REDIS_STREAM_MAXLEN` (default 100 000, `0` = never trim). Trimming deletes
  entries even if they are still pending, so size the limit far above the worst backlog (an outage of the
  processing service). SQLite remains the record of every event, so a trimmed event can be re-queued.
- **Persistence:** run Redis with append-only persistence (`appendonly yes`, see
  [infra/docker-compose.yml](../../infra/docker-compose.yml)); entries, groups and pending lists then
  survive a restart.

## 2. Envelope (every entry of both streams)

Stream entries are flat field/value **strings**, exactly these eight fields:

| Field | Example | Notes |
|---|---|---|
| `messageType` | `audio.event.submitted` | see §3 |
| `schemaVersion` | `1` | major version; an unknown major goes to the DLQ |
| `messageId` | UUID | unique per message (a re-publish gets a new one) |
| `correlationId` | event UUID | = `eventId`; ties logs together across services |
| `sessionId` | `ses_…` | |
| `eventId` | UUID | unique within the session; `(sessionId, eventId)` identifies an event |
| `producedAt` | `2026-11-21T10:08:16.512Z` | ISO-8601 UTC |
| `payload` | JSON text | schema depends on `messageType` |

Schema: [schemas/stream-envelope.v1.json](schemas/stream-envelope.v1.json). Dead-letter entries carry the
original fields plus `dlq.*` fields (§5).

## 3. Message types

### `audio.event.submitted` (backend → processing)
`payload` = [audio-event-message.v1.json](schemas/audio-event-message.v1.json): the uploaded event
metadata plus `candidateId`, `examId` and an **audio reference**. Audio bytes are **never** put into Redis.

```json
"audioRef": { "storage": "file", "key": "ses_01JABC…/7f3c2a1e5b6d4c8e9f0a1b2c3d4e5f60.wav",
              "uri": "file:///…/7f3c….wav", "sha256": "…", "sizeBytes": 48044, "contentType": "audio/wav" }
```
- `key` *(added in Phase 4, optional in the schema, always sent)* is the portable reference: the path of the
  WAV relative to the audio store root (`AUDIO_STORE_PATH`). A processor resolves it against its **own**
  configured mount of the same store, so the two services need not share absolute paths. `uri` is the
  backend's local view, for diagnostics only; do not depend on it.
- Verify `sha256` and `sizeBytes` before processing; a mismatch is a permanent failure.
- The WAV is 16 kHz, mono, 16-bit PCM (validated by the backend on upload).

### `audio.analysis.started` (processing → backend) *(new in Phase 4)*
`payload` = [analysis-started.v1.json](schemas/analysis-started.v1.json): `schemaVersion`, `sessionId`,
`eventId`, `startedAt`, `deliveryCount` (from `XPENDING`/`XAUTOCLAIM`), optional `worker`. Sets the event to
`PROCESSING`. Optional: a processor that never sends it simply goes from `QUEUED` to the final state.

### `audio.analysis.completed` (processing → backend)
`payload` = [analysis-result.v1.json](schemas/analysis-result.v1.json) with `status: "completed"`. Sets
`COMPLETED` and stores the result (served by `GET …/events/{id}` and `…/result`).

**Contract 1.1 additions to the result** (all optional, `schemaVersion: "1.1"`): diarization details
(`diarization.speakers[]` with anonymous labels and roles, `distinctVoices`) and `observations[]` (deterministic
`observationId`, so a redelivery never duplicates rows; `category` informational or review). The backend stores each
observation as a row (`observations` table).

### `audio.analysis.failed` (processing → backend)
`payload` = analysis-result with `status: "failed"` and `error: { code, message, retryable, deliveryCount }`.
- `error.code = "DEAD_LETTERED"`: the processor gave up and moved the event to its DLQ → event
  `DEAD_LETTERED` (final).
- any other code → event `FAILED` (technical failure, not a judgement about the candidate). A later
  `started`/`completed` for a **newer** delivery may still move it on (processor retry).

## 4. How the backend applies results (idempotent)

| Current status | `started` | `completed` | `failed` (other) | `failed` (`DEAD_LETTERED`) |
|---|---|---|---|---|
| `RECEIVED`¹ / `QUEUED` | → `PROCESSING` | → `COMPLETED` | → `FAILED` | → `DEAD_LETTERED` |
| `PROCESSING` | (delivery count only) | → `COMPLETED` | → `FAILED` | → `DEAD_LETTERED` |
| `FAILED` | → `PROCESSING` if `deliveryCount` is newer, else ignored | → `COMPLETED` | updated, or ignored if identical | → `DEAD_LETTERED` |
| `COMPLETED`, `DEAD_LETTERED` | ignored | ignored (first result kept) | ignored | ignored |

¹ A result can arrive while the backend still shows `RECEIVED` (it crashed after `XADD` but before recording
`QUEUED`); the backend records `QUEUED` first.

Every applied change is saved in one SQLite transaction together with its live-update entry (§7). Ignored
messages are acknowledged and change nothing. Concurrent writers (two backend instances) are detected by
an optimistic concurrency column; the loser re-reads and decides again.

## 5. Consumer rules (both sides follow the same pattern)

1. Read new entries: `XREADGROUP GROUP <group> <consumer> COUNT <n> STREAMS <stream> >`. Each process uses a
   **unique consumer name** (`<host>-<pid>-<random>`); several instances share the work.
2. **Acknowledge only after the outcome is durable**: the backend `XACK`s after its database commit; the
   processor `XACK`s after it has published its result. A crash before the `XACK` causes a redelivery, which
   the receiver de-duplicates (§4).
3. **Crash recovery:** every `*_CLAIM_INTERVAL_MS`, `XAUTOCLAIM <stream> <group> <consumer> <idle> 0-0` takes
   over entries idle longer than `*_CLAIM_IDLE_MS` (default 60 s): their consumer crashed or is stuck. The
   delivery count comes from `XPENDING`.
4. **Temporary failure** (e.g. database unavailable): do not acknowledge; the entry comes back via step 3.
5. **Retry limit:** when the delivery count exceeds `*_MAX_DELIVERIES` (default 3), dead-letter it.
6. **Poison message** (invalid envelope or payload, unknown `messageType` or major version, unknown event):
   dead-letter immediately.
7. **Dead-lettering** = `XADD` the original fields plus `dlq.reason`, `dlq.deliveryCount`, `dlq.originalStream`,
   `dlq.originalId`, `dlq.consumerGroup`, `dlq.failedAt` to the DLQ stream, **then** `XACK`. If the DLQ write
   fails the entry stays pending and is retried; nothing is lost.
8. **Missing group** (`NOGROUP`, e.g. Redis lost its data): recreate it (§1) and continue.
9. Consumer names that are idle for 24 h with nothing pending are removed (`XGROUP DELCONSUMER`).

The processing service additionally publishes `audio.analysis.failed` with `error.code = "DEAD_LETTERED"` when
it dead-letters an event, so the event does not stay `QUEUED`/`PROCESSING` forever.

## 6. Delivery guarantees and failure cases

Delivery is **at-least-once** end to end; duplicates are expected and harmless (§4). Nothing is exactly-once.

| Failure | What happens | Guarantee |
|---|---|---|
| Backend crashes after the DB commit, before `XADD` | event stays `RECEIVED`; maintenance re-publishes it after `EVENT_REPUBLISH_AFTER_SECONDS` | not lost |
| Backend crashes after `XADD`, before recording `QUEUED` | maintenance publishes it **again** (new `messageId`, same `eventId`) | duplicate; processor de-duplicates by `(sessionId, eventId)` or the backend keeps the first result |
| Redis down during upload | `503` + `Retry-After`; event `RECEIVED` in SQLite; **no** fallback to another queue | re-published when Redis is back |
| Redis restarts (with AOF) | entries, groups and pending lists are restored; clients reconnect | no loss |
| Redis loses its data (no persistence) | groups recreated; unprocessed `RECEIVED` events are re-published; events already `QUEUED` must be re-queued by an operator | documented limitation |
| Processor crashes mid-analysis | entry stays pending; another worker reclaims it (`XAUTOCLAIM`) | redelivered |
| Backend crashes while applying a result | not acknowledged → redelivered | applied once |
| DB unavailable while applying a result | `Retry` → redelivered after `RESULTS_CLAIM_IDLE_MS`, dead-lettered after `RESULTS_MAX_DELIVERIES` | never silently dropped |
| DLQ write fails | entry not acknowledged; retried | not lost |
| Stream trimmed while entries are pending | those entries are lost from Redis | SQLite still has the event; keep `MAXLEN` far above any backlog |

## 7. Live updates (backend only)

Every applied status change is written to the SQLite table `event_updates` **in the same transaction**
(transactional outbox for client notifications). SSE streams read that table; the
`proctoring.audio.updates.v1` pub/sub message only wakes streams early, including streams held by other
backend instances. Pub/sub is not durable: a lost signal delays an update by at most one poll interval
(`SSE_POLL_INTERVAL_MS`). See [http-api.v1.md](http-api.v1.md#real-time-updates).

## 8. Security

- Use a Redis ACL user per service with only the commands and keys it needs:
  backend: `+xadd +xreadgroup +xack +xautoclaim +xpending +xgroup +xinfo +publish +subscribe +ping` on its
  streams and channel; processor: the same on the events stream, its DLQ and the results stream.
  Configure with `REDIS_USER` / `REDIS_PASSWORD` (never inside `REDIS_CONNECTION`); enable TLS with `REDIS_SSL=true`.
- Credentials are never logged and never appear in health responses.
- Payloads contain identifiers, metadata and storage keys, never audio bytes or session tokens.

## Change log

| Version | Change | Compatibility |
|---|---|---|
| 1.0 | Initial draft (Phase 1) | – |
| 1.1 (Phase 4) | Implemented by the backend. New message type `audio.analysis.started`; new stream `proctoring.audio.results.dlq.v1`; `audioRef.key`; groups created from `0` instead of `$`; `error.code = "DEAD_LETTERED"` convention; result application table (§4); `dlq.*` fields. | Additive for producers/consumers of 1.0 messages (no processor existed yet) |
