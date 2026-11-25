# Frontend walkthrough

A guide for explaining `frontend/` (React 19 + TypeScript + Vite). Read it top to bottom: it follows one piece
of speech from the microphone to the result on the screen.

## 1. The big picture

```
Microphone
  -> AudioWorklet (audio thread)       capture.worklet.ts + resampler.ts   48 kHz stereo -> 16 kHz mono, 32 ms frames
  -> Web Worker                        vad.worker.ts                       Silero VAD (ONNX) -> speech probability per frame
       -> EventDetector                eventDetector.ts                    state machine: when does a speech event start/end?
       -> WAV encoder                  wav.ts                              16 kHz mono 16-bit WAV, validated
  -> main thread (React)               hooks/useExam.ts                    builds metadata, stores, uploads
       -> IndexedDB                    storage/eventStore.ts               every clip is stored locally first
       -> upload queue                 recording/uploadQueue.ts            POST to the backend, retries with back-off
       -> live updates (SSE)           api/sse.ts                          backend pushes status + AI results
  -> screen                            pages/ExamPage.tsx + components/*   status, events, observations, playback
```

Why three threads: audio must never stutter (worklet), the neural network must not freeze the page (worker), and the
UI stays responsive (main thread).

## 2. File by file

| File | What it does | Talking point |
|---|---|---|
| `main.tsx`, `pages/ExamPage.tsx` | App entry; the single exam page (buttons, panels, event list) | Pure layout - all logic is in `useExam` |
| `hooks/useExam.ts` | **The orchestrator.** Start/end exam, open camera + mic, create the session, start the audio pipeline, receive events, save them, upload, apply live updates, diagnostics | Read this first; everything else is called from here |
| `audio/audioPipeline.ts` | Wires mic -> AudioContext -> worklet -> worker; maps the audio clock to wall-clock time | Timestamps come from the audio clock, not `Date.now()` per clip |
| `audio/capture.worklet.ts`, `resampler.ts` | Runs on the audio thread: mixes to mono, band-limited resampling to 16 kHz, sends 512-sample frames | 16 kHz mono is what all the AI models expect |
| `audio/vad.worker.ts`, `sileroVad.ts` | Runs Silero VAD (onnxruntime-web, WASM) on each frame; feeds the detector; encodes WAVs | The model is loaded once per exam |
| `audio/eventDetector.ts` | State machine `IDLE -> SPEECH_DETECTED -> RECORDING -> FINALIZING -> IDLE` with a rolling buffer: 2.5 s pre-roll, ends after 1.5 s silence, merges short pauses, splits long speech at 10 s with 1 s overlap | Only speech is saved - never continuous audio |
| `audio/wav.ts` | Writes and validates the WAV (same rules as the backend) | A bad clip is rejected before upload |
| `recording/eventMetadata.ts` | Builds the upload metadata (event id, times, trigger, SHA-256) for each speech event | Matches the JSON Schema in `docs/contracts` |
| `recording/uploadQueue.ts` | Uploads stored clips; retries network/5xx errors with back-off; same `eventId` => backend de-duplicates | Survives backend outages and page reloads |
| `recording/eventStatus.ts` | Turns upload + backend status into the label on screen ("Uploading audio", "Audio saved (AI processing...)") | Pure functions, unit-tested |
| `storage/eventStore.ts` | IndexedDB: every clip, its WAV (for playback), local video, status, AI result | Local-first: nothing is lost if the network drops |
| `api/client.ts`, `api/sse.ts` | Typed HTTP calls; Server-Sent Events over `fetch` (to send the `Authorization` header), resume with `Last-Event-ID`, polling fallback | Results arrive live without refreshing |
| `video/videoClipRecorder.ts` | Two staggered MediaRecorders keep a video clip around each speech event, stored locally only (never uploaded) | Video is optional context for the candidate's own playback |
| `components/EventList.tsx`, `ObservationList.tsx` | One row per speech event: time, duration, upload/AI status, audio player, number of voices ("Voices: one" / "Voices: 2 different voices"), observations with confidence and evidence | Informational records are plain text; review observations are badges with "not a determination of misconduct" |
| `components/StatusBar.tsx` | Mic/camera/detection state, live level, diagnostics (audio analysed, events detected/saved/uploaded) | Makes capture problems visible |
| `types/contracts.ts` | TypeScript types of the backend contracts (session, event, analysis result 1.1, observations) | Mirrors `docs/contracts/schemas` |
| `config.ts` | Settings from `VITE_*` variables (API URL, VAD thresholds, retention) | No secrets in the frontend |

## 3. One speech event, step by step

1. Candidate speaks -> worklet frames -> worker: Silero says probability > 0.5.
2. `EventDetector` goes to RECORDING, keeps the 2.5 s before the speech from its rolling buffer.
3. 1.5 s of silence -> FINALIZING -> the clip (pre-roll + speech + 0.5 s tail) is cut and encoded as WAV.
4. `useExam` builds the metadata, stores the clip in IndexedDB, and hands it to the upload queue.
5. Upload -> backend validates, stores, queues it on Redis -> AI service analyses it
   (VAD, pyannote speaker turns/overlap, voices compared with each other, rules). No voice sample is used, so no
   speaker is ever identified as the candidate.
6. Backend receives the result -> SSE pushes it -> `useExam.applyRemote` updates IndexedDB and the screen.
7. The event row shows "Audio saved (analysed)", "Speech detected (2.1 s)", "Voices: ..." and any observations (multiple speakers, overlapping speech, background conversation).

## 4. Error handling the candidate sees

| Situation | What happens |
|---|---|
| Camera/mic permission denied | Explained on the page; no session is created |
| No camera | Continues with audio only, clearly marked |
| Mic unplugged / muted by the system | Reported; monitoring stops and can be resumed |
| Backend unreachable | Clip kept locally, "Upload failed - retrying automatically", sent later |
| Page reloaded mid-exam | Session and unsent clips restored from sessionStorage/IndexedDB; "Resume exam" |
| Live updates lost | Reconnects with `Last-Event-ID`; falls back to polling |
| No audio frames for 3 s | Watchdog warning in the status bar |

## 5. Tests

- `npm test` - 41 unit tests (detector, WAV, resampler, metadata vs schema, IndexedDB, upload retry, SSE,
  status wording, observations display, real Silero model on recorded speech).
- `npm run e2e` - real Chrome with a fake microphone against the running services: 27 checks (four utterances ->
  four events, silence -> none, upload retry, permission denial, AI results on the page).
