"""Turns detector outputs into neutral, evidence-backed observations for human review.

Rules (contract 1.1 observation types); each needs positive evidence, nothing is inferred from silence:

  NON_CANDIDATE_VOICE          a speech region of >= SPEAKER_MIN_NON_CANDIDATE_SECONDS whose voice comparison is
                               LIKELY_NON_CANDIDATE (similarity at or below the calibrated `different` threshold).
  MULTIPLE_SPEAKERS_DETECTED   the same clip contains a LIKELY_CANDIDATE region AND a LIKELY_NON_CANDIDATE region,
                               i.e. two distinct voices are supported by the voice comparison.
  SPEECH_DETECTED /            informational records (category "informational") for every speech event: whether
  NO_SPEECH_DETECTED           the server-side VAD confirmed speech. Never review flags.

Not produced here (yet): OVERLAPPING_SPEECH, BACKGROUND_CONVERSATION (need diarization), POSSIBLE_EXTERNAL_ASSISTANCE
(needs >= 2 indicators), PHONE_RINGING / KEYBOARD_TYPING / UNUSUAL_SOUND (need the sound classifier),
WHISPERING (unsupported). Ordinary speech - candidate or uncertain - never creates an observation.

Confidence: `model-score` = a monotonic transform of the cosine similarity relative to the calibrated
thresholds and the calibration medians (0.5 at the decision threshold, 1.0 at the median of the opposite class).
It ranks observations for reviewers; it is NOT a probability of anything.
"""
from __future__ import annotations

import uuid

import numpy as np
from dataclasses import dataclass

from app.services.speaker_service import LIKELY_CANDIDATE, LIKELY_NON_CANDIDATE, Comparison
from app.utils.time_utils import utc_now_iso

NAMESPACE = uuid.UUID("6f1f3f0e-5b8a-4c35-9a59-2d3c0b9e7a11")


@dataclass(frozen=True)
class ScoreScale:
    """Calibration reference points (LibriSpeech calibration split, Stage 1)."""
    same: float
    different: float
    genuine_median: float = 0.6936
    impostor_median: float = 0.1136

    def non_candidate_confidence(self, similarity: float) -> float:
        span = max(self.different - self.impostor_median, 1e-6)
        return _clip(0.5 + 0.5 * (self.different - similarity) / span)

    def candidate_confidence(self, similarity: float) -> float:
        span = max(self.genuine_median - self.same, 1e-6)
        return _clip(0.5 + 0.5 * (similarity - self.same) / span)


def observation_id(event_id: str, kind: str, start: float) -> str:
    return str(uuid.uuid5(NAMESPACE, f"{event_id}|{kind}|{start:.3f}"))


INFORMATIONAL = {"SPEECH_DETECTED", "NO_SPEECH_DETECTED"}


def build(kind: str, session_id: str, event_id: str, start: float, end: float, confidence: float,
          evidence: dict, model_name: str, model_version: str, confidence_kind: str = "model-score") -> dict:
    return {
        "observationId": observation_id(event_id, kind, start),
        "sessionId": session_id, "eventId": event_id, "observationType": kind,
        "startSeconds": round(start, 3), "endSeconds": round(end, 3),
        "confidence": round(confidence, 3), "confidenceKind": confidence_kind,
        "supportingEvidence": evidence, "modelName": model_name, "modelVersion": model_version,
        "createdAt": utc_now_iso(), "category": "informational" if kind in INFORMATIONAL else "review",
    }


def speech_observation(session_id: str, event_id: str, vad, duration_seconds: float, model_name: str,
                       model_version: str) -> dict:
    """SPEECH_DETECTED / NO_SPEECH_DETECTED (informational). Confidence: the server VAD's speech probability
    (mean inside the speech segments) or, for no speech, 1 - its highest window probability. Model scores, not
    calibrated probabilities."""
    if vad.verified:
        start, end = vad.segments[0][0], vad.segments[-1][1]
        confidence = vad.mean_speech_probability if vad.mean_speech_probability is not None else vad.max_probability
        return build("SPEECH_DETECTED", session_id, event_id, start, end, confidence,
                     {"speechSeconds": vad.speech_seconds, "speechRatio": vad.speech_ratio,
                      "segments": len(vad.segments), "threshold": vad.threshold,
                      "note": "Informational: speech is present. Not a concern by itself."},
                     model_name, model_version)
    return build("NO_SPEECH_DETECTED", session_id, event_id, 0.0, round(duration_seconds, 3),
                 1.0 - vad.max_probability,
                 {"maxSpeechProbability": vad.max_probability, "threshold": vad.threshold,
                  "note": "Informational: the server did not confirm speech (e.g. a cough or noise). Not a concern."},
                 model_name, model_version)


def speaker_observations(session_id: str, event_id: str, comparisons: list[Comparison], scale: ScoreScale,
                         model_name: str, model_version: str) -> list[dict]:
    out: list[dict] = []
    others = [c for c in comparisons if c.decision == LIKELY_NON_CANDIDATE and c.similarity is not None]
    candidate = [c for c in comparisons if c.decision == LIKELY_CANDIDATE and c.similarity is not None]
    thresholds = {"same": scale.same, "different": scale.different}

    for c in others:
        out.append(build("NON_CANDIDATE_VOICE", session_id, event_id, c.start, c.end,
                         scale.non_candidate_confidence(c.similarity),
                         {"similarityToCandidate": round(c.similarity, 4), "decision": c.decision,
                          "thresholds": thresholds, "regionSeconds": round(c.end - c.start, 2),
                          "note": "Voice differs from the enrolled candidate voice. Not a determination of misconduct."},
                         model_name, model_version))

    if others and candidate:
        best_other = min(others, key=lambda c: c.similarity)
        best_cand = max(candidate, key=lambda c: c.similarity)
        start = min(best_other.start, best_cand.start)
        end = max(best_other.end, best_cand.end)
        confidence = min(scale.non_candidate_confidence(best_other.similarity),
                         scale.candidate_confidence(best_cand.similarity))
        out.append(build("MULTIPLE_SPEAKERS_DETECTED", session_id, event_id, start, end, confidence,
                         {"basis": "voice comparison", "candidateRegions": len(candidate),
                          "nonCandidateRegions": len(others),
                          "candidateRegion": best_cand.to_dict(), "nonCandidateRegion": best_other.to_dict(),
                          "thresholds": thresholds,
                          "note": "Two distinct voices in one clip. A second voice alone is not evidence of misconduct."},
                         model_name, model_version))
    return out


def _clip(x: float) -> float:
    return max(0.0, min(1.0, x))


# ====================================================================== Phase 4: clip-level rules on diarization

import re  # noqa: E402
from dataclasses import field  # noqa: E402


@dataclass(frozen=True)
class RulesConfig:
    """Thresholds of the clip-level rules (settings OVERLAP_MIN_SECONDS, BACKGROUND_LEVEL_DB, ASSISTANCE_*)."""
    overlap_min_seconds: float = 1.0       # total overlap needed for OVERLAPPING_SPEECH_DETECTED / basis (c)
    multiple_from_overlap: bool = True     # overlap alone supports MULTIPLE_SPEAKERS_DETECTED (two people at once)
    background_level_db: float = 10.0      # a non-main speaker this much quieter than the main speaker = background
    assistance_min_indicators: int = 2
    assistance_require_transcript: bool = True  # voice-only evidence never becomes "assistance"


# English phrases that suggest someone is giving answers or instructions. Content rules are only applied when the
# optional transcription is enabled; a match alone never creates an observation.
ASSISTANCE_PATTERNS = [
    r"\bthe answer (is|was)\b", r"\b(correct|right) answer\b", r"\b(option|answer|choice) [a-e]\b",
    r"\b(choose|pick|select|tick|mark) [a-e]\b", r"\bquestion (number )?\d+\b", r"\bnumber \d+ is\b",
    r"\bwrite (down|this)\b", r"\btype (in|this)\b", r"\bit'?s [a-e]\b",
]


@dataclass
class SpeakerView:
    """What the rules know about one diarized speaker."""
    label: str
    speech_seconds: float
    exclusive_seconds: float
    rms_dbfs: float | None
    turns: list[tuple[float, float]]
    voice: str = "UNCERTAIN"              # LIKELY_CANDIDATE / LIKELY_NON_CANDIDATE / UNCERTAIN (needs a voice sample)
    similarity: float | None = None       # to the enrolled voice
    role: str = "uncertain"               # candidate / other / background / uncertain
    main: bool = False                    # the clip's main speaker (the candidate, or the dominant voice without one)
    level_estimated: bool = False         # rms_dbfs estimated from the energy increase during overlap


def estimate_overlap_level(main_dbfs: float | None, overlap_dbfs: float | None) -> float | None:
    """Level of a second voice heard ONLY while the main speaker talks, from the energy it adds: if the combined level
    is D dB above the main speaker's own level, the second voice is at main + 10*log10(10^(D/10) - 1).
    Equal voices add ~3 dB; a voice 10 dB quieter adds ~0.4 dB. Speech loudness varies over time, so this is an
    estimate (marked as such in the evidence), clamped at 30 dB below the main speaker."""
    if main_dbfs is None or overlap_dbfs is None:
        return None
    gain = 10 ** (max(overlap_dbfs - main_dbfs, 0.0) / 10) - 1
    return main_dbfs + max(10 * np.log10(max(gain, 1e-3)), -30.0)


def assign_roles(views: list[SpeakerView], pair_similarity: dict[tuple[str, str], float], scale: ScoreScale,
                 cfg: RulesConfig, overlap_dbfs: float | None = None) -> SpeakerView | None:
    """Sets main/role on each speaker and returns the main speaker. Identity is only claimed with evidence:
    'candidate' requires LIKELY_CANDIDATE from the voice comparison. Without a voice sample the main speaker is the
    one with the most non-overlapping speech and is explicitly NOT called the candidate (role 'uncertain')."""
    if not views:
        return None
    candidates = [v for v in views if v.voice == LIKELY_CANDIDATE]
    pool = candidates if candidates else views
    main = max(pool, key=lambda v: (v.exclusive_seconds, v.speech_seconds))
    main.main = True
    for v in views:  # speakers heard only during overlap: estimate their level from the added energy
        if v is not main and v.rms_dbfs is None and v.exclusive_seconds < 0.5:
            v.rms_dbfs = estimate_overlap_level(main.rms_dbfs, overlap_dbfs)
            v.level_estimated = v.rms_dbfs is not None
    for v in views:
        if v.voice == LIKELY_CANDIDATE:
            v.role = "candidate"
            continue
        if v is main:
            v.role = "uncertain"  # main voice, identity unknown (no voice sample or undecided comparison)
            continue
        sim_to_main = _pair(pair_similarity, v.label, main.label)
        distinct = v.voice == LIKELY_NON_CANDIDATE or (sim_to_main is not None and sim_to_main <= scale.different)
        same_as_main = sim_to_main is not None and sim_to_main >= scale.same
        quieter = (v.rms_dbfs is not None and main.rms_dbfs is not None
                   and main.rms_dbfs - v.rms_dbfs >= cfg.background_level_db)
        if same_as_main and v.voice != LIKELY_NON_CANDIDATE:
            v.role = main.role if main.role == "candidate" else "uncertain"  # the same voice as the main speaker
        elif quieter:
            v.role = "background"
        elif distinct:
            v.role = "other"
        else:
            v.role = "uncertain"
    return main


def clip_observations(session_id: str, event_id: str, views: list[SpeakerView], main: SpeakerView | None,
                      overlaps: list[tuple[float, float]], pair_similarity: dict[tuple[str, str], float],
                      transcript_segments: list | None, scale: ScoreScale, cfg: RulesConfig,
                      reference_present: bool, model_name: str, model_version: str) -> list[dict]:
    """MULTIPLE_SPEAKERS_DETECTED, OVERLAPPING_SPEECH_DETECTED, BACKGROUND_CONVERSATION_DETECTED,
    NON_CANDIDATE_VOICE and POSSIBLE_EXTERNAL_ASSISTANCE for one diarized clip (each at most once per subject)."""
    out: list[dict] = []
    thresholds = {"same": scale.same, "different": scale.different, "overlapMinSeconds": cfg.overlap_min_seconds,
                  "backgroundLevelDb": cfg.background_level_db}
    identity_note = None if reference_present else "No accepted voice sample: speakers are anonymous, identity unknown."
    overlap_total = round(sum(e - s for s, e in overlaps), 3)

    # NON_CANDIDATE_VOICE: per speaker whose voice comparison is LIKELY_NON_CANDIDATE (>= 3 s, see speaker_service)
    for v in views:
        if v.voice == LIKELY_NON_CANDIDATE and v.similarity is not None:
            out.append(build("NON_CANDIDATE_VOICE", session_id, event_id, v.turns[0][0], v.turns[-1][1],
                             scale.non_candidate_confidence(v.similarity),
                             {"speaker": v.label, "similarityToCandidate": round(v.similarity, 4),
                              "exclusiveSeconds": round(v.exclusive_seconds, 2), "decision": v.voice,
                              "thresholds": thresholds,
                              "note": "Voice differs from the enrolled candidate voice. Not a determination of misconduct."},
                             model_name, model_version))

    # MULTIPLE_SPEAKERS_DETECTED: one observation, all supporting bases listed
    bases, confidences = [], []
    cand = [v for v in views if v.voice == LIKELY_CANDIDATE and v.similarity is not None]
    others = [v for v in views if v.voice == LIKELY_NON_CANDIDATE and v.similarity is not None]
    if cand and others:
        c = min(scale.non_candidate_confidence(min(o.similarity for o in others)),
                scale.candidate_confidence(max(x.similarity for x in cand)))
        bases.append({"basis": "candidate voice and a different voice", "confidence": round(c, 3)})
        confidences.append(c)
    distinct_pairs = []
    for (a, b), sim in pair_similarity.items():
        va, vb = _view(views, a), _view(views, b)
        if va and vb and sim <= scale.different and not (va.voice == LIKELY_CANDIDATE and vb.voice == LIKELY_CANDIDATE):
            distinct_pairs.append({"speakers": [a, b], "similarity": round(sim, 4)})
    if distinct_pairs:
        c = scale.non_candidate_confidence(min(p["similarity"] for p in distinct_pairs))
        bases.append({"basis": "two diarized speakers with clearly different voices", "pairs": distinct_pairs,
                      "confidence": round(c, 3)})
        confidences.append(c)
    if cfg.multiple_from_overlap and overlap_total >= cfg.overlap_min_seconds:
        c = _overlap_confidence(overlap_total)
        bases.append({"basis": "overlapping speech (two people speaking at once)", "overlapSeconds": overlap_total,
                      "confidence": round(c, 3)})
        confidences.append(c)
    if bases:
        spans = [t for v in views for t in v.turns]
        out.append(build("MULTIPLE_SPEAKERS_DETECTED", session_id, event_id, min(s for s, _ in spans),
                         max(e for _, e in spans), max(confidences),
                         {"speakers": [_describe(v) for v in views], "bases": bases, "thresholds": thresholds,
                          "identity": identity_note,
                          "note": "More than one voice in this clip. A second voice alone is not evidence of misconduct."},
                         model_name, model_version))

    # OVERLAPPING_SPEECH_DETECTED
    if overlap_total >= cfg.overlap_min_seconds:
        out.append(build("OVERLAPPING_SPEECH_DETECTED", session_id, event_id, overlaps[0][0], overlaps[-1][1],
                         _overlap_confidence(overlap_total),
                         {"overlapSeconds": overlap_total, "regions": [[round(s, 2), round(e, 2)] for s, e in overlaps],
                          "thresholds": thresholds, "note": "Two or more people spoke at the same time."},
                         model_name, model_version, confidence_kind="rule"))

    # BACKGROUND_CONVERSATION_DETECTED: per quieter non-main speaker
    for v in views:
        if v.role != "background" or main is None:
            continue
        diff = main.rms_dbfs - v.rms_dbfs
        out.append(build("BACKGROUND_CONVERSATION_DETECTED", session_id, event_id, v.turns[0][0], v.turns[-1][1],
                         _clip(0.5 + 0.5 * (diff - cfg.background_level_db) / 20.0),
                         {"speaker": v.label, "levelBelowMainSpeakerDb": round(diff, 1),
                          "levelEstimatedFromOverlap": v.level_estimated,
                          "mainSpeaker": main.label, "mainSpeakerRole": main.role, "voice": v.voice,
                          "speechSeconds": round(v.speech_seconds, 2), "thresholds": thresholds,
                          "identity": identity_note,
                          "note": "A quieter second voice, e.g. people talking in the background. Not a determination of misconduct."},
                         model_name, model_version, confidence_kind="rule"))

    # POSSIBLE_EXTERNAL_ASSISTANCE: >= N independent indicators, one of them from the content when required
    indicators = []
    non_main = [v for v in views if not v.main and v.role in ("other", "background")]
    if others or non_main or any(o["observationType"] == "MULTIPLE_SPEAKERS_DETECTED" for o in out):
        indicators.append({"indicator": "another voice", "speakers": sorted({v.label for v in others + non_main})})
    changes = _speaker_changes(views, main)
    if changes >= 2:
        indicators.append({"indicator": "back-and-forth with the main speaker", "speakerChanges": changes})
    content = _content_match(transcript_segments, [v for v in views if not v.main])
    if content:
        indicators.append({"indicator": "assistance-like phrase", **content})
    if len(indicators) >= cfg.assistance_min_indicators and (content or not cfg.assistance_require_transcript):
        out.append(build("POSSIBLE_EXTERNAL_ASSISTANCE", session_id, event_id,
                         min(t[0] for v in views for t in v.turns), max(t[1] for v in views for t in v.turns),
                         _clip(len(indicators) / 3.0),
                         {"indicators": indicators, "thresholds": {"minIndicators": cfg.assistance_min_indicators,
                                                                   "requireTranscript": cfg.assistance_require_transcript},
                          "identity": identity_note,
                          "note": "Several indicators together suggest someone may be helping. For human review only."},
                         model_name, model_version, confidence_kind="rule"))
    return out


def _overlap_confidence(seconds: float) -> float:
    return _clip(0.5 + seconds / 4.0)


def _pair(pairs: dict[tuple[str, str], float], a: str, b: str) -> float | None:
    return pairs.get((a, b), pairs.get((b, a)))


def _view(views: list[SpeakerView], label: str) -> SpeakerView | None:
    return next((v for v in views if v.label == label), None)


def _describe(v: SpeakerView) -> dict:
    return {"speaker": v.label, "role": v.role, "voice": v.voice, "main": v.main,
            "speechSeconds": round(v.speech_seconds, 2),
            "rmsDbfs": None if v.rms_dbfs is None else round(v.rms_dbfs, 1)}


def _speaker_changes(views: list[SpeakerView], main: SpeakerView | None) -> int:
    """Switches between the main speaker and other speakers along the clip (A-B-A = 2)."""
    if main is None:
        return 0
    timeline = sorted((s, "main" if v.main else "other") for v in views for s, _ in v.turns)
    changes, last = 0, None
    for _, who in timeline:
        if last is not None and who != last:
            changes += 1
        last = who
    return changes


def _content_match(segments: list | None, non_main: list[SpeakerView]) -> dict | None:
    """A transcript segment with an assistance-like phrase that overlaps a non-main speaker's turn."""
    if not segments:
        return None
    for seg in segments:
        text = (getattr(seg, "text", "") or "").lower()
        for pattern in ASSISTANCE_PATTERNS:
            m = re.search(pattern, text)
            if not m:
                continue
            speakers = [v.label for v in non_main
                        if any(min(e, seg.end) - max(s, seg.start) > 0 for s, e in v.turns)]
            if speakers:
                return {"phrase": m.group(0)[:60], "segment": [round(seg.start, 2), round(seg.end, 2)], "speakers": speakers}
    return None
