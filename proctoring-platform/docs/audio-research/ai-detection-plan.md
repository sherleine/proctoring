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

### Stage 1 - CAM++ voice embedding, enrolment and comparison (AI side) - done 24 Nov 2026

Code: `ai-service/app/services/speaker_service.py` (sherpa-onnx 2.0.3, model
`3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx`, SHA-256 `98daf9bc...88bb5`, 512-dim, CPU, loaded once).
Evaluation: `ai-service/evaluation/speaker_verification.py` on **LibriSpeech test-clean** (MD5 `32fa31d2...49a9`,
40 real speakers). Results: `ai-service/evaluation/results/speaker_verification.json` (+ raw scores `.npz`).

Measured on the development machine (CPU, 2 threads):

| Item | Value |
|---|---|
| model load | 0.53 s, +68 MB working set |
| embedding time (median) | 16.8 ms per 1.5 s, 28.3 ms per 3 s, 43.9 ms per 5 s segment (~0.009 s per audio second) |
| process with VAD + CAM++ loaded | ~330 MB working set |

**Threshold selection (calibration speakers only, 20 of 40):** `different` = lowest 1st percentile of genuine
scores over all length x condition cells (= **0.3188**, from degraded 1.5 s); `same` = highest 99th percentile of
same-gender impostor scores (= **0.4028**). At 1.5 s genuine and impostor ranges overlapped on the calibration
speakers (genuine p1 0.32-0.35 vs same-gender impostor p99 0.37-0.40); from 3 s they did not (0.44-0.53 vs <= 0.40).
Rule therefore: **LIKELY_NON_CANDIDATE only for regions >= 3 s**; shorter regions are LIKELY_CANDIDATE or UNCERTAIN.
Similarities are raw cosine scores, **not probabilities**.

**Held-out evaluation (the other 20 speakers; thresholds unchanged).** `degraded` = level -12 dB + white noise at
15 dB SNR. Each cell: 200 genuine trials, 3 800 impostor trials.

| Cell | Own voice -> NON_CANDIDATE (false) | Own voice -> UNCERTAIN | Other voice -> CANDIDATE (missed) | Other voice -> NON_CANDIDATE (detected) |
|---|---|---|---|---|
| clean 1.5 s | 0.000 | 0.010 | 0.0045 | 0.000 (abstains: too short) |
| clean 3 s | 0.000 | 0.000 | 0.0084 | 0.963 |
| clean 5 s | 0.000 | 0.000 | 0.0089 | 0.958 |
| degraded 1.5 s | 0.000 | 0.070 | 0.0042 | 0.000 (abstains: too short) |
| degraded 3 s | 0.000 | 0.020 | 0.0079 | 0.961 |
| degraded 5 s | 0.000 | 0.000 | 0.0074 | 0.955 |

0 of 1 200 genuine segments were called non-candidate (95 % upper bound ~0.25 % pooled, ~1.8 % per 200-trial cell).
Same-gender impostors are harder: 0.9-1.9 % called LIKELY_CANDIDATE.

**Enrolment checks:** all 40 single-speaker samples accepted (self-consistency min 0.815, median 0.890); 19 of 20
two-voice samples (half speaker A, half speaker B) were *not* accepted (flagged as more than one voice).
The first version of the enrolment check (similarity to the averaged reference) could not detect a two-voice sample
at all; it was replaced by a pairwise window check before these numbers were produced.

**Limits:** LibriSpeech is clean audiobook speech recorded per speaker; laptop microphones, rooms, Bluetooth codecs
and emotional/whispered speech are harder. These thresholds are a starting point to be re-checked on the project's
own consented recordings.

### Stage 2 - enrolment flow, contracts 1.1, observations end to end - done 24 Nov 2026

| Service | Change |
|---|---|
| Contracts | metadata `eventType`/`trigger.type` += `enrolment`; message `speakerReference`; result 1.1 `enrolment`, `speaker`, `observations[]` (all optional; the 12 existing examples still validate) |
| AI | `processing_pipeline.py`: enrolment events -> CAM++ reference; speech events -> per-region comparison; `detection_rules.py`: `NON_CANDIDATE_VOICE` (region >= 3 s, LIKELY_NON_CANDIDATE) and `MULTIPLE_SPEAKERS` (a LIKELY_CANDIDATE and a LIKELY_NON_CANDIDATE region in one clip); confidence = `model-score`; health reports `speakerModel` |
| Backend | tables `session_voiceprints`, `observations` (migration `AddObservationsAndVoiceprints`); ingestion moves the embedding out of the stored result; publisher adds the accepted reference to speech events of the same session; `GET .../enrolment`, `GET .../observations`; voiceprint retention `VOICEPRINT_RETENTION_HOURS` (24 h) |
| Frontend | voice-sample panel (read a passage, 8-25 s, the sample never becomes a speech event); observations with type, time, duration, confidence kind, evidence and model on each event; voice-check line; wording "speech event received" / "AI processing" / "AI processing failure" |

**Tests (all run 24 Nov 2026):** AI 104 + 9 real-model; backend 274 (155 unit incl. 12 new, 119 integration incl. 2
new over real Redis); frontend 42 (6 new); browser E2E 22/22 against the updated services.

**Real-model end-to-end check** (`ai-service/evaluation/e2e_speaker_flow.py`: HTTP -> Redis -> AI with CAM++ -> HTTP,
LibriSpeech evaluation-split speakers 1188 (enrolled) and 1320, both male):

| Clip | Result | Expected |
|---|---|---|
| voice sample, 15 s audio | accepted, 9.6 s speech, 1.0 s to process; embedding not returned over HTTP | accepted |
| speaker A, 5 s | similarity 0.868 -> LIKELY_CANDIDATE; no observation; 115 ms AI time | no observation |
| speaker B, 5 s | similarity 0.069 -> NON_CANDIDATE_VOICE; 96 ms AI time | NON_CANDIDATE_VOICE |
| A 4 s + pause 1 s + B 4 s | B: 0.138 -> NON_CANDIDATE_VOICE; A's speech was split by its own pauses into 1.44 s and 1.12 s regions (< 1.5 s minimum) -> UNCERTAIN; **no MULTIPLE_SPEAKERS** | MULTIPLE_SPEAKERS + NON_CANDIDATE_VOICE |

**Known limitation found:** without diarization, regions are formed from VAD segments (gaps < 0.5 s merged). Natural
pauses can cut a speaker's turn into pieces shorter than the 1.5 s needed for a comparison, so the candidate's voice
is not confirmed and MULTIPLE_SPEAKERS (which needs positive evidence of both voices) is not raised. The system
abstains rather than guessing. Fix: speaker turns from diarization (Stage 3), evaluated on labelled mixtures - the
merge gap was deliberately **not** tuned to make this single clip pass.
