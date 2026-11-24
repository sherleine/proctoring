# Frontend — exam page with automatic speech events

React + TypeScript + Vite. The candidate clicks **Start exam**; from then on the browser listens with Silero VAD
for the whole exam and saves **only the moments with speech**: every stretch of speech (with ~2.5 s before it) becomes
its own 16 kHz mono WAV, which is stored in the browser and uploaded to the backend. No button per utterance, no
continuous recording, and no transcript is required (server-side transcription is optional and off by default).

The page talks only to the backend's HTTP API ([http-api.v1.md](../docs/contracts/http-api.v1.md)); it never
talks to Redis or the AI service.

## Setup and run (Windows PowerShell)

Requirements: Node 20+ (verified with Node 24.18), Chrome or Edge. The backend, Redis and AI service must be
running (see the root [README](../README.md#run-everything-windows-powershell)).

```powershell
cd "d:\proctoring poc\proctoring-platform\frontend"
npm install                     # also provides the Silero model and ONNX Runtime
Copy-Item .env.example .env     # optional: change the API URL or VAD thresholds
npm run dev                     # http://localhost:5173 (copies the VAD model to public/models first)
```

Camera and microphone only work on `http://localhost` or HTTPS. The backend must allow the page's origin
(`CORS_ORIGINS=http://localhost:5173`, the default).

| Command | What it does |
|---|---|
| `npm run dev` | development server |
| `npm run build` | type check + production build into `dist/` |
| `npm test` | unit tests + the real Silero model test (Node, no browser) |
| `npm run e2e` | browser end-to-end test (needs all services running; see below) |

### Model files

`npm install` brings the Silero VAD v5 ONNX model (inside `@ricky0123/vad-web`, MIT) and ONNX Runtime Web.
`scripts/copy-assets.mjs` (run automatically by `dev`/`build`) copies the model to `public/models/`; the browser
downloads it once (2.3 MB) when the exam starts and caches it. Inference runs on the CPU via WebAssembly in a
Web Worker (single thread, no GPU, no special server headers needed).

## How it works

| Folder | Responsibility |
|---|---|
| `src/api/` | `client.ts` — typed calls to the backend (sessions, event upload, event list); `sse.ts` — live updates over fetch + `Authorization`, `Last-Event-ID` resume, polling fallback |
| `src/audio/` | `capture.worklet.ts` (audio thread: mono + resampling to 16 kHz), `vad.worker.ts` (Silero + event detection + WAV), `eventDetector.ts` (the event rules), `wav.ts`, `resampler.ts`, `audioPipeline.ts` (wiring) |
| `src/video/` | `videoClipRecorder.ts` — event video clips with pre-roll from two staggered MediaRecorders |
| `src/recording/` | `eventMetadata.ts` (upload metadata), `uploadQueue.ts` (retries, idempotency), `eventStatus.ts` (what each state means) |
| `src/storage/` | `eventStore.ts` — IndexedDB: events, WAV until uploaded, local video, statuses, results |
| `src/hooks/useExam.ts` | the exam lifecycle: devices → session → detection → events → uploads → live results |
| `src/components/`, `src/pages/` | the exam screen |

**State machine** (`src/audio/eventDetector.ts`, one instance for the whole exam):
`IDLE → SPEECH_DETECTED → RECORDING → FINALIZING → IDLE`. Finalizing cuts the clip out of the rolling buffer and
resets only the per-event state; the microphone, AudioWorklet, worker, Silero model and rolling buffer keep running,
so the next event can start on the very next audio frame. Saving an event never waits more than 2 s for its
optional video clip.

**Event rules** (timing from the backend's `recordingPolicy`, i.e. `RECORDING_*` in `backend/.env`):

| Rule | Setting | Default |
|---|---|---|
| speech starts at probability ≥ | `VITE_VAD_START_THRESHOLD` | 0.5 |
| speech continues while probability ≥ | `VITE_VAD_END_THRESHOLD` | 0.35 |
| shorter sounds are ignored | `VITE_VAD_MIN_SPEECH_MS` | 250 ms |
| audio kept before the speech (pre-roll) | `RECORDING_PRE_EVENT_SECONDS` | 2.5 s (from a 5 s rolling buffer) |
| event ends after this much silence (shorter pauses are merged) | `RECORDING_SILENCE_SECONDS` | 1.5 s |
| audio kept after the last speech (word endings) | `VITE_VAD_TAIL_PAD_MS` | 0.5 s - the rest of the closing silence is not saved |
| longest clip; longer speech is split | `RECORDING_MAX_EVENT_SECONDS` | 10 s |
| overlap between split parts | `RECORDING_SPLIT_OVERLAP_SECONDS` | 1 s |

**Audio format:** the worklet mixes the microphone to mono and resamples it with a band-limited (windowed-sinc)
filter from the device rate (usually 48 kHz) to 16 kHz; the worker writes a canonical 44-byte-header PCM 16-bit
WAV and checks it with the same rules as the backend before it is stored.

**Video:** video stays in this browser (IndexedDB, "Show local video" on each event); it is never uploaded. Each
clip starts 0–3 s before the audio clip (see [architecture §4](../docs/architecture.md#4-browser-capture-phase-6))
and records its own start time.

**Uploads:** every event is saved locally first, then uploaded (`POST /api/v1/sessions/{id}/events`). Network
errors and 5xx are retried with back-off (2, 4, 8 … 60 s) re-sending the identical metadata and WAV, so a retry of
an upload that did arrive is recognised by the backend (200) instead of creating a duplicate. Validation errors
are shown with a manual **Retry upload** button. A page reload resumes unfinished uploads.

**Live status:** `GET …/updates` (Server-Sent Events) via `fetch()`, because `EventSource` cannot send the
`Authorization` header. Reconnects send `Last-Event-ID`; after three failures the page polls `GET …/events`
every `VITE_UPDATES_POLL_FALLBACK_MS` while it keeps trying to reconnect.

**What the candidate sees:** live state (*Listening - waiting for speech* → *Speech detected…* → *Recording
speech event*), and for each event: number, time, duration, *Uploading audio* → *Audio saved* (or *Upload failed -
retrying automatically* / *Upload failed - retry required*), a player for the saved WAV (kept in the browser), the
optional local video, and the server analysis state in brackets. Nothing is scored or called cheating.

**Diagnostics** (on the page and on `window.__proctoringDiag`): microphone in use, loudest level and highest speech
probability of the last 5 s, seconds of audio analysed, and events detected / saved / uploaded. A watchdog reports
when no microphone audio has been analysed for 3 s; pipeline errors are shown instead of being swallowed.

**Session token:** kept in memory and in this tab's `sessionStorage` (so a reload can resume the session); it is
sent only in the `Authorization` header and never logged. Closing the tab forgets it.

**Local retention:** stored events older than `VITE_LOCAL_RETENTION_DAYS` (default 7) are deleted when the page
opens.

## Tests

```powershell
npm test       # 36 tests: state machine (4 separate events, 60 events over ~7 min, pause merging, silence, clip
               # bounds), WAV, resampler, metadata vs the JSON Schema, IndexedDB, upload retry, SSE, status mapping,
               # and the REAL Silero ONNX model: 4 recorded utterances -> 4 valid speech-only WAVs
npm run e2e    # Chrome (headless) with a fake microphone playing a WAV, against the running services
```

`npm run e2e` checks, in a real browser against the real backend, Redis and AI service: four utterances with pauses
produce four events, each a valid 16 kHz mono PCM WAV containing the speech with pre-roll and no long silence, all
uploaded and saved; 20 s of silence produce no event; two failed upload attempts are retried and the event is saved;
denied permissions are explained and no session is created.
Screenshots and details land in `test-results/`. It uses Chrome's fake microphone, so it does **not** replace a
test with a real microphone and a real voice.

## Manual test with a real microphone

1. Start Redis, backend, AI service and `npm run dev`; open http://localhost:5173 in Chrome or Edge.
2. **Start exam**, allow camera and microphone: camera preview, *Microphone: on*, *Speech detection: active*.
3. Speak, stop, wait ~5 s, speak again - repeat several times: one event per stretch of speech, each *Audio saved*
   with a playable WAV. A pause shorter than 1.5 s inside a sentence stays in the same event.
4. Stay silent for 20 s: no new event; "Audio analysed" keeps counting up.
5. Stop the backend, speak, watch *Upload will be retried*; start the backend again: the event completes.
6. Unplug or disable the microphone during the exam: the page reports it and stops monitoring.
7. Block camera/microphone in the site settings and start again: the denial is explained.

## Known limitations

- Chromium browsers (Chrome, Edge) are the tested target; Firefox/Safari are not tested.
- Video pre-roll is 0–3 s longer than the audio pre-roll (MediaRecorder segments); timestamps are stored to align them.
- Timestamps come from the audio clock mapped once to wall time; over hours they can drift by a few hundred ms.
- The candidate and exam ids are demo values from `.env`; there is no login (out of scope for this prototype).
