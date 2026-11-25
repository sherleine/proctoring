# Phase 4 - observation rules on diarization + voice comparison

Date: 24 Nov 2026. Measured on **synthetic mixtures of real read speech** (LibriSpeech test-clean, 20 held-out
evaluation-split speakers, 10 s clips); real rooms are expected to be harder.

## Observations

| Observation | Category | Raised when (all thresholds configurable) | Confidence |
|---|---|---|---|
| `SPEECH_DETECTED` / `NO_SPEECH_DETECTED` | informational | every speech event (server VAD) | model-score |
| `NON_CANDIDATE_VOICE` | review | a diarized speaker's non-overlapping speech (>= 3 s) is LIKELY_NON_CANDIDATE vs the enrolled voice | model-score |
| `MULTIPLE_SPEAKERS_DETECTED` | review | once per clip, if any basis holds (all listed in the evidence): (a) candidate voice + a different voice; (b) two diarized speakers whose voices differ (works without a voice sample); (c) overlap >= `OVERLAP_MIN_SECONDS` (1.0 s) | max of the bases (model-score) |
| `OVERLAPPING_SPEECH_DETECTED` | review | overlap regions total >= `OVERLAP_MIN_SECONDS` | rule: 0.5 + overlap/4 s |
| `BACKGROUND_CONVERSATION_DETECTED` | review | a non-main speaker >= `BACKGROUND_LEVEL_DB` (10 dB) quieter than the main speaker; for a voice heard only during overlap the level is **estimated** from the energy it adds (`levelEstimatedFromOverlap: true`) | rule: from the level difference |
| `POSSIBLE_EXTERNAL_ASSISTANCE` | review | >= `ASSISTANCE_MIN_INDICATORS` (2) of: another voice; back-and-forth between main and other speaker (>= 2 changes); an assistance-like phrase ("the answer is", "option B", "question 3"...) in a transcript segment spoken by a NON-main speaker. With `ASSISTANCE_REQUIRE_TRANSCRIPT=true` (default) the phrase is mandatory, so **with transcription off (default) this is never raised** - voice-only evidence is already covered by the observations above. | rule: indicators / 3 |

Speaker roles in `diarization.speakers[].role`: `candidate` only when the voice comparison says LIKELY_CANDIDATE;
the main speaker without a voice sample stays `uncertain` (identity never claimed without evidence); `other`
(different voice), `background` (quieter), `uncertain`. "Model-score" confidences are monotonic transforms of model
scores, **not** calibrated probabilities.

## Held-out evaluation (`evaluation/eval_diarization.py`, 20 clips per type)

| Clip | MULTIPLE | NON_CANDIDATE | OVERLAPPING | BACKGROUND | ASSISTANCE | any review observation |
|---|---|---|---|---|---|---|
| one speaker | **0 %** | 0 % | 0 % | 0 % | 0 % | **0 %** (no false alarm) |
| A, pause, B | 85 % | 85 % | 0 % | 0 % | 0 % | 85 % |
| A split by pauses, then B | 65 % | 65 % | 0 % | 0 % | 0 % | 75 % |
| A + B overlapping (equal level) | **100 %** | 0 % | **100 %** | 20 % (mislabelled) | 0 % | 100 % |
| A + B overlapping, B -10 dB | 75 % | 0 % | 75 % | 30 % | 0 % | 80 % |
| A + B overlapping, B -20 dB | 20 % | 0 % | 20 % | 15 % | 0 % | 20 % |
| A, then quiet B (-10 dB) alone | 80 % | 80 % | 0 % | 40 % | 0 % | 80 % |
| A, then quiet B (-20 dB) alone | 95 % | 90 % | 0 % | **95 %** | 0 % | 95 % |

Diarization: overall DER 0.214 (collar 0) / 0.169 (collar 0.25 s); overlap precision 0.875 / recall 0.914 on the
equal-level overlap clips. Median processing time per 10 s clip: 0.25-0.32 s (VAD + segmentation + CAM++ + rules).

End to end through the running services (HTTP -> Redis -> AI -> HTTP): all checks pass; browser E2E 22/22;
AI tests 128 passed.

## Limitations

- A -10 dB background voice sits on the 10 dB threshold (40 % flagged); the threshold was **not** tuned on this
  held-out data. Quiet voices that only speak over the candidate are often not separated by diarization at all.
- The overlap-energy level estimate is noisy (speech loudness varies): 20 % of equal-level overlaps were labelled
  background - those clips are still reported as overlap + multiple speakers.
- Assistance needs transcription (off by default) and English phrase patterns; transcripts can be wrong.
- Synthetic mixtures only; no real room or two-person recordings yet (Phase 7).
