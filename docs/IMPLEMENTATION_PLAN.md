# Implementation plan and compatibility findings

Environment inspected on 2026-11-20: Windows 11 Pro, Node 24.18, npm 11.16, Python 3.13.7,
FFmpeg 9.0.1, NVIDIA RTX 3050 Laptop (6 GB), Chrome + Edge, Docker 29.2.

## Folder responsibilities

| Folder | Responsibility |
| --- | --- |
| `frontend/src/audio` | Framework-free audio engine: Web Audio graph (`audioCapture.ts`), AudioWorklet resampler/framer (`capture.worklet.ts`, `resampler.ts`), ring buffer (`rollingBuffer.ts`). Phase 3–4 add `vadEngine.ts`, `audioProcessor.ts` (VAD Web Worker), `clipRecorder.ts`. |
| `frontend/src/hooks` | React state orchestration: `useMicrophone` (permission, device loss), `useAudioMonitoring` (session lifecycle), `useBackendHealth`, `useEventLog`. Phase 3 adds `useVAD`. |
| `frontend/src/services` | HTTP client for the backend (`audioApi.ts`). No component calls `fetch` directly. |
| `frontend/src/components/{dashboard,audio,common}` | Presentational components only. |
| `frontend/src/config`, `types`, `utils` | Central config from `.env`, TypeScript contracts mirroring the Pydantic schemas, pure helpers (WAV, dB). |
| `backend/app/routes` | HTTP endpoints only: parse input, call a service, return a schema. |
| `backend/app/services` | Business logic: sessions, clip validation/storage, AI forwarding, retries (`ai_client_service.py` is the only code that knows the AI service URL). |
| `backend/app/schemas` / `models` | API request/response models (Pydantic) / persistence models (Phase 5), kept separate. |
| `backend/app/config`, `exceptions`, `middleware`, `utils` | Settings, error hierarchy + JSON handlers, request-ID/timing logging, helpers. |
| `ai_service/app/model_adapters` | One adapter per model. Loads weights once (`load()`), exposes inference. Contracts in `base.py`. |
| `ai_service/app/services` | `analysis_pipeline_service.py` coordinates per-capability services; no model-specific code. |
| `ai_service/app/schemas` | `analysis_schema.py` – the structured result; `event_schema.py` – event types; `audio_schema.py` – clip metadata. |
| `tests/{frontend,backend,ai_service}` | Vitest and pytest suites; `tests/fixtures` generates TTS speech clips. |

## Model compatibility (verified by installing, not assumed)

| Component | Status on this machine | Notes |
| --- | --- | --- |
| **Silero VAD (browser)** via onnxruntime-web 1.31.1 | Model files present (`silero_vad_v5.onnx`, `silero_vad_v6.onnx`, via `@ricky0123/vad-web`) | Inputs `input [1, 64+512]`, `state [2,1,128]`, `sr int64`; outputs `output`, `stateN`. Fixed 512-sample (32 ms) window at 16 kHz, which is slightly above the spec's 20–30 ms. Runs single-threaded WASM in a Web Worker (multi-threaded WASM needs cross-origin isolation). |
| **Silero VAD (server)** `silero-vad 6.2.3` | Installed, imports | Uses torch; also bundles an ONNX model. MIT license. |
| **faster-whisper 1.2.1** / CTranslate2 4.9.0 | Installed, imports; CTranslate2 detects the RTX 3050 | Models download from Hugging Face on first use, no token needed (MIT). GPU needs the cuBLAS 12 + cuDNN 9 DLLs on PATH; default is CPU int8 until GPU is verified in Phase 6. |
| **pyannote.audio 4.1.1** | Installed, imports | `pyannote/speaker-diarization-community-1` is **gated**: needs an HF account, accepting the model terms, and `HF_TOKEN`. Requires torchaudio. The newest torchaudio wheel is 2.11.1, which works with torch 2.15 (resample smoke test + `pip check` clean). CPU only for now. |
| **YAMNet** | Not installed yet | Needs `tensorflow-cpu 2.21.0` + `tensorflow-hub 0.16.1`. A dry run against the current venv shows no conflicts (protobuf 7 accepted). It adds about 500 MB. YAMNet has no "keyboard typing" class; nearest are *Typing*, *Computer keyboard*, *Typewriter* (AudioSet ontology). Alternatives if it underperforms: PANNs CNN14 (PyTorch, no TensorFlow) or AST (HF transformers). |
| **Company LLM** | Not used (decision 2026-11-20: no LLM details available) | `LLM_ENABLED=false`; analysis is rule-based. The adapter interface remains so it can be added later. |

## Company LLM – information needed

1. Endpoint base URL, and reachability from the machine running `ai_service`.
2. API style: OpenAI-compatible `/v1/chat/completions`, Ollama, vLLM, TGI, or proprietary.
3. Model name/version and context window.
4. Authentication: API key header, mTLS, SSO, or none.
5. Structured output: JSON mode / JSON schema / tool calling support.
6. Latency/throughput limits and whether transcripts may be sent to it (data-handling policy).

The adapter will only receive structured evidence (transcript text, speaker counts, timestamps, sound labels), never audio.

## Phases

| Phase | Scope | Depends on | Status |
| --- | --- | --- | --- |
| 1 Foundation | Three apps, central config, exceptions, middleware, health endpoints, schemas, adapter contracts | – | **Done** |
| 2 Audio capture | getUserMedia, AudioWorklet 16 kHz framing, status/errors, level meter, replay check | 1 | **Done** |
| 3 Browser VAD | Silero in Web Worker, probability, hysteresis start/end, noise-floor EWMA | 2 | **Done** |
| 4 Clip generation | Pre-speech buffer, 700 ms silence end, 10 s split, WAV encoding, clip playback, recording dashboard | 3 | **Done** |
| 5 Backend | Sessions, upload/status/result routes, validation, storage + retention, AI forwarding with retries; must return `sha256` of stored bytes | 1, 4 | Next |
| 6 AI models | Adapters + services for Silero, faster-whisper, pyannote, YAMNet; rules; LLM adapter; pipeline | 5 | |
| 7 End-to-end | Dashboard results, consecutive events, failure recovery | 4–6 | |
| 8 Performance | Latency/bandwidth/memory metrics, long sessions, docker-compose, limitations | 7 | |
