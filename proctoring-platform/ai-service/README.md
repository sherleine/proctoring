# AI service (Python) — speech detection and transcription

A small, independent Python service that analyses the audio events uploaded to the backend:

1. it **reads** `audio.event.submitted` messages from Redis Streams (`proctoring.audio.events.v1`);
2. it **loads** the event's WAV from the shared audio store (using `audioRef.key`);
3. **Silero VAD** checks whether there is actually speech, and where;
4. if transcription is enabled (`TRANSCRIPTION_ENABLED=true`; **off by default**) and there is speech,
   **faster-whisper** transcribes it - otherwise the result simply has no transcript;
5. simple **rules** turn that into observations for a human reviewer;
6. it **publishes** `audio.analysis.started` and then `audio.analysis.completed` (or `…failed`) to
   `proctoring.audio.results.v1`, and only then acknowledges the input message.

The .NET backend consumes the results, stores them in SQLite and moves the event to `PROCESSING` →
`COMPLETED`. This service never touches the backend database and offers **no** endpoint that accepts audio.

> Proof of concept. No speaker diarization (pyannote), no LLM. Findings are observations for human review —
> speech, silence or noise are never treated as evidence of misconduct.

## Contents

```
ai-service/
├── app/
│   ├── main.py                     FastAPI app + startup: load models once, start the worker thread(s)
│   ├── api/health.py               GET /health/live, GET /health/ready
│   ├── clients/redis_client.py     every Redis command (XREADGROUP, XAUTOCLAIM, XADD, XACK, DLQ)
│   ├── core/config.py              settings from environment variables / ai-service/.env
│   ├── core/logging.py             JSON log lines (no audio, transcripts, paths or secrets)
│   ├── core/errors.py              PermanentEventError / TransientEventError
│   ├── schemas/contracts/*.json    copies of the contract JSON Schemas (docs/contracts/schemas)
│   ├── schemas/contracts.py        JSON Schema validation
│   ├── schemas/audio_event.py      incoming audio.event.submitted (validated)
│   ├── schemas/analysis_result.py  outgoing started / completed / failed messages (validated)
│   ├── services/audio_loader.py    safe store access + WAV validation/decoding
│   ├── services/vad_service.py     Silero VAD
│   ├── services/transcription_service.py   faster-whisper
│   ├── services/analysis_service.py        rule-based findings
│   ├── services/processing_pipeline.py     the stages for one event (no Redis)
│   ├── services/event_worker.py            the consumer loop: retries, dead letters, acknowledgement
│   └── utils/                      audio level helpers, timestamps
├── tests/  unit/ (fake models)  integration/ (real Redis, fake models)  models/ (real models)  fixtures/
├── scripts/send_sample_event.py    end-to-end demo through the backend API
├── requirements.txt / requirements-gpu.txt / pyproject.toml / Dockerfile / .env.example
```

`.gitignore` excludes `.venv/`, `.env`, `data/` and `models/`.

## 1. Setup on Windows

The service uses its **own** Python 3.12 (supported by every pinned library) in its own virtual environment;
the machine-wide Python is not touched. [uv](https://docs.astral.sh/uv/) installs a private Python 3.12 for
your user account:

```powershell
cd "d:\proctoring poc\proctoring-platform\ai-service"

# Python 3.12 (skip if `py -3.12 --version` already works)
python -m pip install --user uv          # or: winget install astral-sh.uv
uv python install 3.12

# Virtual environment + dependencies (~1 GB, mostly PyTorch CPU)
uv venv --python 3.12 --seed .venv
.\.venv\Scripts\python -m pip install torch==2.15.0 torchaudio==2.11.1 --index-url https://download.pytorch.org/whl/cpu
.\.venv\Scripts\python -m pip install -r requirements.txt

Copy-Item .env.example .env            # then adjust if needed
```

Versions verified together (`pip check` clean): Python 3.12.16, torch 2.15.0+cpu, torchaudio 2.11.1+cpu,
silero-vad 6.2.3, faster-whisper 1.2.1, ctranslate2 4.9.1, redis 8.1.0, fastapi 0.165.0, uvicorn 0.54.0,
pydantic 2.14.4, numpy 2.5.5, soundfile 0.14.0, jsonschema 4.26.0.

### Models

| Model | How it is obtained |
|---|---|
| Silero VAD 6.2.3 | ships inside the `silero-vad` package — no download |
| faster-whisper `WHISPER_MODEL` | downloaded from Hugging Face on first start into the HF cache (`%USERPROFILE%\.cache\huggingface`, or `HF_HOME`), then reused. `tiny` ≈ 75 MB, `base` ≈ 145 MB, `small` ≈ 480 MB |

To download a model ahead of time:
`.\.venv\Scripts\python -c "from faster_whisper import WhisperModel; WhisperModel('base', device='cpu', compute_type='int8')"`

### Whisper on CPU or GPU

| Setting | CPU (default) | NVIDIA GPU |
|---|---|---|
| `WHISPER_DEVICE` | `cpu` | `cuda` |
| `WHISPER_COMPUTE_TYPE` | `int8` | `float16` (or `int8_float16` for `small`+) |
| extra install | – | `.\.venv\Scripts\python -m pip install -r requirements-gpu.txt` (cuBLAS 12 inside the venv) |

Measured on the development laptop (RTX 3050 Laptop GPU 6 GB, 3.5 s speech clip, warm model):
`base` CPU int8 ≈ 1.3 s, `base` CUDA float16 ≈ 0.12 s, `small` CUDA int8_float16 ≈ 0.26 s — all three gave
the exact transcript. If `cuda` is requested but CTranslate2 finds no usable CUDA device, the service logs a
warning and uses the CPU (`/health/ready` shows the device actually used). Only Whisper uses the GPU; Silero
runs on the CPU (it is tiny), so a 6 GB card is plenty for `base`/`small`.

**Memory note:** loading Whisper needs a few hundred MB of free RAM/commit. On a machine that is already
at its memory limit, loading fails with `mkl_malloc: failed to allocate memory` or `paging file is too small`;
close other programs or use `WHISPER_MODEL=tiny`.

## 2. Configuration (`.env`)

| Variable | Default | Meaning |
|---|---|---|
| `REDIS_CONNECTION` / `REDIS_PASSWORD` | `localhost:6379` / empty | the backend's Redis |
| `REDIS_EVENTS_STREAM` / `REDIS_RESULTS_STREAM` / `REDIS_DLQ_STREAM` | contract names | see [redis-streams.v1.md](../docs/contracts/redis-streams.v1.md) |
| `REDIS_AI_GROUP` | `ai-audio-processors` | consumer group |
| `AI_CONSUMER_NAME` | `<computer>-<pid>` | consumer name in the group |
| `AI_WORKER_CONCURRENCY` | `1` | worker threads (models are shared) |
| `AI_MAX_DELIVERIES` / `AI_CLAIM_IDLE_MS` | `3` / `60000` | retry limit / take over entries of a crashed worker after this idle time |
| `AUDIO_STORE_PATH` | `../backend/src/Proctoring.Api/data/audio` | the backend's audio folder (relative to `ai-service/`) |
| `VAD_THRESHOLD` | `0.5` | Silero speech probability threshold |
| `TRANSCRIPTION_ENABLED` | `false` | `true` loads Whisper and adds transcripts; `false` completes events with speech segments only (Whisper is not loaded) |
| `WHISPER_MODEL` / `WHISPER_DEVICE` / `WHISPER_COMPUTE_TYPE` | `base` / `cpu` / `int8` | see above |
| `WHISPER_LANGUAGE` | empty (auto-detect) | e.g. `en` to force a language |
| `LOG_LEVEL` | `INFO` | |

### Shared audio storage

The backend writes each upload to `{AUDIO_STORE_PATH}/{sessionId}/{random}.wav` and puts the relative part
(`audioRef.key`) in the Redis message. This service opens `{its own AUDIO_STORE_PATH}/{key}`:

- **Both on the same machine (development):** the default `AUDIO_STORE_PATH` already points at the backend's
  folder — nothing to configure.
- **AI service in Docker, backend on the host:** a file inside a container is not visible to other
  containers or the host, so [infra/docker-compose.yml](../infra/docker-compose.yml) bind-mounts the backend's
  folder **read-only** at `/data/audio` (`--profile ai`).
- **Both in Docker (Phase 8):** one named volume, mounted read-write by the backend and read-only here.

The key must match the backend's key format and stay inside the store (no `..`, no absolute paths); the
backend's absolute `uri` is ignored. Size and SHA-256 are checked against the message. The file is only read.

## 3. Run

```powershell
# 1. Redis + backend (see the root README); the backend needs QUEUE_PROVIDER=redis-streams
docker compose -f ..\infra\docker-compose.yml up -d --wait

# 2. The AI service: health API on http://127.0.0.1:8090 + the Redis worker in the same process
.\.venv\Scripts\python -m uvicorn app.main:app --host 127.0.0.1 --port 8090

# 3. Is it ready? (models loaded, Redis reachable, audio store present)
Invoke-RestMethod http://127.0.0.1:8090/health/ready
```

`/health/live` answers as soon as the process runs. `/health/ready` is `503` until both models are loaded
and while Redis or the audio store is unavailable; it shows check results, model names and the Whisper
device — never passwords, tokens or paths.

### Try it with a sample WAV

With the backend (port 5080), Redis and the AI service running:

```powershell
.\.venv\Scripts\python scripts\send_sample_event.py                       # bundled TTS speech sample
.\.venv\Scripts\python scripts\send_sample_event.py C:\path\to\clip.wav    # your own 16 kHz mono 16-bit WAV
```

It creates a session, uploads the WAV like the browser will, and prints the status changes
(`QUEUED → PROCESSING → COMPLETED`), the transcript and the findings. Record your own clip, e.g. with
`ffmpeg -f dshow -i audio="Microphone (...)" -t 5 -ar 16000 -ac 1 -c:a pcm_s16le clip.wav`.

## 4. What the results contain

Every result follows [analysis-result.v1.json](../docs/contracts/schemas/analysis-result.v1.json) and is
validated against it before publishing.

| Field | Content |
|---|---|
| `audio` | duration, sample rate, RMS and peak level (dBFS) of the original audio |
| `speech` | `verified` (≥ 0.25 s of speech), speech seconds, ratio, segments with start/end |
| `transcription` | language, probability, text, segments with timestamps, `avgLogprob`, `noSpeechProb`; `available: false` when no speech was verified (no transcript is invented) |
| `diarization` | always `available: false` ("not included in this version") |
| `findings` | `CANDIDATE_SPEECH` (speech detected; speaker not identified), `NO_SPEECH_VERIFIED`, `UNUSUAL_AUDIO` (clipping / very quiet) — all severity `info` |

Quiet recordings (peak below −30 dBFS) are amplified **in memory** (max +30 dB) before the models see them;
reported levels and the stored file are unchanged. Whisper segments that the model itself marks as "probably
no speech" with low confidence are dropped rather than reported as words.

## 5. Failures, retries, duplicates

| Situation | What happens | Backend status |
|---|---|---|
| message breaks the contract | dead-letter stream + `failed` (`DEAD_LETTERED`) if the ids are usable, then ACK | `DEAD_LETTERED` |
| audio missing, wrong size/checksum, not a 16 kHz mono 16-bit WAV, empty | `failed` with `AUDIO_NOT_FOUND`, `AUDIO_SIZE_MISMATCH`, `AUDIO_CHECKSUM_MISMATCH`, `INVALID_AUDIO`, `EMPTY_AUDIO`, then ACK | `FAILED` |
| VAD or Whisper error (e.g. out of memory) | not acknowledged → retried after `AI_CLAIM_IDLE_MS`; on delivery `AI_MAX_DELIVERIES` → dead letter + `failed` (`DEAD_LETTERED`) | `PROCESSING` → … → `DEAD_LETTERED` |
| Redis unavailable | worker backs off (up to 30 s) and reconnects; nothing is acknowledged meanwhile | unchanged |
| result could not be published | not acknowledged → redelivered | unchanged |
| worker crashed mid-event | another worker (or the restarted one) takes the entry over with `XAUTOCLAIM` | redelivered |
| same event delivered twice | after a final result is published the pair `(sessionId, eventId)` is remembered in Redis (7 days); a repeat is acknowledged without analysing again. If a crash prevents that, the backend still keeps only the first result | unchanged |

Order for every message: publish `started` → analyse → publish final result → remember → `XACK`.

## 6. Tests

```powershell
.\.venv\Scripts\python -m pytest                     # unit + integration (fake models); integration needs Docker
.\.venv\Scripts\python -m pytest tests\unit          # no Docker, no models
.\.venv\Scripts\python -m pytest -m models           # REAL Silero + Whisper (WHISPER_TEST_MODEL, default tiny)
$env:WHISPER_TEST_MODEL="base"; .\.venv\Scripts\python -m pytest -m models
```

| Folder | Models | Redis | Covers |
|---|---|---|---|
| `tests/unit` | fake (deterministic) | in-memory double | WAV validation/loading, store safety, levels, message/result schemas, mapping, rules, pipeline, worker decisions |
| `tests/integration` | fake | real (testcontainers `redis:7.4-alpine`) | group creation, consume/ack, duplicates, missing audio, retries → DLQ, crash recovery, publish failure, Redis outage, health endpoints |
| `tests/models` | **real** Silero + faster-whisper | – | speech/silence/noise/tone, quiet and noisy speech, transcription of real speech, full pipeline |

Synthetic fixtures are generated in `tests/helpers.py` (silence, noise, tone, quiet/noisy speech, invalid
and corrupted WAV). `tests/fixtures/speech_16k_mono.wav` is Windows text-to-speech saying "Can you tell me
the answer to question number three?".

## 7. Using this service in another project

Copy the `ai-service/` folder. It depends on nothing else in this repository: the contract schemas are
bundled in `app/schemas/contracts/` (a test checks they still equal `docs/contracts/schemas` when that folder
exists). The other project must:
1. publish `audio.event.submitted` messages as described in
   [redis-streams.v1.md](../docs/contracts/redis-streams.v1.md) (with `audioRef.key`),
2. make the WAV files readable under `AUDIO_STORE_PATH`,
3. consume `audio.analysis.*` messages from the results stream.

## 8. Limitations

- No speaker diarization: `MULTIPLE_VOICES`, `BACKGROUND_CONVERSATION`, `OVERLAPPING_SPEECH` and
  `POSSIBLE_VERBAL_ASSISTANCE` are never produced.
- Whisper can mis-hear or (rarely) hallucinate words, especially with `tiny`/`base` and in noise; transcripts
  are for human review.
- `speech.verified` depends on `VAD_THRESHOLD`; very quiet or heavily distorted speech can be missed.
- One process: health API and worker share a process; `AI_WORKER_CONCURRENCY > 1` shares one Whisper model.
