# Proctoring Audio Monitoring Platform (standalone)

Event-based audio monitoring for online exams: the browser detects speech locally, saves a short
video+audio clip (with the seconds before the speech) and a 16 kHz WAV for each event, uploads the
audio to a .NET backend, which queues it on Redis Streams for a Python AI service (speech verification, speaker
diarization with pyannote, voice comparison between the speakers with CAM++, rule-based observations such as
multiple speakers, overlapping speech and background conversation; transcription optional). Results flow back to the
exam screen.

> **Complete standalone application.** Frontend, backend, Redis Streams and AI service all live in this repository and
> run on one machine; there is no dependency on any other proctoring system. Service boundaries are the versioned
> contracts in [docs/contracts](docs/contracts).

**Status:** working end to end - browser speech capture, upload, Redis Streams, AI analysis (speaker diarization,
voice comparison, observations) and live results on the exam page. Speakers are anonymous: no voice sample is
recorded and nobody is identified. Earlier plans and dated phase reports are kept in [docs/history/](docs/history/).

## Documentation
- [Architecture](docs/architecture.md) — components, data flow, design decisions, privacy controls
- [HTTP API v1](docs/contracts/http-api.v1.md) — REST endpoints, SSE updates
- [Redis Streams v1](docs/contracts/redis-streams.v1.md) — streams, groups, envelope, retries, DLQ
- [JSON Schemas](docs/contracts/schemas) and [examples](docs/contracts/examples)

## Directory structure

```
proctoring-platform/
├── backend/                         ASP.NET Core (.NET 9) - implemented
│   ├── Proctoring.sln
│   ├── .env.example                 backend settings (database, sessions, audio store, Redis, SSE)
│   ├── src/
│   │   ├── Proctoring.Api/              endpoints (sessions, events, SSE updates, health), settings, middleware
│   │   ├── Proctoring.Application/      use cases: sessions, upload, publishing, result ingestion, live updates
│   │   ├── Proctoring.Domain/           Session, AudioEvent (status rules), EventUpdate
│   │   ├── Proctoring.Infrastructure/   EF Core/SQLite + migrations, audio store, queues (local, Redis/), security
│   │   └── Proctoring.Contracts/        HTTP DTOs
│   └── tests/                       Proctoring.UnitTests, Proctoring.IntegrationTests (incl. Redis/), Shared/
├── ai-service/                      Python AI service (Silero VAD + faster-whisper, Redis worker, health API)
├── frontend/                        React + TypeScript + Vite exam page (browser VAD, clips, uploads, live results)
├── docs/                            architecture, implementation plan, contracts (HTTP, Redis, JSON Schemas, examples)
├── infra/
│   ├── docker-compose.yml           Redis (all services in Phase 8)
│   └── docker/redis/redis.conf      append-only persistence, no eviction
├── scripts/redis-streams.ps1        start/stop/inspect local Redis streams
├── .env.example                     shared infrastructure settings (docker compose)
└── README.md
```

Each service has its own `.env.example`; services share **no code**, only the contracts in `docs/contracts`.
Planned Python and React layouts: [ai-service/README.md](ai-service/README.md), [frontend/README.md](frontend/README.md).
Why the backend keeps `src/` + `tests/`: [architecture §0](docs/architecture.md#0-repository-layout).

## Service responsibilities

| Service | Owns | Does not do |
|---|---|---|
| Frontend | devices, local VAD, rolling buffers, clip creation, IndexedDB, upload queue, showing results | AI analysis; uploading continuous audio |
| Backend (.NET) | sessions + tokens, upload validation, audio storage, event status, Redis publishing, result intake, SSE | audio decoding or ML |
| Redis | durable work queue + results stream, retries via consumer groups, dead letters | long-term storage |
| AI service | decoding, speech verification, transcription, diarization, rule findings | user-facing API, decisions about misconduct |

## Setup (Windows PowerShell)

Prerequisites (verified on this machine): Node 24, Python 3.13 (containers use 3.12), .NET SDK 9.0.305,
Docker 29 with Compose v5. Chrome or Edge for the exam page.

```powershell
cd "d:\proctoring poc\proctoring-platform"
Copy-Item backend/.env.example backend/.env   # backend settings (an existing root .env also still works)

# Everything in containers (available from Phase 7)
docker compose up --build

# Local development (available as each phase lands)
docker compose -f infra/docker-compose.yml up -d --wait        # Redis 7.4 with append-only persistence
cd backend;    dotnet run --project src/Proctoring.Api          # http://localhost:5080
cd ai-service; .\.venv\Scripts\python -m uvicorn app.main:app --port 8090   # setup: ai-service/README.md
cd frontend;   npm install; npm run dev                         # http://localhost:5173
```

## Backend (Phase 2)

ASP.NET Core .NET 9 Web API in `backend/` (clean layers: `Domain` → `Application` → `Infrastructure` → `Api`).

```powershell
cd "d:\proctoring poc\proctoring-platform"
Copy-Item backend/.env.example backend/.env   # once; in Development the API reads the nearest .env (backend/, else repo root)
cd backend
dotnet tool restore                   # installs the pinned dotnet-ef 9.0.23 (from .config/dotnet-tools.json)
dotnet build

# Database: migrations run automatically at startup (DATABASE_MIGRATE_ON_START=true).
# To apply them yourself (e.g. with DATABASE_MIGRATE_ON_START=false):
dotnet ef database update --project src/Proctoring.Infrastructure --startup-project src/Proctoring.Api
# To add a migration after changing the model:
dotnet ef migrations add <Name> --project src/Proctoring.Infrastructure --startup-project src/Proctoring.Api --output-dir Persistence/Migrations

# Start the API on http://localhost:5080 (OpenAPI document: /openapi/v1.json)
dotnet run --project src/Proctoring.Api

# Tests (unit + integration; integration tests start the API in memory with a temporary SQLite file)
dotnet test
```

Try it:
```powershell
$s = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/sessions -ContentType application/json `
      -Body '{"candidateId":"cand-123","examId":"exam-42"}'
$h = @{ Authorization = "Bearer $($s.sessionToken)" }
Invoke-RestMethod http://localhost:5080/api/v1/sessions/$($s.sessionId) -Headers $h
Invoke-RestMethod -Method Post http://localhost:5080/api/v1/sessions/$($s.sessionId)/end -Headers $h
Invoke-RestMethod http://localhost:5080/health/ready
```

Backend environment variables (see `backend/.env.example`):

| Variable | Default | Meaning |
|---|---|---|
| `ASPNETCORE_URLS` | `http://0.0.0.0:5080` | listen address |
| `DATABASE_PATH` | `data/db/proctoring.db` | SQLite file; relative to `backend/src/Proctoring.Api` |
| `DATABASE_MIGRATE_ON_START` | `true` | apply migrations at startup |
| `CORS_ORIGINS` | `http://localhost:5173` | comma-separated allowed browser origins |
| `LOG_FORMAT` | `json` | `json` (structured) or `text` |
| `SESSION_MAX_DURATION_HOURS` | `6` | session ends automatically (`endReason: "expired"`) |
| `SESSION_TOKEN_TTL_AFTER_END_HOURS` | `24` | token rejected (401 token-expired) this long after the end |
| `SESSION_UPLOAD_GRACE_SECONDS` | `120` | uploads accepted after the end (used from Phase 3) |
| `SESSION_IDEMPOTENCY_WINDOW_MINUTES` | `60` | a retried `POST /sessions` with the same `Idempotency-Key` returns the same session and token |
| `SESSION_TOKEN_SECRET` | random per process in Development | base64, ≥ 32 bytes; **required in Production**; derives tokens of idempotent creates |
| `RECORDING_*` | see file | recording policy returned to the browser; validated against `session.v1.json` ranges |
| `CONTRACTS_SCHEMA_DIR` | build output `contracts/schemas` | JSON Schemas used for request validation |

Invalid values stop the API at startup with a list of every problem.

## Audio event upload (Phase 3)

```powershell
# 1. Session
$s = Invoke-RestMethod -Method Post http://localhost:5080/api/v1/sessions -ContentType application/json `
      -Body '{"candidateId":"cand-123","examId":"exam-42"}'
$h = @{ Authorization = "Bearer $($s.sessionToken)" }

# 2. A 2-second 16 kHz mono 16-bit test tone
$rate = 16000; $n = 2 * $rate; $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ms)
$w.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $w.Write([int](36 + 2 * $n)); $w.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
$w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]$rate); $w.Write([int](2 * $rate)); $w.Write([int16]2); $w.Write([int16]16)
$w.Write([Text.Encoding]::ASCII.GetBytes("data")); $w.Write([int](2 * $n))
for ($i = 0; $i -lt $n; $i++) { $w.Write([int16](8000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))) }
$wav = $ms.ToArray(); [IO.File]::WriteAllBytes("$PWD\clip.wav", $wav)

# 3. Metadata (must describe the file exactly: size, SHA-256, duration)
$start = [DateTime]::UtcNow
$meta = @{ schemaVersion = "1.0"; eventId = [guid]::NewGuid().ToString(); eventType = "speech"; sequenceNumber = 0
  part = @{ groupId = [guid]::NewGuid().ToString(); index = 0; isFinal = $true; overlapSeconds = 0 }
  startedAt = $start.ToString("o"); endedAt = $start.AddSeconds(2).ToString("o"); speechStartedAt = $start.AddSeconds(0.5).ToString("o")
  durationSeconds = 2.0; preEventSeconds = 0.5
  trigger = @{ type = "vad"; model = "silero-vad-v5"; startThreshold = 0.5; endThreshold = 0.35; maxProbability = 0.95; speechSeconds = 1.5 }
  audio = @{ format = "wav"; codec = "pcm_s16le"; sampleRate = 16000; channels = 1; sizeBytes = $wav.Length
             sha256 = (Get-FileHash clip.wav -Algorithm SHA256).Hash.ToLower() }
  video = @{ recordedLocally = $true; uploaded = $false } }
$meta | ConvertTo-Json -Depth 5 | Set-Content metadata.json -Encoding ascii

# 4. Upload (curl.exe ships with Windows 10+), then read it back
curl.exe -s -X POST "http://localhost:5080/api/v1/sessions/$($s.sessionId)/events" -H "Authorization: Bearer $($s.sessionToken)" `
  -F "metadata=@metadata.json;type=application/json" -F "audio=@clip.wav;type=audio/wav"
Invoke-RestMethod "http://localhost:5080/api/v1/sessions/$($s.sessionId)/events" -Headers $h
```

| Variable | Default | Meaning |
|---|---|---|
| `AUDIO_STORE_PATH` | `data/audio` | audio files; relative to `backend/src/Proctoring.Api` |
| `AUDIO_RETENTION_HOURS` | `24` | stored audio deleted after this; the event record is kept |
| `AUDIO_MIN_DURATION_SECONDS` | `0.1` | shorter audio is rejected |
| `AUDIO_DURATION_TOLERANCE_SECONDS` | `0.2` | metadata vs. measured duration |
| `RECORDING_MAX_UPLOAD_BYTES` / `RECORDING_MAX_EVENT_SECONDS` | `2000000` / `10` | upload size limit / duration limit (+1 s) |
| `EVENT_CLOCK_SKEW_SECONDS` | `300` | client clock tolerance for "event inside session" |
| `EVENT_MAINTENANCE_ENABLED`, `_INTERVAL_SECONDS` | `true`, `30` | background re-publish, cleanup, retention |
| `EVENT_REPUBLISH_AFTER_SECONDS` | `15` | RECEIVED events older than this are re-published |
| `EVENT_MISSING_AUDIO_AFTER_MINUTES` | `10` | RECEIVED events without a file become FAILED |
| `EVENT_STAGING_MAX_AGE_MINUTES` | `60` | leftover staging files are deleted |
| `QUEUE_PROVIDER` | `local-directory` | `redis-streams` (Phase 4) or `local` / `local-directory` (development placeholder, see limitations); no automatic fallback |
| `QUEUE_LOCAL_DIRECTORY` | `data/queue` | where the placeholder writes messages |

**Placeholder queue limitations:** messages are written as JSON files (Redis stream-entry format) and **nothing
consumes them** — events stay `QUEUED`, no analysis happens, and the result endpoint returns 404. It is
single-machine only. Use `QUEUE_PROVIDER=redis-streams` (Phase 4) for the real queue; both implement the same
`IAudioEventQueue` interface. Video upload is not supported yet (422).

## Redis Streams and live updates (Phase 4)

```powershell
docker compose -f infra/docker-compose.yml up -d --wait     # Redis on 127.0.0.1:6379
# backend/.env: QUEUE_PROVIDER=redis-streams, REDIS_CONNECTION=localhost:6379
cd backend; dotnet run --project src/Proctoring.Api
Invoke-RestMethod http://localhost:5080/health/ready         # checks: database + redis
..\scripts\redis-streams.ps1 status                          # stream lengths, groups, pending, dead letters
```

What happens now: an upload is stored in SQLite, appended to `proctoring.audio.events.v1` (metadata + audio
*reference*, never audio bytes) and marked `QUEUED`. The consumer group `ai-audio-processors` is created so the
future processing service will see every event from the first one. The backend's results consumer (group
`backend-results`) waits on `proctoring.audio.results.v1`; since no processing service exists yet, nothing
arrives and events stay `QUEUED`. The backend never invents results.

Live updates: `GET /api/v1/sessions/{id}/updates` with `Authorization: Bearer <token>` is a Server-Sent Events
stream of the session's event changes (`ready`, `event.updated`, `resync`, `end`, heartbeats), resumable with
`Last-Event-ID`; `GET …/events` is the polling fallback. Details: [http-api.v1.md](docs/contracts/http-api.v1.md#real-time-updates).

```powershell
curl.exe -N -H "Authorization: Bearer $($s.sessionToken)" http://localhost:5080/api/v1/sessions/$($s.sessionId)/updates
```

| Variable | Default | Meaning |
|---|---|---|
| `REDIS_CONNECTION` | `localhost:6379` | `host:port` only; credentials in `REDIS_USER` / `REDIS_PASSWORD` (never logged, never in health output) |
| `REDIS_SSL` | `false` | TLS |
| `REDIS_CONNECT_TIMEOUT_MS` / `REDIS_OPERATION_TIMEOUT_MS` | `5000` / `3000` | an upload waits at most this long for Redis |
| `REDIS_EVENTS_STREAM` / `REDIS_RESULTS_STREAM` | `proctoring.audio.events.v1` / `proctoring.audio.results.v1` | stream names |
| `REDIS_DLQ_STREAM` / `REDIS_RESULTS_DLQ_STREAM` | `….events.dlq.v1` / `….results.dlq.v1` | dead letters (processor / backend) |
| `REDIS_AI_GROUP` / `REDIS_CREATE_AI_GROUP` | `ai-audio-processors` / `true` | processing group, created from the first entry on first publish |
| `REDIS_BACKEND_GROUP` | `backend-results` | the backend's group on the results stream |
| `REDIS_STREAM_MAXLEN` | `100000` | approximate cap per stream (`0` = never trim) |
| `REDIS_UPDATES_CHANNEL` | `proctoring.audio.updates.v1` | pub/sub wake-ups between backend instances |
| `RESULTS_CONSUMER_ENABLED` | `true` | run the results consumer in this instance |
| `RESULTS_CLAIM_IDLE_MS` / `RESULTS_CLAIM_INTERVAL_MS` | `60000` / `15000` | reclaim results left pending by a crashed consumer |
| `RESULTS_MAX_DELIVERIES` | `3` | then dead-letter |
| `RESULTS_READ_BATCH` / `RESULTS_POLL_MS` | `10` / `500` | read size / idle pause |
| `SSE_POLL_INTERVAL_MS` / `SSE_HEARTBEAT_SECONDS` / `SSE_REAUTHORIZE_SECONDS` | `1000` / `15` / `60` | live-update stream timing |
| `EVENT_UPDATES_RETENTION_HOURS` | `24` | change-log history for `Last-Event-ID` |

Integration tests start real Redis containers (Testcontainers), so **Docker must be running** for `dotnet test`.

## Run everything (Windows PowerShell)

One command (first-time setup: the backend, AI service and frontend READMEs). It checks the prerequisites, starts
Redis -> backend -> AI service -> frontend, waits for each health check and writes one log per service to `.run/logs/`:

```powershell
.\scripts\run-all.ps1            # start; then open http://localhost:5173 in Chrome or Edge
.\scripts\run-all.ps1 status     # health and URLs
.\scripts\run-all.ps1 stop       # stops only what the script started (add -KeepRedis to keep Redis running)
```

Baseline results: [docs/history/results/phase7-baseline.md](docs/history/results/phase7-baseline.md).

Manually, the same four processes in this order, each in its own terminal:

```powershell
cd "d:\proctoring poc\proctoring-platform"
docker compose -f infra/docker-compose.yml up -d --wait                       # 1. Redis

cd backend                                                                     # 2. backend (port 5080)
$env:QUEUE_PROVIDER="redis-streams"; dotnet run --project src/Proctoring.Api

cd ai-service                                                                  # 3. AI service (port 8090, CPU)
.\.venv\Scripts\python -m uvicorn app.main:app --port 8090

cd frontend                                                                    # 4. exam page
npm run dev                                                                    # open http://localhost:5173 in Chrome or Edge
```

Click **Start exam**, allow camera and microphone, and speak: about 1.5 s after you stop, the event appears as
*Uploading audio* and then *Audio saved*, with a player for the captured speech. Speak again as often as you like;
every stretch of speech becomes its own event. Transcription is optional and off by default
(`TRANSCRIPTION_ENABLED` in `ai-service/.env`).

## AI service (Phase 5)

Full guide: [ai-service/README.md](ai-service/README.md) (Python 3.12 venv setup, models, CPU/GPU, tests).

```powershell
docker compose -f infra/docker-compose.yml up -d --wait                       # Redis
# backend with QUEUE_PROVIDER=redis-streams (backend/.env), then:
cd backend;    dotnet run --project src/Proctoring.Api                         # http://localhost:5080
cd ai-service; .\.venv\Scripts\python -m uvicorn app.main:app --port 8090    # worker + /health/ready
cd ai-service; .\.venv\Scripts\python scripts\send_sample_event.py          # upload a sample WAV, print the result
```

Expected output of the demo (measured): `QUEUED → PROCESSING → COMPLETED` in about 2 s, transcript
`'Can you tell me the answer to question number three?'`, finding `CANDIDATE_SPEECH [info]`.

## Contract check

```powershell
python -m pip install "jsonschema[format]>=4.23" referencing
python docs/contracts/validate_examples.py
```
