# HTTP API v1 (standalone backend)

Base path: `/api/v1`. JSON uses camelCase. Errors use RFC 7807 `application/problem+json`:

```json
{ "type": "https://proctoring.local/errors/validation", "title": "Validation failed", "status": 422,
  "detail": "audio.sampleRate must be 16000", "traceId": "00-…", "errors": { "audio.sampleRate": ["must be 16000"] } }
```

Authentication: `Authorization: Bearer <sessionToken>` on every `/sessions/{sessionId}/…` route. The token
is returned once by `POST /sessions`; the backend stores only its SHA-256 hash. A token for session A
used on session B returns `404` (the session's existence is not revealed).

Token format: `tok_` + 43 base64url characters (256 bits).

### Status codes (contract 1.2)

Checks run in this order; the first failing check decides the response.

| # | Condition | Status | `type` | Reveals whether the session exists? |
|---|---|---|---|---|
| 1 | No `Authorization` header, more than one, scheme other than `Bearer` (case-insensitive), empty token, or a token not matching `^tok_[A-Za-z0-9_-]{43}$` | **401** + `WWW-Authenticate: Bearer` | `unauthorized` | No – decided before any lookup |
| 2 | Session ID not matching `^ses_[A-Za-z0-9]{20,}$`, unknown session ID, or a well-formed token that is not this session's token (wrong, or another session's) | **404** | `not-found` | No – one identical body (`detail: "Session not found"`) for all of these |
| 3 | Correct token, but the session ended more than `SESSION_TOKEN_TTL_AFTER_END_HOURS` ago | **401** | `token-expired` | No – only the token holder can reach this |
| – | Correct token, session ended or expired within the TTL | **200** | – | The holder sees `status: "ended"` |

Request-body errors on `POST /sessions` (no credentials involved):

| Condition | Status | `type` |
|---|---|---|
| `Content-Type` is not `application/json` | **415** | `unsupported-media-type` |
| Body larger than 16 KB, by `Content-Length` **or while reading** a chunked body | **413** | `payload-too-large` |
| Body is not valid JSON | **400** | `bad-request` |
| JSON does not match `session-create.request.v1.json`, or malformed `Idempotency-Key` | **422** | `validation` (with `errors`) |
| `Idempotency-Key` reused with a different body, or no longer replayable | **409** | `conflict` |
| Unexpected server error | **500** | `internal` (no internal details) |

Unknown routes return `404` problem+json (`detail: "No such resource"`). Expected errors (4xx) are not
logged as exceptions; tokens, idempotency keys and request bodies are never logged.

Every response carries `X-Correlation-ID` (the caller's value if it matches `^[A-Za-z0-9._-]{1,64}$`,
otherwise a generated one); problem responses also include it as `correlationId`. Other error types:
`…/errors/bad-request` (400, malformed JSON), `…/errors/unsupported-media-type` (415),
`…/errors/payload-too-large` (413), `…/errors/not-found` (404), `…/errors/conflict` (409),
`…/errors/internal` (500, no internal details).

## Sessions

### `POST /api/v1/sessions`
Request ([session-create.request.v1.json](schemas/session-create.request.v1.json)):
```json
{ "candidateId": "cand-123", "examId": "exam-42", "client": { "userAgent": "…", "appVersion": "0.1.0" } }
```
Response `201` ([session.v1.json](schemas/session.v1.json) + token):
```json
{ "sessionId": "ses_01J…", "sessionToken": "<opaque, shown once>", "candidateId": "cand-123", "examId": "exam-42",
  "status": "active", "createdAt": "2026-11-21T10:00:00Z", "endedAt": null,
  "recordingPolicy": { "preEventSeconds": 2.5, "silenceSeconds": 1.5, "maxEventSeconds": 10, "splitOverlapSeconds": 1,
                       "uploadVideo": false, "maxUploadBytes": 2000000 } }
```
`recordingPolicy` lets the server control client recording parameters. The response also contains
`expiresAt` and `endReason` (contract 1.1 additions, see the change log below).

**Idempotency (optional, contract 1.2):** send `Idempotency-Key: <32-128 chars [A-Za-z0-9._:-]>`, generated
randomly by the client (a UUID v4 or 32 random bytes in base64url).

| Situation | Result |
|---|---|
| First request | `201`, new session and token |
| Same key + same body within `SESSION_IDEMPOTENCY_WINDOW_MINUTES` (default 60) while the session is active | `200`, header `Idempotent-Replayed: true`, **the same session and the same token** |
| Concurrent requests with the same key | exactly one `201`; all others `200` with the same session and token |
| Same key, different body | `409` |
| Same key after the window, after the session ended, or after the server secret changed | `409` (use a new key); the existing token keeps working |

How the same token can be returned without storing it: for idempotent creates the token is
`HMAC-SHA256(SESSION_TOKEN_SECRET, sessionId + random salt)`. The database keeps the token's SHA-256 hash,
the key's SHA-256 hash and the salt. The salt is erased when the window passes or the session ends, after
which the token cannot be recreated even with the secret. Because the key can recover the token during the
window, it must be unguessable and is treated as a secret (never logged, stored only as a hash).
Requests without an `Idempotency-Key` get a purely random token, exactly as before.

### `GET /api/v1/sessions/{sessionId}` → `200` session (no token).
### `POST /api/v1/sessions/{sessionId}/end` → `200` session with `status: "ended"`, `endReason: "candidate"`.
Idempotent: ending an ended session returns `200` with the unchanged session. Uploads for events that
ended before `endedAt` are still accepted for `SESSION_UPLOAD_GRACE_SECONDS`.

**Expiry:** a session ends automatically `SESSION_MAX_DURATION_HOURS` after creation (`expiresAt`). It is then
reported as `status: "ended"`, `endReason: "expired"`, `endedAt` = `expiresAt`.

## Events

### `POST /api/v1/sessions/{sessionId}/events` — upload one event (implemented in Phase 3)
`Authorization: Bearer <sessionToken>`, `Content-Type: multipart/form-data` with these parts (any order):

| Part | Content type | Required | Description |
|---|---|---|---|
| `metadata` | `application/json` | yes | [audio-event-metadata.v1.json](schemas/audio-event-metadata.v1.json), at most 16 KB |
| `audio` | `audio/wav` (also `audio/x-wav`, `audio/wave`, `audio/vnd.wave`) | yes | 16 kHz, mono, 16-bit PCM WAV, at most `RECORDING_MAX_UPLOAD_BYTES` |
| `video` | – | **not accepted yet** | returns 422; `metadata.video.uploaded` must be `false` |

The part's file name is ignored; the server generates storage names. `candidateId`/`examId` come from the
authenticated session and are not accepted in the metadata (the schema forbids unknown fields).

**Checks, in order** (the first failing check decides the response):

| Check | Failure |
|---|---|
| Credentials (same rules as all session routes) | 401 `unauthorized` |
| Session exists and the token belongs to it | 404 `not-found` |
| Token still valid (`SESSION_TOKEN_TTL_AFTER_END_HOURS`) | 401 `token-expired` |
| Session accepts uploads: active, or ended less than `SESSION_UPLOAD_GRACE_SECONDS` ago | 409 `session-ended` (`detail` says ended/expired) |
| Request is `multipart/form-data` with a boundary; `metadata` part is `application/json`; `audio` part is a WAV type | 415 `unsupported-media-type` |
| Request / audio part size (enforced **while streaming**; reading stops at the limit) | 413 `payload-too-large` |
| Multipart body well-formed; `metadata` is valid JSON | 400 `bad-request` |
| Exactly one `metadata` and one `audio` part, no unknown parts | 422 `validation` |
| `metadata` matches the JSON Schema | 422 `validation`, `errors` keyed by JSON path |
| `audio` is a real WAV: RIFF/WAVE header, `fmt ` + `data` chunks, sizes consistent, not empty | 422 `validation`, `errors.audio` |
| Encoding is PCM (format tag 1), 16-bit, mono, 16000 Hz | 422 `validation`, `errors.audio` |
| WAV duration ≥ `AUDIO_MIN_DURATION_SECONDS` and ≤ `RECORDING_MAX_EVENT_SECONDS` + 1 | 422 `validation`, `errors.audio` |
| `audio.sizeBytes`, `audio.sha256` equal the received file; `durationSeconds` and `endedAt - startedAt` within `AUDIO_DURATION_TOLERANCE_SECONDS` of the WAV duration; `speechStartedAt` inside the clip; clip inside the session (± `EVENT_CLOCK_SKEW_SECONDS`) | 422 `validation`, `errors` keyed by field |
| Same `eventId` already in this session with different metadata or audio | 409 `conflict` |

**Responses:**
- `202 Accepted` — new event stored and queued: [event-status.v1.json](schemas/event-status.v1.json), `status: "QUEUED"`.
- `200 OK` — the same `eventId` with identical metadata (key order does not matter) and identical audio was already
  received; returns its current status. Nothing is stored or queued twice.
- `503 Service Unavailable` (`type: …/errors/service-unavailable`, `Retry-After: 5`) — the event **was stored**
  (`RECEIVED`) but the queue refused it; the problem body contains the event under `event`. It is re-published
  automatically; retrying the same upload returns `200` with its current status.

Example (`curl`, Git Bash):
```bash
curl -X POST "http://localhost:5080/api/v1/sessions/$SID/events" -H "Authorization: Bearer $TOKEN" \
     -F "metadata=@metadata.json;type=application/json" -F "audio=@clip.wav;type=audio/wav"
```
```json
{ "eventId": "3ae7b4e6-5265-4906-99d7-4758a0d5be6e", "sessionId": "ses_01M7YD8AFECSFD5HC3D36MW66S", "sequenceNumber": 0,
  "status": "QUEUED", "receivedAt": "2026-11-21T06:38:46.6841823Z", "updatedAt": "2026-11-21T06:38:46.8201132Z",
  "startedAt": "2026-11-21T06:38:46.128Z", "endedAt": "2026-11-21T06:38:48.128Z", "durationSeconds": 2,
  "attempts": 0, "error": null, "result": null,
  "audio": { "format": "wav", "codec": "pcm_s16le", "sampleRate": 16000, "channels": 1, "sizeBytes": 64044,
             "sha256": "3f8069…f528", "retained": true } }
```
Responses never contain storage paths or keys; `audio.retained` becomes `false` once retention deleted the file.

### `GET /api/v1/sessions/{sessionId}/events` → `200` `{ "items": [event-status…] }`
Newest first (highest `sequenceNumber`, then latest receipt), at most 500 items.
### `GET /api/v1/sessions/{sessionId}/events/{eventId}` → `200` event-status, including `result` when completed.
`eventId` must be a UUID; unknown IDs and other sessions' events are `404` (indistinguishable).
### `GET /api/v1/sessions/{sessionId}/events/{eventId}/result` → `200` [analysis-result.v1.json](schemas/analysis-result.v1.json), `404` until available.

### `GET /api/v1/sessions/{sessionId}/observations` → `200` (contract 1.1)
`{ "items": [ { "observationId", "sessionId", "eventId", "observationType", "category", "startSeconds", "endSeconds",
"startTimestamp", "endTimestamp", "durationSeconds", "confidence", "confidenceKind", "supportingEvidence",
"modelName", "modelVersion", "createdAt" } ], "note" }`, oldest first. Potential observations for human review,
never determinations of misconduct. `confidenceKind: "model-score"` means a transform of a model score, not a
probability. Session token required.

### Event status lifecycle
```
RECEIVED ──► QUEUED ──► PROCESSING ──► COMPLETED
   │            │            │  └────► FAILED ──► QUEUED (retry) / DEAD_LETTERED
   └► FAILED    └────────────┴───────► DEAD_LETTERED
```
| Status | Meaning | Set by |
|---|---|---|
| `RECEIVED` | stored by the backend, not yet in the queue (queue down, or publishing in progress) | upload (Phase 3) |
| `QUEUED` | published to the processing queue | upload / re-publish job (Phase 3) |
| `PROCESSING`, `COMPLETED`, `DEAD_LETTERED` | analysis in progress / done / given up | results consumer (Phase 4), from processing-service messages only |
| `FAILED` | technical failure; `error.code` e.g. `AUDIO_FILE_MISSING`, `AUDIO_RETENTION_EXPIRED`, or a processor error code (not a judgement about the candidate) | maintenance (Phase 3), results consumer (Phase 4) |

`COMPLETED` and `DEAD_LETTERED` are final: duplicate or late messages never change them, and a completed
event keeps its first result. Only the upload, maintenance and the results consumer change statuses; no read
endpoint does. Until a processing service runs (Phase 5), events stay `QUEUED`: the backend never invents a
result and never fails an event because no processor exists. Rules: [redis-streams.v1.md §4](redis-streams.v1.md).

## Real-time updates

### `GET /api/v1/sessions/{sessionId}/updates` — Server-Sent Events *(implemented in Phase 4)*

Authentication and errors are the same as for `GET …/events` (`401` missing/malformed token or expired
token, `404` unknown session or another session's token, identical bodies). They are answered as
problem+json **before** the stream starts. The browser `EventSource` cannot send an `Authorization`
header, so clients read the stream with `fetch()` (`ReadableStream`) and the bearer token. Tokens are
never accepted in the URL, so they cannot end up in access logs.

Response: `200`, `Content-Type: text/event-stream`, `Cache-Control: no-store`, `X-Accel-Buffering: no`.

```
retry: 3000

id: 41
event: ready
data: {"sessionId":"ses_…","lastEventId":"41"}

id: 42
event: event.updated
data: { …event-status.v1.json, exactly what GET …/events/{eventId} returns… }

: heartbeat
```

| Event | When | `data` |
|---|---|---|
| `ready` | first message of every connection | `sessionId`, `lastEventId` (where the stream starts) |
| `resync` | instead of `ready` when `Last-Event-ID` is not a number or older than the retained history | same as `ready` |
| `event.updated` | an event of this session was added or changed status/result/error/audio | the event's **current** state ([event-status.v1.json](schemas/event-status.v1.json)) |
| `end` | the session token stopped being valid (checked every `SSE_REAUTHORIZE_SECONDS`) | `{ "reason": "token-expired" \| "unauthorized" }`; the server then closes the stream |
| `: heartbeat` (comment) | after `SSE_HEARTBEAT_SECONDS` without other output | – |

- **`id`** is the id of the change-log entry: strictly increasing within a session, **not** contiguous
  (ids are shared by all sessions). Treat it as an opaque resume token.
- Each `event.updated` carries the event's latest state. Several changes of one event that are read
  together are sent once, so intermediate statuses can be skipped; apply messages by `eventId`, and
  remember that `updatedAt` only increases.
- **Reconnect:** send the last received `id` as `Last-Event-ID`. Every change after it is replayed, then
  the stream continues live. Without `Last-Event-ID`, the stream starts at "now": load
  `GET …/events` after `ready`, then apply updates (no gap, because the stream is already open).
- **`resync`:** history older than `EVENT_UPDATES_RETENTION_HOURS` (default 24) is pruned. Reload
  `GET …/events`, then continue with the stream.
- **Source of truth:** messages are read from SQLite (written in the same transaction as the status change),
  so any backend instance can serve any client, and a restart loses nothing.
- **Polling fallback:** when streaming is unavailable (proxy, network), poll `GET …/events` every 3–5 s.
  It returns the same event objects.
- No storage paths, tokens, stack traces or internal error details are sent. A server-side error after the
  stream started simply closes it; the client reconnects with `Last-Event-ID`.

## Health
- `GET /health/live` — process is up.
- `GET /health/ready` — database reachable and, with `QUEUE_PROVIDER=redis-streams`, Redis answers `PING`
  (`503` otherwise). Each check reports only its name, status and a fixed description; connection strings,
  user names, passwords and exception messages are never included.

## Change log

| Version | Change | Compatibility |
|---|---|---|
| 1.0 | Initial contract (Phase 1) | – |
| 1.1 | `session.v1.json`: optional `expiresAt`, `endReason`; `sessionToken` pattern `^tok_[A-Za-z0-9_-]{43}$`. Documented 401/400/413/415 responses, `X-Correlation-ID`, `correlationId` in problem bodies, `Idempotency-Key` / `Idempotent-Replayed` on `POST /sessions`, session expiry. | Additive for tolerant readers; all 1.0 examples remain valid. **Note:** v1.0 `session.v1.json` had `additionalProperties: false`, so a client validating responses *strictly* against v1.0 rejects `expiresAt`/`endReason`. No such client exists yet; frozen v1.0 schemas are kept in `schemas/history/` and tests prove the remaining fields are unchanged. |
| 1.4 | SSE `GET …/updates` implemented. The Phase 1 draft's `event.status` / `event.result` messages and per-session contiguous ids are replaced by `ready`, `resync`, `event.updated` (full event-status) and `end`, with change-log ids (increasing per session, not contiguous). Results consumer sets `PROCESSING` / `COMPLETED` / `FAILED` / `DEAD_LETTERED`. Readiness includes Redis when it is the queue. | Changes only the never-implemented SSE draft (no client exists yet). REST shapes unchanged. |
| 1.3 | Event upload and retrieval implemented. `event-status.v1.json`: optional `endedAt` and `audio` (format, codec, sampleRate, channels, sizeBytes, sha256, retained). New problem types `session-ended` (409) and `service-unavailable` (503). Video parts are rejected (422) in this version. | Additive; all examples remain valid |
| 1.2 | Idempotent replay returns the **same** token (1.1 re-issued a new one and invalidated the old). `Idempotency-Key` minimum length 8 → **32**. A malformed bearer token is **401** (1.1 implementation answered 404). `409` also for replays outside the window. `413` enforced for chunked bodies. Status-code tables above. | **Breaking for 1.1 clients** that send keys shorter than 32 characters or rely on token rotation; no such clients exist yet (frontend not built). Request/response shapes unchanged. |
