# Phase 3 - speaker diarization integrated (pyannote segmentation + CAM++ identity)

Date: 24 Nov 2026. Measured results only. Evaluation data: **synthetic mixtures of real read speech**
(LibriSpeech test-clean, held-out evaluation-split speakers) - no room acoustics, no laptop microphones; real-room
results are expected to be worse.

## What was built

| Piece | Detail |
|---|---|
| `app/services/diarization_service.py` | loads `pyannote/speaker-diarization-community-1` once (revision `3533c8cf...`, local cache `models/hf`, works offline), uses its **segmentation step** (`get_segmentations`) - not the clustering step, which merged sequential speakers on 10 s clips in the Phase 2 probe. Output: anonymous speakers `S1, S2...` (order of first appearance, never assumed to be the candidate), turns, overlap regions, per-speaker speech, non-overlapping speech and level. Pause handling: gaps < 0.5 s inside a speaker's speech are closed; pieces < 0.3 s dropped; a speaker needs >= 1.0 s; overlap needs >= 0.5 s. All configurable (`DIARIZATION_*`). |
| `processing_pipeline.py` | diarization runs only when the clip has >= 1.0 s of verified speech; CAM++ (unchanged) compares **each diarized speaker's non-overlapping speech** with the enrolled voice (one comparison per speaker instead of per pause-separated fragment). A speaker heard only during overlap is UNCERTAIN. Several diarized speakers that all match the candidate count as one voice (`distinctVoices`) - guard against over-splitting. Falls back to the previous region comparison if diarization is disabled. |
| Result | `diarization` stage filled: `speakerCount`, `turns`, `overlaps`, `speakers[{speaker, speechSeconds, exclusiveSeconds, rmsDbfs, voice}]`, `distinctVoices` (additive, contract 1.1) |
| Privacy | pyannote telemetry (on by default, to `otel.pyannote.ai`) forced off in `app/__init__.py` before any import; token only in `ai-service/.env` (ignored), dropped from memory after loading |
| Health | `/health/ready` reports `diarizationModel` |

Observation rules are unchanged in this phase (MULTIPLE_SPEAKERS_DETECTED / NON_CANDIDATE_VOICE now use the
per-speaker comparisons). Overlap, background-conversation and assistance observations are Phase 4.

## Held-out evaluation (`evaluation/eval_diarization.py`)

20 evaluation-split speakers, each enrolled from 15 s of their first chapter, 5 clip types x 20 = 100 clips (10 s),
processed by the service's own pipeline code. Ground truth = Silero VAD on each source piece.

| Clip | Diarized speaker count correct | Distinct voices correct | MULTIPLE_SPEAKERS_DETECTED raised | Other |
|---|---|---|---|---|
| one speaker | 95 % (1 clip: 2) | **100 %** | **0 %** (no false alarm) | DER 0.127 |
| A 4.5 s, pause, B 5 s | 85 % | 85 % | **85 %** | DER 0.163 |
| A in two pieces split by pauses, then B | 70 % | 55 % | **55 %** (was 0 % before diarization) | DER 0.250 |
| A + B overlapping 4 s | 95 % | - (B only in overlap) | 0 % (overlap rule = Phase 4) | **overlap precision 0.875, recall 0.914**; DER 0.135 |
| A + quiet B (-20 dB) | **20 %** | 0 % | 0 % | overlap recall 0.088; DER 0.403 |

Overall DER: **0.227** (collar 0, overlap scored), **0.182** (collar 0.25 s).
Processing time per 10 s clip (whole pipeline: VAD + segmentation + CAM++ + rules): median **0.25-0.34 s**.

## End to end through the running services (`evaluation/e2e_speaker_flow.py`)

HTTP -> backend -> Redis -> AI (VAD + pyannote + CAM++) -> backend -> HTTP, evaluation speakers 1188 / 1320:
all checks passed, including **"A then B in one clip" -> MULTIPLE_SPEAKERS_DETECTED + NON_CANDIDATE_VOICE**, which
failed before diarization. AI time 166-245 ms per clip, 0.5 s end to end. Browser E2E: 22/22.

## Resources

| Item | Value |
|---|---|
| pyannote.audio 4.1.1 added to the service venv | **+444 MB** (2 016 -> 2 460 MB), install 10 min, `pip check` clean, existing pins unchanged |
| Model files | 31.3 MB |
| AI service working set with VAD + CAM++ + pyannote loaded | **722 MB** (was ~330 MB) |
| Free RAM on the development machine with all services running | **0.4 GB** - tight; see limitations |

## Limitations found

- **Quiet background speech (-20 dB) is mostly not detected** (20 % of clips). Louder background levels were not
  measured yet (Phase 7 level sweep).
- **Pause-split candidate speech**: 55 %; the candidate's short pieces (2.6 s total in the test) are sometimes not
  enough for a confident comparison.
- **A speaker heard only during overlap cannot be identified** (UNCERTAIN), by design.
- Synthetic mixtures only; no real two-person room recordings yet.
- Memory: the AI service now needs ~0.7 GB; the machine had 0.4 GB free with everything running.
