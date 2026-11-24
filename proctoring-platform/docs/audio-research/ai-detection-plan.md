# AI detection layer - implementation plan

Status: plan + stage log (started 24 Nov 2026). Builds on the standalone architecture
([integration-design.md](integration-design.md)) and the detection strategy
([detection-strategy.md](detection-strategy.md)). Every stage ends with tests and measured numbers recorded in the
"Stage log" below; a detector that fails its evaluation stays disabled.

## 1. What exists and is reused (inspected 24 Nov 2026)

| Area | Existing, reused as-is | Gap |
|---|---|---|
| AI worker (`ai-service/app/services/event_worker.py`) | consumer group, duplicate detection (`already_processed`), ack only after publish, transient retry via XAUTOCLAIM, DLQ at `AI_MAX_DELIVERIES`, `DEAD_LETTERED` result | none |
| AI pipeline (`processing_pipeline.py`) | WAV load via `audioRef` (size + SHA-256 checked), Silero VAD, optional Whisper, schema-validated result | speaker, diarization, sound stages; observations |
| Result contract (`analysis-result.v1.json`) | envelope + validation on both sides; a `diarization` stage placeholder; `findings[]` | `observations[]`, `enrolment`, `speakers`, `sound` (strict `additionalProperties: false` -> additive v1.1) |
| Event contract (`audio-event-metadata.v1.json`) | `eventType: const "speech"`, `trigger.type: const "vad"` | `enrolment` type; candidate voice reference in the Redis message |
| Backend ingestion (`ResultIngestionService`) | validates payload against the bundled schema, idempotent status transitions, stores `result_json` | observations table (queryable), session voiceprint |
| Frontend (`useExam.ts`, `EventList.tsx`) | capture, upload, SSE status, local WAV playback | enrolment step, observation display |
| Storage | WAVs on local disk, SQLite | – |

## 2. Model decisions (verified 24 Nov 2026)

| Purpose | Model / runtime | Licence (verified) | Size | Status |
|---|---|---|---|---|
| Candidate voice embedding | 3D-Speaker **CAM++ English VoxCeleb** (`iic/speech_campplus_sv_en_voxceleb_16k`) as ONNX `3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`, run with **sherpa-onnx 2.0.3** | weights: **Apache License 2.0** (ModelScope API `License` field); sherpa-onnx Apache-2.0; training data VoxCeleb - the VoxCeleb site states no licence (data terms open) | 29.6 MB ONNX; wheels 2.5 + 17.2 MB | Stage 1 |
| Diarization, speaker count, overlap | **pyannote `speaker-diarization-community-1`** (pyannote.audio 4.1.1) | code MIT; model **CC-BY-4.0**, **gated** (accept conditions + HF token) | not stated | **Blocked until the HF token is provided** (Stage 3) |
| Sound events | **EfficientAT `mn10_as`** (`mn10_as_mAP_471.pt`), 527 AudioSet classes, `metadata/class_labels_indices.csv` | repo MIT; weights licence not stated separately; AudioSet ontology CC-BY-4.0 | 19.7 MB | Stage 4 (needs 32 kHz input and a torchvision helper) |
| Sound baseline | YAMNet (Apache-2.0) | – | 3.7 M weights | offline comparison only, separate environment (TensorFlow is not added to the service) |
| Transcription | faster-whisper (existing, optional, off) | MIT | – | only for the assistance rule |

## 3. Stages

| Stage | Deliverable | Verified by |
|---|---|---|
| **1. Voice embedding + enrolment (AI)** | `speaker_service` (CAM++ via sherpa-onnx, loaded once); enrolment quality checks (speech >= 8 s target 10-20 s, clipping, level, self-consistency); per-segment comparison -> `LIKELY_CANDIDATE` / `LIKELY_NON_CANDIDATE` / `UNCERTAIN` | LibriSpeech test-clean: same-speaker vs different-speaker score distributions, thresholds from a calibration split of speakers, FAR/FRR on held-out speakers; CPU time + peak RAM |
| **2. Contracts, backend, enrolment flow** | metadata `eventType: enrolment`; message `speakerReference`; result v1.1 (`enrolment`, `speakers`, `observations`); backend `session_voiceprints` + `observations` tables (migration), ingestion, `GET …/observations`; frontend enrolment step | contract tests; backend unit/integration tests; E2E with a recorded voice |
| **3. Diarization (pyannote community-1)** | `diarization_service` on clips with enough speech; speaker turns, count, overlap; per-turn CAM++ comparison | LibriSpeech mixtures (sequential, overlapping, background at -10/-20 dB): DER, speaker-count accuracy, overlap P/R; CPU time + RAM |
| **4. Sound classification** | `sound_classification_service` (EfficientAT); browser `sound` trigger for non-speech events; phone ring rule; typing rule off by default | label-map check first; labelled phone/typing/noise clips (needs recordings - see blockers); CPU/RAM |
| **5. Rules + observations** | `detection_rules`: MULTIPLE_SPEAKERS, OVERLAPPING_SPEECH, NON_CANDIDATE_VOICE, BACKGROUND_CONVERSATION, POSSIBLE_EXTERNAL_ASSISTANCE (>= 2 indicators), PHONE_RINGING, KEYBOARD_TYPING (off), WHISPERING (off/unsupported) | rule unit tests; no observation on single-speaker and silence sets |
| **6. Frontend display** | observation badges, times, confidence, evidence, playback on the existing exam page | browser test |
| **7. Evaluation report** | `evaluation-results.md` with measured numbers only | – |

## 4. Blockers and dependencies on the user

1. **pyannote community-1 access**: accept the conditions on Hugging Face and put a read token in
   `ai-service/.env` as `HF_TOKEN` (never printed or logged). Until then Stage 3 cannot run.
2. **Evaluation data**: approved source is **LibriSpeech test-clean** (CC BY 4.0, real read speech). It covers
   speaker identity, sequential/overlapping/background mixtures. It does **not** contain phone rings, typing,
   television or room recordings: sound-classification accuracy and scenarios 5-7, 9, 10 cannot be measured
   without additional consented recordings or an approved sound dataset.
3. **Memory**: ~1.4-2 GB free RAM. Models are loaded once; peak RAM is measured per stage before the next model is
   added.

## 5. Stage log

(filled in as stages complete - measured results only)
