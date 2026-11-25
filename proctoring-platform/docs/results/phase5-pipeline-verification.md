# Phase 5 - complete pipeline and failure handling

Date: 24 Nov 2026. All checks below ran against the **running services** (backend, Redis, AI service with Silero VAD +
CAM++ + pyannote, frontend), started with `scripts/run-all.ps1`. No test hooks were added to the product code.

## Pipeline: browser to AI and back (`npm run e2e`, Chrome with a fake microphone)

microphone (fake device, real speech file) -> frontend AudioWorklet + Silero VAD -> 16 kHz WAV clip -> upload ->
backend -> Redis Streams -> AI service (VAD, pyannote segmentation, CAM++, rules) -> results stream -> backend ->
SSE -> frontend.

New checks for this phase (all passed): each of the 4 events arrived in the browser with `diarization.available`,
1 speaker, the informational `SPEECH_DETECTED` record and **no review observation** (single voice); every event shows
the "Speech detected" line on the page.

Result: **26 of 27 checks passed.** The failure is the known, intermittent **local video clip** issue (0 bytes for
event 1; see phase7-baseline.md) - audio, upload and analysis are unaffected. To be fixed in Phase 6 (frontend).

## Failure handling (`ai-service/evaluation/e2e_resilience.py`) - 13/13 checks passed

| # | Scenario | Result |
|---|---|---|
| 1 | AI service stopped, 3 clips uploaded | accepted and `QUEUED`; after restart all 3 `COMPLETED` (19.2 s incl. model loading) |
| 2 | 3 clips queued, the 2nd clip's stored file deleted | clip 2 `FAILED` with `AUDIO_NOT_FOUND`; clips 1 and 3 `COMPLETED` - one failed clip does not stop the others |
| 3 | an already-processed event re-added to the stream (new messageId) | still exactly one completed result; no new observation rows; event unchanged (`updatedAt`) |
| 4 | malformed entry added to the events stream | moved to the dead-letter stream (0 -> 1); the next clip was processed |
| 5 | Redis paused during an upload | `503` with the event stored as `RECEIVED`; after unpausing it was published and `COMPLETED` 1.5 s later |
| 6 | burst of 10 clips (6 s each) with an SSE listener | 10/10 `COMPLETED` in 2.8 s (~211 clips/min); SSE delivered `COMPLETED` for 10/10; one completed result per clip; 0 duplicate observation rows |

Already covered by automated suites and unchanged: upload retry after network failures (browser E2E), transient AI
errors retried then dead-lettered at `AI_MAX_DELIVERIES` and crashed-worker recovery via XAUTOCLAIM (AI integration
tests with a real Redis container), Redis outage handling in the backend (backend integration tests).

## Tooling added

- `scripts/run-all.ps1 -Service <redis|backend|ai|frontend>`: start or stop one service while the others keep
  running (used by scenarios 1 and 2).
