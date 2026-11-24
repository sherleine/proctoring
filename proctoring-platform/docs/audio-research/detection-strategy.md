# Detection strategy: from audio clips to observations for human review

Status: design only (24 Nov 2026). Nothing here is implemented yet. Model facts and their evidence labels
([V] verified, [M] measured, [E] estimate, [A] assumption) come from [model-comparison.md](model-comparison.md).

**Principle.** Every output is a *potential observation with a confidence score for a proctor to review*. The system
never declares cheating, never scores a candidate, and never treats silence, missing audio or a disabled microphone
as misconduct.

## 1. Two gaps in today's POC that the design must close

1. **Only speech is uploaded.** The browser creates a clip only when Silero VAD detects speech. A phone ringing or
   keyboard typing without speech never leaves the browser, so the AI side could never report
   PHONE_RINGING_DETECTED or KEYBOARD_TYPING_DETECTED. → Add a second, rate-limited **sound trigger** in the browser
   (section 3).
2. **There is no notion of "the candidate's voice".** Without a reference, "two voices" can only be detected when
   both speak in the same clip. → Add a short **voice enrolment** at exam start (section 4).

A third point is a configuration risk rather than a gap: the POC requests browser **noise suppression, echo
cancellation and automatic gain control** by default. These can remove quiet background speech, whispering and
typing before they are recorded, and make levels incomparable. The evaluation ([evaluation-plan.md](evaluation-plan.md))
runs every scenario with processing on and off; the expectation [A] is that the analysis path needs `noiseSuppression`
and `autoGainControl` **off**.

## 2. Pipeline overview

```mermaid
flowchart TD
    subgraph Browser["Browser (POC capture code + new sound trigger)"]
        MIC[Microphone 16 kHz mono] --> VAD[Silero VAD<br/>every 32 ms frame]
        MIC --> NF[Adaptive noise floor<br/>+ onset detector]
        VAD -->|speech event| CLIP1[Speech clip<br/>trigger=vad]
        NF -->|non-speech onset,<br/>rate-limited| CLIP2[Sound clip<br/>trigger=sound]
    end
    CLIP1 --> T1
    CLIP2 --> T1
    subgraph AI["AI application (audio module)"]
        T1[Tier 1 - every clip<br/>validate WAV, Silero VAD,<br/>audio tagger] --> R{speech verified?}
        R -->|no| RULES
        R -->|yes, >= 1 s| T2[Tier 2 - speech clips<br/>speaker embedding<br/>vs candidate enrolment]
        T2 --> S{mismatch, ambiguous,<br/>or speech >= 3 s?}
        S -->|yes| T3[Tier 3 - triggered<br/>segmentation: overlap /<br/>speaker change]
        S -->|no| RULES
        T3 --> RULES
        T2 -.->|only if enabled AND<br/>other-voice evidence| T4[Tier 4 - optional<br/>faster-whisper + text rules]
        T4 --> RULES
        RULES[Rule-based interpretation<br/>-> observations + confidence]
    end
    RULES --> OUT[Observations -> Redis results stream -><br/>.NET backend (SQLite) -> SSE -> reviewer UI]
```

| Tier | Runs on | Model(s) | Cost per 10 s clip | Purpose |
|---|---|---|---|---|
| 0 (browser) | every audio frame | Silero VAD (ONNX, browser) + noise floor | already running in the POC | decide *what* to upload |
| 1 | **every clip** | WAV validation, Silero VAD, audio tagger (EfficientAT mn10_as) | VAD 158 ms [M]; tagger not measured, 0.54 G MACs [V] → expected < 0.3 s [E] | is there speech? which sounds? |
| 2 | clips with ≥ 1 s verified speech | speaker embedding (CAM++) per speech segment ≥ 1 s, cosine vs enrolment | not measured; 7.2 M params [V] → expected < 0.2 s [E] | is it the candidate? |
| 3 | **triggered**: embedding mismatch or ambiguous, or ≥ 3 s speech | pyannote segmentation-3.0 (ONNX) | not measured; sherpa-onnx diarization RTF 0.11–0.30 on another machine [V] → ≤ 3 s per 10 s clip [E] | overlap, speaker changes within the clip |
| 4 | **optional** (`TRANSCRIPTION_ENABLED`), only when tier 2/3 found another voice | faster-whisper base int8 | 1.3 s [M] | words for content rules |

**Not run on every frame or clip:** segmentation, transcription, the full pyannote pipeline (never per clip in this
POC), AST/Cnn14 (offline reference only).

**Expected total** [E, to be measured]: ~0.5 s CPU for a typical clip (tiers 1–2), up to ~2–4 s for the minority
that reaches tiers 3–4. With the proposal's assumption of ~30–60 s of uploaded audio per candidate-hour (≈ 5–15 clips),
that is in the order of 10–60 s of CPU per candidate-hour, i.e. one CPU core could serve on the order of 60–300
concurrent candidates [E]. This must be confirmed by the load test in the evaluation plan.

## 3. Browser triggers

| Trigger | Condition | Clip | Rate limit |
|---|---|---|---|
| `vad` (implemented) | Silero probability ≥ start threshold with hysteresis, min 250 ms | pre-roll 2.5 s + speech + 0.5 s tail, max 10 s, split with 1 s overlap | none (speech is what we monitor) |
| `sound` (new) | No speech, and short-term energy exceeds the adaptive noise floor by ≥ X dB (start: 15 dB [A]) for ≥ 100 ms; the noise floor follows the proposal's EMA with updates only in non-speech frames | 1 s pre-roll + 2 s after onset (3 s total) | ≤ 1 clip per 10 s, ≤ 20 per hour [A]; continuous sounds (typing, fan) are absorbed by the noise floor after a few seconds |

The sound trigger deliberately stays simple (no browser tagger): it only decides that *something changed*; the
classification happens on the server where it can be improved without redeploying the frontend.

## 4. Candidate voice enrolment

- At exam start (after the consent screen), the candidate reads a displayed neutral sentence for ~10 s. The browser
  records it with the same pipeline and uploads it as a clip with `eventType: "enrolment"`.
- The AI module computes an embedding from the enrolment's verified speech (requires ≥ 5 s of speech [A]); it is
  kept in memory/cache per session and the raw enrolment audio follows the same retention rules as other clips.
- If enrolment fails (too short, no speech, several voices detected), the session continues **without** speaker
  verification: tier 2 is skipped, tier 3 runs on every speech clip ≥ 3 s, and observations carry
  `"enrolment": "unavailable"` in the evidence. A failed enrolment is never itself an observation about misconduct.
- During the exam, segments that match the enrolment with high confidence may update a running candidate profile
  (EMA of embeddings) to adapt to the session's microphone and room [A].

Scores: cosine similarity *s* between segment and enrolment embeddings. Thresholds are **calibrated** on the
evaluation set (section 5 of the evaluation plan), not taken from VoxCeleb EER papers. Starting points [A]:
`s ≥ 0.55` same speaker, `s < 0.35` different speaker, in between = ambiguous (→ tier 3).

## 5. Observation types

An observation is created **only** when a detector has enough evidence. Ordinary candidate speech, silence, normal
room noise, a missing microphone or a failed enrolment never create an observation. Per-clip analysis data (speech
segments, tags, similarities) is still stored with the event's analysis result, so a reviewer can see why *no*
observation was raised.

Fields (the requested snake_case names; JSON contracts in this repo use camelCase, mapping shown):

| Field | JSON (camelCase) | Type | Meaning |
|---|---|---|---|
| observation_id | `observationId` | UUID | unique; deterministic from `(eventId, eventType, start)` so redelivery never duplicates |
| session_id | `sessionId` | string | copied from the clip's stored session (backend-generated), never from the browser body |
| candidate_id | `candidateId` | string | from the session |
| event_id | `eventId` | UUID | the audio clip this came from |
| event_type | `eventType` | enum | one of the types below |
| start_timestamp | `startTimestamp` | ISO-8601 UTC | clip `startedAt` + `start` offset |
| end_timestamp | `endTimestamp` | ISO-8601 UTC | clip `startedAt` + `end` offset |
| duration | `durationSeconds` | number | end − start |
| confidence_score | `confidenceScore` | 0–1 | rule-combined score (section 6); a ranking aid for reviewers, **not** a probability of cheating |
| evidence | `evidence` | object | the model outputs and thresholds that produced it |
| model_name | `modelName` | string | the deciding model, e.g. `efficientat-mn10_as`, `campplus-voxceleb` |
| model_version | `modelVersion` | string | weights file / release identifier |
| created_at | `createdAt` | ISO-8601 UTC | when the AI created it |

Reviewer decisions (`reviewStatus`: `pending` → `dismissed` / `relevant`, plus a note) are stored by the backend
next to the observation; the AI never sets them.

| event_type | Raised when (thresholds calibrated in the evaluation) | Default | Key evidence |
|---|---|---|---|
| `MULTIPLE_SPEAKERS_DETECTED` | ≥ 2 speakers in one clip (segmentation), or speech segments whose embeddings differ from the enrolment / from each other | on | speaker count, turns, similarities |
| `OVERLAPPING_SPEECH_DETECTED` | segmentation reports ≥ 0.5 s of simultaneous speakers | on (after evaluation) | overlap intervals |
| `POSSIBLE_BACKGROUND_CONVERSATION` | non-candidate / low-similarity speech **and** a distance cue (speech-to-noise ratio well below the enrolment's, or tagger `Conversation`/`Television`/`Radio`), or speech while the candidate's voice is absent | on (after evaluation) | SNR delta, tagger scores, similarity |
| `POSSIBLE_EXTERNAL_ASSISTANCE` | transcription enabled **and** an additional-speaker observation in the same clip **and** a text rule hit (answer phrases, option letters, question numbers, instructions) | off (needs transcription) | rule matched, speaker turn, short transcript excerpt |
| `POSSIBLE_WHISPERING` | only if the evaluation shows a usable detector (tagger `Whispering` and/or low voiced-frame ratio); otherwise the type stays disabled and the limitation is documented | off until evaluated | tagger score, voiced ratio |
| `PHONE_RINGING_DETECTED` | tagger `Telephone` / `Telephone bell ringing` / `Ringtone` ≥ threshold on ≥ 2 consecutive frames | on | class scores per frame |
| `KEYBOARD_TYPING_DETECTED` | tagger `Typing` / `Computer keyboard` ≥ threshold | **off** (typing is often allowed) | class scores |
| `UNUSUAL_SOUND_DETECTED` | sound-triggered clip whose top tag is on a configurable review list (e.g. `Knock`, `Door`, `Alarm`) | on | top-3 tags |

Technical states (microphone denied/disconnected, processing failed) are shown as **status**, never as observations.

Example (values illustrative, not measured):

```json
{
  "schemaVersion": "2.0",
  "observationId": "5d0f8a2c-1e44-4b7a-9c61-0b1f2e3d4c5b",
  "sessionId": "ses_01JABCDEFGHJKMNPQRSTVWXYZ",
  "candidateId": "cand-123",
  "eventId": "7f3c2a1e-5b6d-4c8e-9f0a-1b2c3d4e5f60",
  "eventType": "MULTIPLE_SPEAKERS_DETECTED",
  "startTimestamp": "2026-11-21T10:08:12.500Z",
  "endTimestamp": "2026-11-21T10:08:15.700Z",
  "durationSeconds": 3.2,
  "confidenceScore": 0.74,
  "evidence": {
    "speakerCount": 2,
    "turns": [ { "start": 2.4, "end": 3.6, "speaker": "S0" }, { "start": 3.9, "end": 5.6, "speaker": "S1" } ],
    "enrolmentSimilarity": { "S0": 0.71, "S1": 0.22 },
    "thresholds": { "sameSpeaker": 0.55, "differentSpeaker": 0.35 }
  },
  "modelName": "campplus-voxceleb",
  "modelVersion": "<weights file + sha256 prefix>",
  "createdAt": "2026-11-21T10:08:18.900Z"
}
```

The existing `analysis-result.v1` has `findings[]` (`type`, `severity`, `start`, `end`, `evidence`). Version 2 adds
`observations[]` with the fields above plus per-detector sections (`sound`, `speaker`); v1 consumers keep working
during the transition.

## 6. Confidence scores

- **Per-model scores** (tagger probability, cosine similarity, segmentation posterior) are kept raw in `evidence`.
- **`confidence`** is a deterministic, documented rule per type, e.g. for `NON_CANDIDATE_SPEECH_DETECTED`:
  `clip01((t_diff − s) / t_diff) × min(1, speechSeconds / 2)` — lower similarity and longer speech → higher value.
- After the evaluation, each type's mapping is **calibrated** on labelled data (isotonic or Platt scaling) so that,
  for example, 0.8 means "in our evaluation set, about 80 % of observations with this score were labelled as the
  described sound/speaker situation". This is a statement about the *acoustic observation*, never about cheating.
- Reviewers see observations ordered by confidence; a session-level summary counts observations, it does not add
  them into a score.

## 7. What the design deliberately does not do

- No automatic misconduct decision, no candidate risk score, no blocking of the exam.
- No speaker identification against people other than the candidate's own enrolment; no gender/age/accent inference.
- No LLM in this POC; no continuous audio upload; no lip-movement analysis (2 FPS is too low, as the proposal notes).
- Silence, a missing microphone or failed enrolment never produce a misconduct-related observation.
