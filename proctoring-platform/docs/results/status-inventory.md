# Project status inventory (Phase 1 of the completion plan)

Date: 24 Nov 2026. Inspection only; the single change made is the `.gitignore` fix below. Test numbers are from runs
on 24 Nov 2026 (see [phase7-baseline.md](phase7-baseline.md) and the Stage logs in
[../audio-research/ai-detection-plan.md](../audio-research/ai-detection-plan.md)).

## Completed and verified

| Area | What works | Evidence |
|---|---|---|
| Startup | `scripts/run-all.ps1` start/stop/status, health-gated, per-service logs, only stops its own processes | Phase 7 baseline |
| Browser capture | mic permission, AudioWorklet 16 kHz, Silero VAD in a worker, repeated speech events (pre-roll, pause merge, split + overlap), WAV validation, IndexedDB, upload retry, SSE + polling fallback | frontend 42 tests; browser E2E 22/22; real-mic sessions (VS Code browser) |
| Backend | sessions + token auth, upload validation + idempotency, local audio store, SQLite, Redis Streams outbox, results consumer, SSE, retention | 274 tests (155 unit, 119 integration incl. real Redis) |
| Redis Streams | events/results streams, consumer groups, XAUTOCLAIM retries, DLQs, idempotent receivers, no audio bytes in Redis | integration tests on both sides |
| AI service | Redis worker, WAV validation, Silero VAD, optional Whisper (off), CAM++ voice reference (enrolment) and per-region comparison, observations `NON_CANDIDATE_VOICE` / `MULTIPLE_SPEAKERS` | 104 + 9 real-model tests; CAM++ calibrated/evaluated on LibriSpeech (0/1200 false non-candidate); real-model HTTP->Redis->AI check |
| Contracts | v1 + additive 1.1 (enrolment, speakerReference, speaker stage, observations) | example validator 12/12 |
| Voice sample UI | record/skip/re-record, status, observations on events with time, confidence kind, evidence, playback | frontend tests + E2E regression (enrolment flow not yet in the browser E2E) |

## Incomplete

| Item | State |
|---|---|
| Speaker diarization (pyannote community-1) | not implemented; HF access now works (verified 24 Nov) |
| Overlap, background-conversation, assistance rules | not implemented (need diarization / transcription evidence) |
| Observation names requested in the completion plan (`MULTIPLE_SPEAKERS_DETECTED`, `BACKGROUND_CONVERSATION_DETECTED`, `OVERLAPPING_SPEECH_DETECTED`, `SPEECH_DETECTED`, `NO_SPEECH_DETECTED`) | contract 1.1 uses `MULTIPLE_SPEAKERS`, `NON_CANDIDATE_VOICE`, ...; decision needed (Phase 4) |
| Sound-event capture / classification (phone, typing) | not implemented (out of the current plan's scope) |
| Labelled evaluation beyond LibriSpeech (rooms, TV, real two-person recordings) | not done; needs consented recordings |
| Enrolment flow in the browser E2E test | not covered |
| AI Docker image | does not fetch the CAM++ model (works only via `run-all.ps1`) |
| README / setup docs | outdated (still says speaker detection is not implemented) |

## Broken or at risk

| Item | Detail |
|---|---|
| **Repository contains 330 MB + 2 712 files of LibriSpeech** | `eval-data/` was committed in `a7d4283`. Now ignored for the future; still in the commit and the index. Branch not pushed yet (no upstream). |
| Local video clips | intermittently missing (Phase 7 known issue); audio unaffected |
| MULTIPLE_SPEAKERS without diarization | missed when the candidate's speech is cut by pauses into < 1.5 s pieces (Stage 2 E2E) |
| Root `.env` of the platform | sets `QUEUE_PROVIDER=local-directory` (run-all overrides it) |
| Old prototype at the repository root | `backend/`, `ai_service/`, `frontend/`, `tests/`, `docs/` (FastAPI prototype, Nov 20) are tracked next to `proctoring-platform/` - confusing for a handover |
| Stray files | `proctoring-platform-api.log` (now ignored), `proctoring-platform/frontend/frontend-packages.txt` (untracked, origin unknown) |

## Change made in Phase 1

`.gitignore` (repository root): added `eval-data/` and `*.log`.
