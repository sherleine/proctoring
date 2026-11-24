# Architecture

A complete, **standalone** audio-proctoring application: React frontend, .NET 9 backend, Redis Streams and a
Python AI service, all in this repository and runnable on one machine. It does **not** integrate with or depend on
any other proctoring system. Service boundaries are the versioned contracts in [contracts/](contracts/).

Sections 1–8 describe what is implemented today (Phases 1–6). The extensions for suspicious-audio detection
(sound trigger, voice enrolment, sound classification, speaker analysis, rule engine, reviewer interface) are
designed in [audio-research/integration-design.md](audio-research/integration-design.md) and scheduled in
[implementation-plan.md](implementation-plan.md) (Phases 7–13).

## 0. Repository layout

| Path | Contents |
|---|---|
| `backend/` | .NET solution: `src/Proctoring.{Api,Application,Domain,Infrastructure,Contracts}`, `tests/`, `.env.example` |
| `ai-service/` | Python 3.12 AI service: `app/{api,clients,core,schemas,services,utils}`, `tests/{unit,integration,models}`, `scripts/`, Dockerfile, `.env.example` |
| `frontend/` | React + TypeScript app: `src/{api,audio,video,recording,storage,hooks,components,pages,types,utils}`, `tests/`, `scripts/` (assets, browser E2E) |
| `docs/` | architecture, plan, contracts (HTTP + Redis, JSON Schemas, examples) |
| `infra/` | `docker-compose.yml` (Redis now; all services in Phase 8), `docker/redis/redis.conf` |
| `scripts/` | operator scripts (`redis-streams.ps1`) |

The backend keeps its `src/` + `tests/` layout instead of a flat `backend/<Project>` layout: moving the projects
would break the solution file, test project references, the EF Core migration commands and the documented
database paths for no functional gain. The **integration boundary** between services is only the contracts
(HTTP API, Redis messages, JSON Schemas); no service references another's code, and each has its own settings
file, so `ai-service/` and `frontend/` can each move to their own repository.

## 1. Components at a glance

```
 Candidate browser (React)                         Standalone backend (ASP.NET Core .NET 9)
 ┌──────────────────────────────────┐   HTTPS     ┌─────────────────────────────────────────┐
 │ Exam screen + webcam preview     │ ──────────► │ Sessions API      Events API            │
 │ Media worker:                    │  multipart  │ validation, session-scoped tokens       │
 │  - WebCodecs VP8/Opus encoding   │  WAV + JSON │ SQLite (sessions, events, results)      │
 │  - rolling A/V buffers (≈5 s)    │             │ audio file store (shared volume)        │
 │  - Silero VAD (ONNX Runtime Web) │ ◄────────── │ SSE: status + results to the browser    │
 │  - event clips: WebM + WAV 16 kHz│   SSE/poll  └──────────┬───────────────────▲──────────┘
 │ IndexedDB: clips + metadata      │                        │ XADD              │ XREADGROUP
 └──────────────────────────────────┘                        ▼                   │
                                              ┌──────────────────────────────────┴──────────┐
                                              │ Redis Streams                               │
                                              │  proctoring.audio.events.v1   (work queue)  │
                                              │  proctoring.audio.results.v1  (results)     │
                                              │  proctoring.audio.events.dlq.v1 (failures)  │
                                              └──────────┬───────────────────▲──────────────┘
                                                         │ XREADGROUP        │ XADD
                                              ┌──────────▼───────────────────┴──────────────┐
                                              │ AI service (Python FastAPI)                 │
                                              │  decode/resample → speech verification      │
                                              │  → faster-whisper → pyannote → rules        │
                                              └─────────────────────────────────────────────┘
```

| Component | Technology | Why this technology |
|---|---|---|
| Frontend | React 18 + TypeScript + Vite | React builds the UI from components; TypeScript catches type mistakes before running; Vite is a fast dev server/bundler. |
| Media processing | Web Worker, WebCodecs, MediaStreamTrackProcessor | A worker runs off the UI thread so the page stays responsive. WebCodecs gives direct access to the browser's video/audio encoders, which is what makes a *rolling* buffer possible (see §4). |
| Speech detection | Silero VAD via ONNX Runtime Web | Silero is a small neural network trained to detect human speech (not just loudness). ONNX Runtime Web runs it locally in the browser with WebAssembly, so no audio leaves the device for detection. |
| Local storage | IndexedDB | The browser's built-in database; it can store large binary files (Blobs) on disk. `localStorage` cannot. |
| Backend | ASP.NET Core (.NET 9), EF Core + SQLite | ASP.NET Core is a high-performance web framework; EF Core maps C# classes to database tables; SQLite is a single-file database, so no database server is needed for the standalone version. |
| Messaging | Redis Streams + consumer groups | A stream is an append-only log. Consumer groups let several workers share the work, and every message must be *acknowledged*, so nothing is lost if a worker crashes (§5). |
| AI service | Python 3.12, FastAPI, faster-whisper, silero-vad (pyannote.audio deferred) | Python has the best audio-ML ecosystem. FastAPI serves health/diagnostic endpoints; the real work runs in a background stream consumer. |
| Packaging | Docker + Docker Compose | Each service gets a reproducible container; Compose starts all of them (plus Redis) with one command. |

## 2. End-to-end data flow

1. **Session start.** The browser calls `POST /api/v1/sessions` with candidate and exam IDs and receives a
   `sessionId` plus a random **session token**. Every later request for that session must carry the
   token (`Authorization: Bearer …`), which is what "session-scoped access" means here.
2. **Monitoring.** Camera and microphone stay on. An AudioWorklet resamples the microphone to 16 kHz mono;
   a Web Worker runs Silero VAD on it and keeps the last ~5 s of audio. Two staggered MediaRecorders keep
   short video segments. Nothing is stored or uploaded while it is quiet.
3. **Event.** VAD confirms speech → the event starts 2–3 s *before* the trigger (taken from the rolling
   buffer) and continues until 1–2 s of silence. Shorter pauses are merged into the same event; events
   longer than 10 s are split into consecutive parts with a small overlap.
4. **Clip files.** The worker encodes the event audio as a 16 kHz mono 16-bit WAV; the held MediaRecorder
   segment becomes the event's WebM video (video only, with pre-roll). Both are saved in IndexedDB.
5. **Upload.** Only the WAV + metadata are uploaded (`POST /api/v1/sessions/{id}/events`). Video stays
   local (the contract rejects video uploads in this version). Uploads retry with backoff; the client-chosen
   `eventId` makes retries idempotent.
6. **Queue.** The backend validates the request, stores the WAV on the shared audio volume, records the
   event as `QUEUED` and appends a message to `proctoring.audio.events.v1` (it carries a *reference* to
   the file, not the bytes — see [redis-streams.v1.md](contracts/redis-streams.v1.md)).
7. **Analysis.** An AI worker in group `ai-audio-processors` reads the message, runs the pipeline and
   appends an `audio.analysis.completed` (or `…failed`) message to `proctoring.audio.results.v1`, then
   acknowledges the input message.
8. **Results.** A backend background service (group `backend-results`) stores the result and pushes it
   to the browser over **Server-Sent Events**; `GET …/events/{eventId}` works as a polling fallback.
9. **Display.** The event card in the browser shows upload status, AI status and the findings.

## 3a. Upload and recovery (Phase 3)

```
browser ──multipart──► API: authenticate ─► stream "audio" part into {AUDIO_STORE_PATH}/.staging/<random>.part
                                              (size limit + SHA-256 while streaming)
                          validate WAV header + metadata (schema and cross-checks)
                          INSERT audio_events row  (status RECEIVED, storage_key = <sessionId>/<random>.wav)
                          rename staging file ─► {AUDIO_STORE_PATH}/<sessionId>/<random>.wav
                          claim publish lease ─► IAudioEventQueue.Publish ─► status QUEUED
```

**Why the database row is written before the file is moved into place:** the permanent audio directory then
only ever receives files that a database row already points to, so a database failure can never leave an
untracked permanent file. The remaining failure windows are covered explicitly:

| What fails | Result | Recovery |
|---|---|---|
| Client disconnects / upload too large / validation fails | staging file deleted immediately | – |
| Process crashes while streaming | orphan `.part` file in staging | maintenance deletes staging files older than `EVENT_STAGING_MAX_AGE_MINUTES` |
| Database insert fails | staging file deleted; nothing stored | client retries |
| File rename fails after the insert | the row is deleted again (compensation) | client retries |
| Crash between insert and rename | row `RECEIVED` without a file | a retried upload (same eventId and content) re-stores the file after `RepairAfterSeconds` (30 s); otherwise maintenance marks it `FAILED` (`AUDIO_FILE_MISSING`) after `EVENT_MISSING_AUDIO_AFTER_MINUTES` |
| Queue unavailable | row `RECEIVED`, file kept, `503` | maintenance re-publishes every `EVENT_MAINTENANCE_INTERVAL_SECONDS` |
| Crash between publish and "QUEUED" | row stays `RECEIVED` | re-published after the publish lease (30 s) expires: **at-least-once**; consumers de-duplicate by `eventId` |

Duplicates: the key is `(session_id, event_id)`. Identical retries (same canonical metadata hash and audio
SHA-256) return the existing event; different content with the same `eventId` is a `409`. Simultaneous duplicates
race on the primary key: one insert wins, the others become retries of the winner. The publish lease is taken
with a single conditional `UPDATE … WHERE status='RECEIVED' AND lease expired`, so only one caller publishes.

**Storage layout** (`AUDIO_STORE_PATH`, outside any web root; the API serves no static files):
```
data/audio/.staging/<32 hex>.part            uploads in progress
data/audio/<sessionId>/<32 hex>.wav          committed audio (storage key = relative path)
```
All names are generated by the server; keys are checked against `^ses_…/[a-f0-9]{32}\.wav$` and must resolve
inside the root. `IAudioStore` hides all of this: an S3 implementation only has to provide stage / commit /
exists / delete / processing URI.

**Queue placeholder:** `QUEUE_PROVIDER=local-directory` writes each `audio.event.submitted` message — exactly the
stream-entry fields of [redis-streams.v1.md](contracts/redis-streams.v1.md), payload validated against
`audio-event-message.v1.json` — to `QUEUE_LOCAL_DIRECTORY/<sessionId>_<eventId>.json`. It is durable and
idempotent, and it fails loudly if it cannot write, but **nothing consumes it**: events stay `QUEUED`. The Redis
Streams implementation will be another `IAudioEventQueue` (`XADD` with the same fields), selected by
`QUEUE_PROVIDER=redis-streams`; the upload API, statuses and recovery logic do not change.

**Retention:** maintenance deletes stored audio `AUDIO_RETENTION_HOURS` after receipt. The event record stays
(`audio.retained: false`); an event not yet analysed becomes `FAILED` with `AUDIO_RETENTION_EXPIRED`.

## 3. Event lifecycle

```
browser:  RECORDED → UPLOADING → (upload confirmed)
backend:  RECEIVED → QUEUED → PROCESSING → COMPLETED
                                        ↘ FAILED (retryable error; retried up to 3 deliveries)
                                        ↘ DEAD_LETTERED (gave up; message moved to DLQ)
```

A status only advances after the next component confirms it (an upload is not "uploaded" until the
backend answers `202`).

## 4. Browser capture (Phase 6)

```
getUserMedia ─► AudioContext ─► AudioWorklet "capture-processor"      (audio thread)
                                 mono mix + band-limited resampling to 16 kHz, 512-sample frames
                                        │ MessageChannel (frames go straight to the worker)
                                        ▼
                                 Web Worker "vad.worker"                (no UI work)
                                 Silero VAD v5 (onnxruntime-web, WASM, 1 thread, loaded once)
                                 rolling buffer + EventDetector state machine + WAV encoder/validator
                                        │ speech-start / event(WAV) / level
                                        ▼
                                 main thread (React): metadata, IndexedDB, upload queue, SSE, UI
getUserMedia video ─► 2 staggered MediaRecorders (6 s segments, offset 3 s) ─► held segment = event clip
```

- **State machine:** `IDLE → SPEECH_DETECTED → RECORDING → FINALIZING → IDLE`, one instance for the whole exam.
  Finalizing resets only the per-event state; the model, worker, worklet, microphone and rolling buffer keep running.
  Saving an event never waits more than 2 s for its optional video clip, and a failure while processing one audio
  frame is reported without stopping later frames.
- **Event rules** (`EventDetector`, unit-tested): a candidate starts at probability ≥ `VITE_VAD_START_THRESHOLD`
  and becomes an event after `VITE_VAD_MIN_SPEECH_MS` of speech (shorter sounds are dropped). The clip starts
  `recordingPolicy.preEventSeconds` before the first speech frame, merges pauses shorter than `silenceSeconds`
  (hysteresis: speech continues while ≥ `VITE_VAD_END_THRESHOLD`) and ends after `silenceSeconds` of silence; only
  `VITE_VAD_TAIL_PAD_MS` (0.5 s) of that closing silence is kept.
  Clips reaching `maxEventSeconds` are split; the next part repeats `splitOverlapSeconds`. Timing comes from the
  backend's `recordingPolicy`, so browser and backend limits always agree.
- **Timestamps:** every sample has an index; the audio clock is mapped once to wall time
  (`AudioContext.getOutputTimestamp`), so `startedAt`, `endedAt` and `durationSeconds` agree exactly with the WAV.
- **Video with pre-roll:** MediaRecorder cannot cut a playable WebM out of a running recording, so two
  recorders run in turn and restart every 6 s, offset by 3 s. At speech start, the recorder whose segment began
  before the clip start is kept running until the event ends. The video clip therefore starts 0–3 s *before*
  the audio clip (its own start time is stored for alignment); it is never uploaded. (The earlier plan used
  WebCodecs for frame-exact video; MediaRecorder was chosen for this prototype because it is far simpler and
  works in every Chromium browser.)
- **Reliability:** each event is stored in IndexedDB (metadata JSON + WAV + video) *before* upload; the upload
  queue re-sends the identical bytes on retry (idempotent by `eventId`), so reloads and backend outages lose
  nothing. Live status comes from the SSE stream (fetch + `Authorization`, `Last-Event-ID` resume), with
  polling of `GET /events` after repeated stream failures.

## 5. Reliable processing with Redis Streams (Phase 4)

Full contract: [contracts/redis-streams.v1.md](contracts/redis-streams.v1.md). In plain words:

- **A stream is a log** that Redis keeps in order. The backend appends ("`XADD`") one entry per uploaded
  event to `proctoring.audio.events.v1`; the entry holds identifiers, metadata and a **reference** to the WAV
  (`audioRef.key`), never the audio bytes.
- **A consumer group** is a named reader of a stream. Each entry goes to one member of the group, so several
  processing workers share the work. Redis remembers which entries were handed out but not yet
  **acknowledged** (`XACK`): the *pending list*.
- **Crash recovery:** if a worker dies before acknowledging, its entries stay pending. Another worker takes
  them over after `*_CLAIM_IDLE_MS` with `XAUTOCLAIM`. Redis counts how often each entry was delivered.
- **Retries and dead letters:** an entry delivered more than `*_MAX_DELIVERIES` times, or one that can never
  succeed (invalid, unknown event), is copied to a *dead-letter stream* with the reason and acknowledged, so it
  stops blocking and an operator can inspect it.
- **Two directions, the same rules:** the processing service consumes the events stream (group
  `ai-audio-processors`) and writes `audio.analysis.started/completed/failed` to `proctoring.audio.results.v1`;
  the backend consumes that stream (group `backend-results`) and dead-letters unusable results to
  `proctoring.audio.results.dlq.v1`.
- **At-least-once:** every step can repeat after a crash, so every receiver is idempotent: results are keyed
  by `(sessionId, eventId)`, `COMPLETED` / `DEAD_LETTERED` are final, a completed event keeps its first result.

### Backend components

| Component (backend/src/Proctoring.Infrastructure/Redis) | Role |
|---|---|
| `RedisSettings`, `RedisConnection` | `REDIS_*` settings; one shared multiplexer created lazily, reconnecting in the background; commands fail fast while disconnected; the password is never logged |
| `RedisStreamsAudioEventQueue` | `IAudioEventQueue` for `QUEUE_PROVIDER=redis-streams`: `XADD` of the contract envelope; Redis errors become `QueueUnavailableException` |
| `RedisStreamConsumer` | generic consumer-group engine: group creation from `0`, `XREADGROUP`, `XAUTOCLAIM`, delivery counts, ack / retry / dead-letter, `NOGROUP` recovery, stale-consumer cleanup |
| `ResultsConsumerWorker` + `ResultsMessageHandler` | hosted service running the engine on the results stream; envelope validation; one DI scope (one DB connection) per entry |
| `ResultIngestionService` (Application) | the **only** code that applies processing results: schema validation, domain rules, one transaction, `Applied / Ignored / Retry / DeadLetter` |
| `RedisEventUpdateNotifier` | pub/sub wake-up for SSE streams on other backend instances |
| `RedisHealthCheck` | readiness: `PING` within the operation timeout; reports nothing about the connection |

`QUEUE_PROVIDER=local` (alias `local-directory`) keeps the Phase 3 placeholder for development without Redis.
There is **no automatic fallback**: when Redis is configured but down, uploads answer `503` and the events wait
in SQLite.

### Outbox: why no event is lost

The backend needs two writes per upload: SQLite (the event) and Redis (the message). They cannot share one
transaction, so the database acts as the **outbox** (state-based):

1. The event row is committed as `RECEIVED` (with the audio file in place).
2. A short *publish lease* is claimed atomically, so only one instance publishes it.
3. `XADD` to Redis.
4. The status becomes `QUEUED` (only if still `RECEIVED`).

| Crash / outage at | Result |
|---|---|
| before 3 | stays `RECEIVED`; maintenance (`EVENT_REPUBLISH_AFTER_SECONDS`) publishes it |
| between 3 and 4 | maintenance publishes it **again** → duplicate message; the processor / backend de-duplicate |
| Redis down at 3 | `503` + `Retry-After` to the client; same as "before 3" once Redis is back |

A separate outbox table for `audio.event.submitted` was considered and not added: the `RECEIVED` status plus the
lease already *is* that outbox for the one message type, and a second copy of the state could drift from it.

### Live updates (Server-Sent Events)

Every change of an event's status, result, error or stored audio appends a row to `event_updates` **in the same
transaction** (`ProctoringDbContext.SaveChanges`; the `QUEUED` update writes it explicitly in its transaction).
This is a transactional outbox for client notifications:

```
result applied ──► SQLite: audio_events + event_updates (one commit) ──► Notify(sessionId)
                                                                         │ in-process + Redis pub/sub
GET …/updates (any instance) ◄── reads event_updates after Last-Event-ID ◄┘ (or after SSE_POLL_INTERVAL_MS)
```

- The SSE `id` is the `event_updates.id`; a reconnect with `Last-Event-ID` replays everything after it, from
  whichever backend instance the client reaches.
- Pub/sub messages carry only a session id and may be lost; they only shorten the wait. SQLite stays the source
  of truth, and `GET …/events` returns the same state (polling fallback).
- A stream holds no database connection between polls, re-checks the token every `SSE_REAUTHORIZE_SECONDS`,
  sends heartbeats, and ends on client disconnect or application shutdown.
- `event_updates` is pruned after `EVENT_UPDATES_RETENTION_HOURS`; an older `Last-Event-ID` gets `resync`.

### Concurrency

`audio_events.version` is an optimistic concurrency column incremented by every update. If two instances apply
messages for the same event at the same time, the second save fails, the message is re-evaluated against the
fresh state, and a final state is never overwritten.

### Health

`/health/live` checks nothing (the process runs). `/health/ready` checks SQLite and, with Redis as the queue,
`PING`. A Redis outage therefore makes the instance *not ready* (a load balancer stops sending uploads) without
restarting it, and the consumer reconnects by itself.

## 6. AI pipeline (Phase 5, proof of concept)

The Python service in [ai-service/](../ai-service/README.md) consumes `proctoring.audio.events.v1` (group
`ai-audio-processors`) and answers on `proctoring.audio.results.v1`. It is a separate program with its own
virtual environment, settings and bundled copies of the contract schemas; it never touches the backend's
database.

```
XREADGROUP ─► validate message (JSON Schema) ─► publish audio.analysis.started
          ─► read WAV via audioRef.key (inside AUDIO_STORE_PATH, size + SHA-256 checked)
          ─► validate 16 kHz / mono / 16-bit, measure levels, raise quiet audio in memory
          ─► Silero VAD ── no speech ──► result: speech.verified=false, no transcript
                       └─ speech ─────► faster-whisper ─► rule-based findings
          ─► result validated against analysis-result.v1.json ─► XADD results ─► XACK
```

| Stage | What it does | Library |
|---|---|---|
| 1. Load | strict WAV check (format, rate, channels, sample type), float32 samples, original timing; no resampling | soundfile, numpy |
| 2. Speech verification | segments with start/end, speech seconds/ratio, speech probabilities | silero-vad 6.2.3 (bundled model, CPU) |
| 3. Transcription | **optional, off by default** (`TRANSCRIPTION_ENABLED`); when on and speech was verified: language, text, timestamped segments | faster-whisper 1.2.1 (CTranslate2; CPU int8 default, CUDA optional) |
| 4. Diarization | **not included** in this version (`available: false`) | – |
| 5. Findings | `CANDIDATE_SPEECH`, `NO_SPEECH_VERIFIED`, `UNUSUAL_AUDIO` — severity `info` only | plain Python rules |

Models are loaded once at startup in a background thread; `/health/ready` stays `503` until they are loaded.
A permanent problem (missing/invalid audio) becomes an `audio.analysis.failed` result (event `FAILED`); a
model error is retried via the pending list and dead-lettered after `AI_MAX_DELIVERIES` (event
`DEAD_LETTERED`). Findings are **observations for human review**, never a verdict of misconduct.

## 7. Privacy and security controls

- Consent screen before monitoring starts; a visible indicator while monitoring is active.
- No continuous upload: only finalized speech events, and only audio by default.
- Session-scoped bearer tokens (stored hashed); a token only grants access to its own session.
- Retention: server audio deleted after `AUDIO_RETENTION_HOURS`; results after `RESULT_RETENTION_DAYS`;
  browser clips pruned by age/size (`VITE_LOCAL_RETENTION_DAYS`, `VITE_LOCAL_MAX_MB`).
- Transcripts are never written to logs. Request bodies are not logged.
- Upload size and duration limits; strict content-type and WAV header validation.

## 8. Deliberate limits of the standalone version

- Authentication is a per-session token (prototype), not a full identity system.
- Audio files live on a shared Docker volume; production would use object storage (the store is
  behind an interface).
- SQLite is single-node; a production deployment would use a server database. Several backend instances work
  (Redis consumer group, shared change log, pub/sub wake-ups) only when they share the SQLite file on one
  host; across hosts a server database is required.
- Delivery is at-least-once; duplicates are handled by idempotent receivers, not prevented.
- Stream trimming (`REDIS_STREAM_MAXLEN`) can drop entries that are still pending; events already `QUEUED`
  are then not re-published automatically.
- The AI service has no speaker diarization yet; findings never claim several voices.
- Whisper transcripts can contain recognition errors; they are shown for human review only.
- Only speech triggers a recording today; non-speech sounds (phone, typing) are not captured yet (Phase 8).
- Suspicious-audio detection (speaker, sound, whispering, assistance) is designed but not implemented yet; see
  [audio-research/](audio-research/detection-strategy.md) and Phases 7–13 of the plan.
