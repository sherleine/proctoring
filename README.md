# Real-Time Audio Monitoring POC

Microphone → browser VAD → speech clip → FastAPI backend → AI service (Silero, faster-whisper,
pyannote, YAMNet, optional company LLM) → results in a React dashboard.

**Current status:** event-based proctoring in the browser. The webcam and microphone are monitored continuously, and a short video+audio clip (plus a separate WAV) is saved only when speech is detected. Clips are stored in IndexedDB. Backend upload and AI analysis are not connected yet.
See [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for compatibility findings and the remaining phases.

```
frontend/    React + TypeScript (Vite). Audio engine in src/audio, runs off the UI thread (AudioWorklet).
backend/     FastAPI API: sessions, uploads, validation, AI forwarding.       Port 8000
ai_service/  FastAPI AI service: model adapters + analysis pipeline.         Port 8001
tests/       frontend (Vitest), backend + ai_service (pytest), fixtures (TTS speech clips)
```

## Setup (Windows PowerShell)

Prerequisites: Node 20+, Python 3.13, Chrome or Edge.

```powershell
cd "d:\proctoring poc"
Copy-Item .env.example .env

# Backend
cd backend
python -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
cd ..

# AI service (torch first, from the PyTorch CPU index)
cd ai_service
python -m venv .venv
.\.venv\Scripts\python -m pip install torch==2.15.0 torchaudio==2.11.1 --index-url https://download.pytorch.org/whl/cpu
.\.venv\Scripts\python -m pip install -r requirements.txt
cd ..

# Frontend
cd frontend
npm install
cd ..
```

## Run (three terminals)

```powershell
# 1 - AI service
cd "d:\proctoring poc\ai_service"; .\.venv\Scripts\python -m uvicorn app.main:app --port 8001

# 2 - Backend
cd "d:\proctoring poc\backend"; .\.venv\Scripts\python -m uvicorn app.main:app --port 8000

# 3 - Frontend (copies Silero/ONNX runtime assets into public/ first)
cd "d:\proctoring poc\frontend"; npm run dev
```

Open http://localhost:5173. The browser asks for the microphone, and monitoring starts automatically after you allow it.

## Using the monitor (event-based recording)

Open http://localhost:5173 in **Chrome or Edge** (event recording uses WebCodecs and MediaStreamTrackProcessor, which Firefox/Safari lack). The browser asks for the camera and microphone separately; the live webcam preview appears.

1. **Start Monitoring**: a MONITORING badge and timer appear. Nothing is saved while it is quiet.
2. Speak: a red **REC event** badge appears. The clip starts **2 s before** speech was detected (rolling buffer), so the first words are kept.
3. Stop speaking: after **1.5 s** of silence the event ends (shorter pauses stay in the same event), 1 s of footage after the last word is kept, and two files are saved:
   - `event_<id>.webm` - video (VP8) + microphone audio (Opus)
   - `event_<id>_audio.wav` - the same audio, 16-bit PCM at the microphone's native rate (usually 48 kHz), covering exactly the same time range
4. Monitoring continues for the next event while clips are being saved. Sounds with less than 0.25 s of speech are ignored. Speech longer than 60 s is split into consecutive clips (`_part2`, ...).
5. **Saved Events** shows one card per clip with video and audio players, Download Video / Download Audio and status SAVED. Clips persist across reloads; Delete removes one.

Settings (in `.env`): `PRE_SPEECH_BUFFER` (s), `EVENT_SILENCE_MS`, `EVENT_POST_SPEECH_MS`, `EVENT_MAX_DURATION` (s), `VITE_VAD_MIN_SPEECH_MS`, `VITE_VAD_START_THRESHOLD` / `VITE_VAD_END_THRESHOLD`, `VITE_VIDEO_BITRATE`.

### How it works

All media work happens in one Web Worker ([eventRecorder.worker.ts](frontend/src/media/eventRecorder.worker.ts)):

- Camera frames and microphone audio arrive through `MediaStreamTrackProcessor` (from clones of the preview tracks).
- Video is encoded continuously with `VideoEncoder` (VP8, keyframe every second) and audio with `AudioEncoder` (Opus). The encoded packets and the raw PCM are kept in rolling buffers covering only the last few seconds.
- The same audio is resampled to 16 kHz for Silero VAD; the existing event-timing logic ([clipRecorder.ts](frontend/src/audio/clipRecorder.ts)) decides when an event starts and ends, merges short pauses and drops short sounds.
- When an event ends, the clip is cut from the buffers starting at the keyframe before "speech - 2 s", written as WebM with [Mediabunny](https://mediabunny.dev) (MPL-2.0) using the original capture timestamps, and the WAV is cut from the PCM over the same time range.
- Device timestamps cannot be trusted across streams (Chrome's camera and microphone use different clocks; a real webcam was observed ~250 s off). Each stream is anchored to arrival time and only the device's frame spacing is used, never placing a frame after it arrived (`StreamClock`). Frames queued before reading starts are discarded.
- Within a clip, audio is one continuous run of samples starting at the sample that matches the first video frame. The WAV and the Opus track are both cut from it by sample position (Opus encoder delay compensated), so the two files are identical in content and timing by construction.
- If the camera or microphone drops out, monitoring stops (the event in progress is saved) and the device is re-acquired automatically (3 attempts).

This avoids starting a recorder after speech is detected (which would lose the beginning) and avoids concatenating MediaRecorder fragments.

## Tests

```powershell
cd "d:\proctoring poc"
cd frontend; npm test; npm run typecheck; cd ..
backend\.venv\Scripts\python -m pytest tests/backend
ai_service\.venv\Scripts\python -m pytest tests/ai_service

# Browser test with real Chrome: fake camera + TTS speech (with pauses) as microphone.
# npm run dev must be running. Monitors for --seconds, checks the UI, downloads every event pair.
powershell -ExecutionPolicy Bypass -File tests\fixtures\generate_speech.ps1
cd frontend; npm run e2e -- --seconds 600 --out ..\data\e2e\acceptance_10min; cd ..
# Check every event pair with ffmpeg: VP8+Opus, keyframe at 0, lengths match,
# WAV == WebM audio track and aligned, pre-event audio before speech
ai_service\.venv\Scripts\python tests\e2e\verify_event_clips.py data\e2e\acceptance_10min
```

## API (so far)

| Service | Endpoint | Purpose |
| --- | --- | --- |
| Backend | `GET /api/health` | Backend status + memory/CPU, plus AI service reachability and per-model status |
| AI service | `GET /health` | Per-component: enabled, provider, package installed/version, loaded, credentials configured |

Interactive docs: http://localhost:8000/docs and http://localhost:8001/docs.
Errors from both services use one shape: `{error_code, message, details, request_id}`. Every response carries `X-Request-ID`.

## Model requirements and licensing

| Model | Download | Auth | License |
| --- | --- | --- | --- |
| Silero VAD v5/v6 | bundled (npm `@ricky0123/vad-web`, pip `silero-vad`) | none | MIT |
| faster-whisper (`WHISPER_MODEL`) | Hugging Face on first start, cached in `data/models` | none | MIT |
| pyannote `speaker-diarization-community-1` | Hugging Face | **gated**: accept terms at huggingface.co/pyannote/speaker-diarization-community-1 and set `HF_TOKEN` | CC-BY-4.0 |
| YAMNet | TF Hub (Phase 6) | none | Apache-2.0 |

## Troubleshooting

- **"Microphone capture is unavailable"**: the page must be opened via `http://localhost` or HTTPS, not a LAN IP over HTTP.
- **"The browser paused audio processing"**: click "Activate audio" (autoplay policy).
- **Services card says AI service unavailable**: start `ai_service` on port 8001; check `AI_SERVICE_URL` in `.env`.
- **Slow service-to-service calls on Windows**: use `127.0.0.1` rather than `localhost` in `AI_SERVICE_URL`; `localhost` tries IPv6 first.
