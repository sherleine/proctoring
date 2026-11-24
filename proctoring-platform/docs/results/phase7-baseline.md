# Phase 7 - Standalone baseline verification

Date: 24 Nov 2026 (times in UTC unless marked local; local = UTC+05:30).
Scope: confirm that the standalone application (React frontend, .NET backend, Redis Streams, Python AI service)
starts on its own from this repository, that every existing test suite passes, and record a baseline before Phase 8.
No detection behaviour, API contract, database schema or test was changed.

## 1. Environment and versions

| Item | Version / value |
|---|---|
| OS | Windows 11 Pro 10.0.26200 |
| CPU / RAM | Intel Core i5-13450HX (10 cores / 16 threads), 15.7 GB RAM; **1.4-2.0 GB free** during the runs |
| GPU | RTX 3050 6 GB laptop (not used: CPU inference) |
| Shell | Windows PowerShell 5.1.26100 |
| Docker | 29.2.1; Redis image `redis:7.4-alpine` -> Redis 7.4.12 |
| .NET SDK | 9.0.305 (EF Core SQLite 9.0.23, StackExchange.Redis 2.13.23, JsonSchema.Net 9.7.0) |
| Node / npm | 24.18.0 / 11.16.0 (Vite 8.3.8, React 19.3.1, TypeScript 7.0.2, Vitest 5.1.1, onnxruntime-web 1.31.1) |
| Python (AI) | 3.12.16 in `ai-service/.venv` (torch 2.15.0+cpu, silero-vad 6.2.3, fastapi 0.165.0, redis-py 8.1.0, faster-whisper 1.2.1) |
| Browsers | Chrome 157.0.8103.80 (used by the E2E test), Edge 155.0.4283.62 |

## 2. Startup

### Commands (from any directory)

```powershell
.\scripts\run-all.ps1            # = start: prerequisites -> Redis -> backend -> AI service -> frontend
.\scripts\run-all.ps1 status     # process, health and URL of each service
.\scripts\run-all.ps1 stop       # stops only what run-all started, then Redis (data kept)
.\scripts\run-all.ps1 stop -KeepRedis
```

What the script does (see the header of `scripts/run-all.ps1`):

| Order | Service | Started as | Ready when | URL |
|---|---|---|---|---|
| 1 | Redis | `docker compose -f infra/docker-compose.yml up -d redis` | container health = `healthy` (redis-cli PING) | `redis://127.0.0.1:6379` |
| 2 | Backend | `dotnet run --project backend/src/Proctoring.Api --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Development`, `QUEUE_PROVIDER=redis-streams`, `REDIS_CONNECTION`, `CORS_ORIGINS`, `ASPNETCORE_URLS`, `LOG_FORMAT=text` | `GET /health/ready` = 200 (database + Redis) | `http://localhost:5080` |
| 3 | AI service | `ai-service/.venv/Scripts/python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8090` with `AUDIO_STORE_PATH` = the backend's audio folder, `REDIS_CONNECTION`, `OPENBLAS_NUM_THREADS=4` | `GET /health/ready` = 200 (Redis, audio store, VAD loaded, Whisper disabled) | `http://127.0.0.1:8090` |
| 4 | Frontend | `npm run dev -- --port 5173 --strictPort` | `GET /` = 200 | `http://localhost:5173` |

- Paths are derived from the script location; ports can be overridden with `REDIS_PORT`, `BACKEND_PORT`, `AI_PORT`,
  `FRONTEND_PORT`. The variables above are set only for the child processes; the caller's environment is restored.
- Logs: `.run/logs/<service>.out.log` / `.err.log` per service. PIDs + process start times: `.run/processes.json`.
- **Configuration finding:** the repository-root `.env` (an early-phase file) sets `QUEUE_PROVIDER=local-directory`.
  The backend reads it in Development, so a plain `dotnet run` would **not** use Redis. The script prints a warning
  and runs the backend with `QUEUE_PROVIDER=redis-streams` (real environment variables take precedence). The file was
  not edited (it also contains a secret).
- `ai-service/.env` and `frontend/.env` do not exist; defaults apply (transcription off, API `http://localhost:5080`).

### Results

| Check | Result |
|---|---|
| Earlier project processes (3 background tasks from the previous session) stopped; ports 5080/8090/5173 free afterwards | done; no other process touched |
| `run-all.ps1 start` run from `%TEMP%` (outside the repo) | all four services healthy in **30 s** (backend already compiled) |
| Same, with output piped (`... | tail`) | returns in **28 s** (after the fix described in section 6) |
| `run-all.ps1 stop` with an unrelated `python -m http.server` running | the 3 services and Redis stopped; **unrelated process still alive**; no leftover project processes; ports free |
| Start with port 8090 occupied by an unrelated process | refused before starting anything: `Port 8090 (ai) is already in use by another process`; exit code 1; unrelated process untouched |

Health responses (after start):

```
GET :5080/health/live   {"status":"healthy","totalDurationMs":0.1,"checks":[]}
GET :5080/health/ready  {"status":"healthy",...,"checks":[{"name":"database","status":"healthy",...},{"name":"redis","status":"healthy","description":"Redis reachable (1.2 ms)"}]}
GET :8090/health/live   {"status":"live"}
GET :8090/health/ready  {"status":"ready","checks":{"configuration":"ok","redis":"ok","audioStore":"ok","vadModel":"loaded","whisperModel":"disabled","workers":["running"]},"models":{"vad":"silero-vad-6.2.3","asr":null},"transcription":false,...}
```

## 3. Test suites (all executed on 24 Nov 2026)

| Suite | Command (working directory) | Result |
|---|---|---|
| Frontend unit + real Silero model | `npm test` = `vitest run` (`frontend/`) | **36 passed / 0 failed** (5 files) |
| Frontend type check | `npm run typecheck` (`frontend/`) | passed (exit 0) |
| Backend unit | `dotnet test Proctoring.sln` (`backend/`) | **143 passed / 0 failed / 0 skipped** |
| Backend integration (WebApplicationFactory + Redis via Testcontainers) | same command | **117 passed / 0 failed / 0 skipped** |
| AI service default (unit + 12 Redis-container integration tests) | `.venv\Scripts\python -m pytest -q` (`ai-service/`) | **76 passed**, 9 deselected by design (marker `models`, see next row) |
| AI service real models (Silero + Whisper tiny, CPU) | `.venv\Scripts\python -m pytest -q -m models` (`ai-service/`, `HF_HUB_OFFLINE=1`) | **9 passed / 0 failed** |
| Browser E2E (headless Chrome, fake microphone WAV, against the services started by `run-all.ps1`) | `npm run e2e` (`frontend/`) | run 1: **21 passed / 1 failed**; run 2: **22 passed / 0 failed** - see below |

Totals: 36 + 260 + 85 = **381 unit/integration tests passed, 0 failed, 0 skipped** (the 9 AI model tests are
deselected from the default command by the project's `pyproject.toml` and were run separately).

### Browser E2E details

Both runs: four utterances -> four separate events, detector/IndexedDB/backend counts 4/4/4, every WAV valid
16 kHz mono PCM16 (5.53-5.59 s, pre-roll 2.5 s, ~2.05 s voiced), all `COMPLETED` with server-verified speech and
transcription disabled; timestamps follow "Start exam"; 20 s of silence -> 0 events; two failed uploads retried ->
saved once; permission denial explained, no session created.

The one failing check in run 1 was **"local video clip stored in IndexedDB" (0 bytes)**. Stored video sizes per event:

| Run | speech scenario (4 events) | upload-retry scenario (1 event) |
|---|---|---|
| 1 | 0, 0, 0, 0 bytes | 260 247 bytes |
| 2 | 250 737, 233 785, 320 447, **0** bytes | **0** bytes |

So the **optional, local-only video clip** is intermittently missing; audio events are unaffected (every audio event
was saved, uploaded and processed in both runs). Run 2 passed only because the check looks at the first event.
Likely cause [not verified]: the Phase 6 change that stops an audio event from waiting more than **2 s** for its
video clip (`frontend/src/hooks/useExam.ts`, `Promise.race(... 2000)`); when `MediaRecorder.stop()` takes longer
(the machine had ~1.4 GB free RAM), the clip is dropped. Not changed in Phase 7 (frontend behaviour must not
change); recorded as a known issue.

## 4. Real-microphone session

### Procedure

1. `.\scripts\run-all.ps1` (all four services healthy).
2. Open `http://localhost:5173` in **Chrome or Edge**, click **Start exam**, allow microphone (and camera).
3. Speak a sentence, stop, wait ~5 s; repeat 5-6 times; stay silent ~20 s; click **End exam**.
4. Watch on the page: live state, "Audio analysed ... events detected / saved / uploaded", each event's status and
   its audio player (play each one).
5. Verify on the server with the read-only checker (`verify_session.py`, scratchpad): session, per-event status,
   WAV header (16 kHz / mono / 16-bit), SHA-256 equal to the uploaded file, server VAD result.

### Results so far

A real-microphone session was run against the services started by `run-all.ps1` at 05:00 UTC (10:30 local) in
**VS Code's built-in browser** (user agent `Code/1.138.0 Chrome/148`), not in Chrome or Edge:

| # | Start (UTC) | Duration | Gap to previous | Status | Server VAD | WAV on disk |
|---|---|---|---|---|---|---|
| 0 | 05:01:00.252 | 2.55 s | - | Completed | speech 2.17 s | valid 16 kHz mono 16-bit, SHA-256 ok |
| 1 | 05:01:02.648 | 10.02 s | -0.2 s | Completed | speech 6.97 s | valid, SHA-256 ok |
| 2 | 05:01:11.668 | 3.23 s | -1.0 s | Completed | speech 2.81 s | valid, SHA-256 ok |
| 3 | 05:01:18.520 | 10.02 s | 3.6 s | Completed | speech 5.49 s | valid, SHA-256 ok |
| 4 | 05:01:27.540 | 1.50 s | -1.0 s | Completed | **no speech verified** | valid, SHA-256 ok |
| 5 | 05:01:28.728 | 5.43 s | -0.3 s | Completed | speech 3.10 s | valid, SHA-256 ok |
| 6 | 05:01:32.984 | 5.62 s | -1.2 s | Completed | speech 2.87 s | valid, SHA-256 ok |

Seven separate events, all uploaded, processed through Redis and `Completed`; every WAV valid. Negative gaps are
expected: a new event's 2.5 s pre-roll overlaps the end of the previous event when the pause is short, and events 1
and 3 reached the 10 s maximum (split with 1 s overlap). Event 4 (1.5 s) was a short sound the browser VAD triggered
on but the server VAD did not confirm as speech - reported as such, not as an error.

**Still open:** the same session in **Chrome or Edge**, including listening to each event's playback and watching
live updates. Playback can only be confirmed by a person; it is not claimed here.

## 5. CPU and memory (where practical)

Measured after the two E2E runs (11 events processed), with `Get-Process` / `docker stats`:

| Process | Working set | Peak working set | CPU time | Uptime |
|---|---|---|---|---|
| Backend `Proctoring.Api` | 131 MB | 136 MB | 7.0 s | 273 s |
| AI service (uvicorn, Python 3.12) | 255 MB | 255 MB | 4.1 s | 269 s |
| Frontend dev server (node/vite) | 106 MB | 126 MB | 2.7 s | 263 s |
| Redis container | 6.6 MiB | - | 2.5 % CPU (snapshot) | - |

AI processing time per event (from the AI log, 10 events): 72-324 ms, median ~128 ms (Silero VAD only; Whisper off).
Browser-side CPU was not measured in this phase.

## 6. Failures, limitations and skipped checks

| Item | Status |
|---|---|
| Intermittent missing **local video clip** (E2E run 1 failed on it) | Known issue, cause likely the 2 s video wait; audio unaffected; fix proposed for a later phase |
| Real-microphone session in **Chrome/Edge** + playback confirmation | **Pending** (only the VS Code browser session exists) |
| `run-all.ps1` hung when its output was piped | **Fixed during Phase 7**: PowerShell 5.1's `Start-Process -RedirectStandard*` lets children inherit the caller's pipe. Services are now launched via `cmd.exe /d /s /c "<command> > out.log 2> err.log"` through ShellExecute (no handle inheritance). Verified: piped start returns in 28 s |
| Root `.env` sets `QUEUE_PROVIDER=local-directory` | Not changed; overridden by `run-all.ps1`; consider removing the stale root `.env` |
| Browser CPU, end-to-end latency percentiles | Not measured in Phase 7 |
| Skipped tests | None; the 9 AI model tests are deselected by default and were run separately (9/9) |

## 7. Files created or modified in Phase 7

| File | Change |
|---|---|
| `scripts/run-all.ps1` | **new** - start/stop/status for the whole application |
| `.gitignore` | added `.run/` (logs and PID file of `run-all.ps1`) |
| `docs/results/phase7-baseline.md` | **new** - this report |
| `README.md` | run section points to `run-all.ps1` |
