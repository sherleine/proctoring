# Phase 2 - independent pyannote benchmark (not integrated)

Date: 24 Nov 2026. pyannote is **not** used by the service yet. Everything below ran in an isolated virtual
environment (`ai-service/.venv-diarization`, ignored by git); the service's own environment is unchanged.

## Setup

| Item | Value |
|---|---|
| Packages | `pyannote.audio==4.1.1`, `torch==2.15.0+cpu`, `torchaudio==2.11.1+cpu` (same CPU builds as the service), `soundfile==0.14.0` (benchmark only); `pip check` clean |
| Model | `pyannote/speaker-diarization-community-1`, **revision `3533c8cf8e369892e6b79ff1bf80f7b0286a54ee`** (pinned); licence CC-BY-4.0; gated (conditions accepted, token in `ai-service/.env`, never printed) |
| Model cache | `ai-service/models/hf` (ignored by git) |
| Telemetry | pyannote.audio 4.1.1 sends usage telemetry to `https://otel.pyannote.ai` **by default** (model/pipeline init, per-file duration and speaker settings - no audio). Disabled with `PYANNOTE_METRICS_ENABLED=false`, set before import (verified in `pyannote/audio/telemetry/metrics.py`). Must also be set in the service. |
| Input | in memory (`{"waveform", "sample_rate": 16000}`): no file decoding, no FFmpeg needed |
| Machine | i5-13450HX, 15.7 GB RAM (1.4-2 GB free), CPU only, `torch.set_num_threads(4)` |

## Resources (measured)

| Measurement | Value |
|---|---|
| Installation size (isolated venv incl. torch) | **1 222 MB** (install took 63 min on this machine) |
| Model download | **31.3 MB** |
| Import (`import pyannote.audio`) | 4.7-5.0 s, **+395 MB** working set |
| Pipeline load | 0.8-1.0 s, **+135 MB** |
| Peak working set of the benchmark process | **742 MB** (online run), 726 MB (offline run) |
| 10 s clip, one speaker | median **0.86-0.94 s** -> real-time factor **0.085-0.094** |
| 10 s clip, two speakers (turns / overlap) | median 0.84-0.87 s (RTF ~0.085) |
| 10 s silence | 31-37 ms |
| CPU utilisation while processing | **~3.9-4.0 cores** (= the 4 configured threads) |
| GPU memory | not applicable: CPU-only torch build (`torch.cuda.is_available() = False`) |
| Offline | `HF_HUB_OFFLINE=1` + pinned revision: loads and runs with no network access, 0 MB downloaded |

Results: `ai-service/evaluation/results/bench_diarization.json`, `bench_diarization_offline.json`.

## Speaker-count probe on 10 s clips (our maximum event length)

`evaluation/probe_diarization_short_clips.py`, LibriSpeech **calibration-split speakers only** (20 speakers / pairs;
the other 20 speakers are reserved for the final evaluation). "Local" = speakers the pipeline's own segmentation
model sees active for >= 1.0 s in a 10 s window, captured through the pipeline's `hook` argument (pyannote is not
modified). The 1.0 s / 0.5 activation choice is a first setting and still has to be validated.

| Clip (10 s) | Full pipeline output (after clustering) | Segmentation model, local speakers |
|---|---|---|
| one speaker (expected 1) | 20/20 = 1 | 20/20 = 1 |
| A 4.5 s, pause 0.5 s, B 5 s (expected 2) | **1/20 = 2** (same gender 0/12, cross gender 1/8) | **17/20 = 2** |
| A 10 s + B at 3-7 s (expected 2) | 20/20 = 2; overlap found: median 3.0 s of 4.0 s | 18/20 = 2 (2 clips: 3) |

**Conclusion.** On clips of at most 10 s the pipeline's clustering step merges speakers who talk one after the
other (even a man and a woman); it is not usable on its own for MULTIPLE_SPEAKERS on our event clips. Overlap is
detected reliably. The segmentation model inside the same pipeline separates sequential speakers in 85 % of the
probe clips. Proposed integration: speaker turns and overlap from the segmentation model, identity (candidate or
not) from CAM++ per turn against the enrolled voice - to be confirmed on the held-out evaluation speakers.
