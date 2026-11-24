# Evaluation plan: dataset, scenarios and metrics

Status: plan only (24 Nov 2026). No detection model beyond Silero VAD has been evaluated, and **no accuracy is
claimed** anywhere in these documents. Results will be added only after the runs described here.

Related: [model-comparison.md](model-comparison.md) · [detection-strategy.md](detection-strategy.md) ·
[integration-design.md](integration-design.md)

## 1. Goals

1. Measure, per observation type, how often the system raises it correctly (recall) and how often it raises it
   wrongly (precision, false positives per candidate-hour) **on audio that looks like real exams**.
2. Choose thresholds and calibrate `confidence` ([detection-strategy.md §6](detection-strategy.md#6-confidence-scores)).
3. Measure latency, CPU, memory and bandwidth per clip and per candidate-hour.
4. Decide go/no-go per detector: a detector that cannot meet its target stays off (its observation type is not shown).

## 2. Dataset

### 2.1 Own recordings (primary)

Public benchmarks do not contain laptop microphones in bedrooms, browser noise suppression or Bluetooth headsets,
so the primary set is recorded by us:

- **Participants:** at least 6 adult volunteers (mixed voices), each with written consent for recording and use
  in this evaluation only; recordings stored outside the repository with access limited to the evaluation team.
- **Devices:** built-in laptop microphone, USB/wired headset, Bluetooth headset (the POC machine uses one), ≥ 1
  external webcam microphone.
- **Rooms:** small quiet room, room with fan/AC, room with a TV/radio, larger reverberant room.
- **Browser settings:** every scenario recorded twice through the real browser pipeline — with the POC defaults
  (echo cancellation, noise suppression, AGC on) and with noise suppression + AGC off.
- **Capture path:** recordings are made through the actual frontend audio module (so browser VAD triggering is part
  of the test), and additionally as continuous 16 kHz WAV for offline replay.

### 2.2 Public data (supplementary, licence check before use)

| Dataset | Use | Licence (to confirm on the dataset page before download) |
|---|---|---|
| LibriSpeech | single-speaker speech for mixing | CC BY 4.0 [A] |
| AMI Meeting Corpus | real overlapping multi-speaker speech | CC BY 4.0 [A] |
| FSD50K | phone, typing, keyboard, door, cough sound events | per-clip Creative Commons licences [A] (some NC) |
| ESC-50 | environmental sounds incl. keyboard typing, clock alarm, door knock | CC BY-NC [A] — internal evaluation only |
| MUSAN | noise, music and speech for background mixing | mixed CC / public domain [A] |

Licence labels in this table are [A]: they must be checked on the official dataset pages, and non-commercial sets
used only for internal evaluation.

### 2.3 Synthetic mixtures (controlled)

A script mixes single-speaker recordings and sound events at controlled levels to produce exact labels:
second voice at −6, −12, −20 dB relative to the candidate; overlap 0 %, 25 %, 50 %; events at known offsets;
fixed random seed. Mixtures supplement, never replace, real recordings (mixing does not reproduce room acoustics).
The POC's TTS fixtures (`tests/fixtures/generate_speech.ps1`) are useful for pipeline tests but **not** for speaker
tests (synthetic voices are unrealistically consistent).

### 2.4 Labels and layout

```
evaluation/                        (outside git for real recordings; synthetic ones may be regenerated)
  manifest.csv                     clip_id, scenario, participant_ids, device, room, browser_processing, split
  audio/<clip_id>.wav              16 kHz mono PCM 16-bit (same format as production clips)
  labels/<clip_id>.rttm            speaker turns (who speaks when; overlap = overlapping turns)
  labels/<clip_id>.events.csv      start, end, class (phone_ring, typing, whisper, tv_speech, ...)
  enrolment/<participant>.wav      10 s enrolment recording per participant and device
```

Labelling by two people independently for a 20 % subset; disagreements resolved and inter-annotator agreement
reported. Splits: **calibration** (thresholds, 40 %) and **test** (60 %, untouched until thresholds are frozen),
separated **by participant** so no voice appears in both.

## 3. Scenarios and what success looks like

Targets are proposals to be agreed with the product owner; they are not results.

| # | Scenario | Recording protocol (per participant × device) | Expected observations | Must NOT raise | Proposed target |
|---|---|---|---|---|---|
| 1 | One person speaking | candidate reads/thinks aloud, 10 × 5–15 s utterances | no observation | any speaker, background or assistance observation | ≤ 1 false speaker-type observation per candidate-hour |
| 2 | Two people taking turns | candidate + helper alternate sentences, 10 dialogues | `MULTIPLE_SPEAKERS_DETECTED` on clips with the helper's voice | – | recall ≥ 0.8 on clips with ≥ 2 s helper speech |
| 3 | Two people speaking simultaneously | both talk over each other for 3–8 s, 10 times | `OVERLAPPING_SPEECH_DETECTED` (+ MULTIPLE_SPEAKERS) | – | recall ≥ 0.7 for ≥ 1 s overlap |
| 4 | Quiet background conversation | two people talk 2–4 m away at normal level while candidate is silent or speaks occasionally | `POSSIBLE_BACKGROUND_CONVERSATION` or `MULTIPLE_SPEAKERS_DETECTED` | POSSIBLE_EXTERNAL_ASSISTANCE (without content rule hit) | recall measured per distance; no fixed target yet (hardest case) |
| 5 | Whispering | candidate and helper whisper (scripted sentences) | `POSSIBLE_WHISPERING` when a clip is produced | – | first measure **whether a clip is triggered at all** (browser VAD), then detection recall |
| 6 | Phone ringing | 5 ringtones (classic bell, melodic, vibration on desk) × 3 volumes | `PHONE_RINGING_DETECTED` (vibration reported separately) | speaker observations | recall ≥ 0.8 for audible ringtones; precision ≥ 0.8 |
| 7 | Keyboard typing | typing for 30 s (laptop keyboard, mechanical keyboard) | `KEYBOARD_TYPING_DETECTED` (info) | speech observations | no speech clip triggered by typing (browser VAD FP); typing recall ≥ 0.8 |
| 8 | Silence | 30 min silent sessions | nothing (except technical status) | everything | 0 observations |
| 9 | Normal environmental noise | fan/AC, traffic, door closing, chair, cough — 30 min sessions without speech | at most `UNUSUAL_SOUND_DETECTED` for door/knock | speaker and phone observations | ≤ 1 unusual-sound observation per 10 min; 0 speaker observations |
| 10 | Speech with background noise | scenario 1 and 2 repeated with fan/TV-music at 10 and 0 dB SNR | as scenarios 1 and 2 | as scenarios 1 and 2 | recall drop ≤ 0.15 vs. clean |

Additionally, **long negative sessions**: ≥ 10 sessions × 30 min of a single candidate behaving normally (reading,
typing, coughing, occasional self-talk) on different devices → the main false-positive-per-hour measurement, because
false alarms are the main cost for proctors.

## 4. Metrics

### 4.1 Detection

Matching rule: a predicted observation matches a labelled event of the same type if their time intervals overlap
(intersection over union ≥ 0.3, or the label lies within a predicted observation for point-like events). Each label
can be matched once.

| Metric | Definition | Reported per |
|---|---|---|
| Precision | matched predictions / all predictions | type × device × browser processing |
| Recall | matched labels / all labels | type × scenario × device |
| **False positives per candidate-hour** | unmatched predictions / hours of audio monitored (incl. long negative sessions) | type |
| False negatives | unmatched labels, listed with clip ids for manual review | type × scenario |
| Trigger recall (browser) | labelled speech/sound events that produced any uploaded clip | scenario |
| Confusion | e.g. TV speech reported as MULTIPLE_SPEAKERS, ringtone reported as music | type |
| DER (diagnostic) | diarization error rate on scenarios 2–3 with pyannote metrics | model |
| Calibration | reliability diagram + expected calibration error of `confidence` | type |

Uncertainty: every rate is reported with a **95 % Wilson confidence interval**; with the minimum sample sizes above
(e.g. 60 positive clips per type) an interval is still roughly ±10 percentage points, which the report must state.
Threshold choice: precision–recall curves on the calibration split; thresholds frozen before the test split is run.

### 4.2 Performance

| Metric | How |
|---|---|
| Latency | speech end → observation stored (timestamps at browser, backend, AI, backend) — median and p95 |
| AI CPU time per clip, per tier | timers in the audio module; median/p95 per clip length |
| Peak memory | process peak working set with all models loaded |
| Throughput | replay N concurrent synthetic sessions until p95 latency > 10 s; report sessions per core |
| Bandwidth | bytes uploaded per candidate-hour (from backend logs of sizes, not content) |
| Browser cost | CPU % of the audio worker on a low-end laptop during a 30 min session |

## 5. Procedure (reproducible)

1. Freeze versions (models, thresholds, code commit) in `evaluation/run-<date>.json`.
2. Replay every clip through the AI audio module (offline harness, no browser) → predictions JSON.
3. Replay a subset through the full stack (browser fake microphone, as in `frontend/scripts/e2e-browser.mjs`) to
   measure browser triggering and end-to-end latency.
4. Score with a script that reads labels + predictions and writes the tables above (Markdown + CSV).
5. Manually review all false negatives and a sample of false positives; record causes.
6. Write results to `docs/audio-research/evaluation-results.md` with the date, machine, versions and intervals.
   Nothing is reported as "works" without this file.

## 6. Exit criteria for this POC

- Each detector has a measured precision/recall/FP-per-hour with intervals on the test split.
- Detectors that miss their target are disabled, and the reason is documented.
- Resource numbers confirm (or replace) the estimates in [integration-design.md](integration-design.md#7-resources-on-the-development-machine).
