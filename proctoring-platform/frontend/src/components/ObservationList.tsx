import type { AnalysisResult, Observation, ObservationType } from "../types/contracts";

export const OBSERVATION_LABELS: Record<ObservationType, string> = {
  MULTIPLE_SPEAKERS_DETECTED: "Multiple speakers detected",
  OVERLAPPING_SPEECH_DETECTED: "Overlapping speech detected",
  BACKGROUND_CONVERSATION_DETECTED: "Background conversation detected",
  NON_CANDIDATE_VOICE: "Voice differs from the enrolled candidate",
  SPEECH_DETECTED: "Speech detected",
  NO_SPEECH_DETECTED: "No speech detected",
  // deprecated aliases (older results)
  MULTIPLE_SPEAKERS: "Multiple speakers detected",
  OVERLAPPING_SPEECH: "Overlapping speech detected",
  BACKGROUND_CONVERSATION: "Background conversation detected",
  POSSIBLE_EXTERNAL_ASSISTANCE: "Possible external assistance",
  PHONE_RINGING: "Phone ringing detected",
  KEYBOARD_TYPING: "Keyboard typing detected",
  WHISPERING: "Possible whispering",
  UNUSUAL_SOUND: "Unusual sound detected",
};

const CONFIDENCE_KIND: Record<Observation["confidenceKind"], string> = {
  "model-score": "model score, not a probability",
  "calibrated-probability": "calibrated probability",
  rule: "rule-based",
};

/** Short "key: value" list of the simple values in the evidence (nested objects are left out). */
export function evidenceSummary(evidence: Record<string, unknown>): string {
  return Object.entries(evidence)
    .filter(([key, v]) => key !== "note" && (typeof v === "number" || typeof v === "string" || typeof v === "boolean"))
    .map(([key, v]) => `${key}: ${typeof v === "number" ? Number(v.toFixed(3)) : v}`)
    .join(" · ");
}

const INFORMATIONAL: ReadonlySet<string> = new Set(["SPEECH_DETECTED", "NO_SPEECH_DETECTED"]);

/** informational records are never shown as flags; older results without a category are inferred from the type. */
export function isInformational(o: Observation): boolean {
  return o.category ? o.category === "informational" : INFORMATIONAL.has(o.observationType);
}

function absoluteTime(eventStartedAt: string, offsetSeconds: number): string {
  return new Date(Date.parse(eventStartedAt) + offsetSeconds * 1000).toLocaleTimeString();
}

/** The voice-check line for a speech event (no observation needed to show it). */
export function voiceCheck(result: AnalysisResult | null): string | null {
  const speaker = result?.speaker;
  if (!speaker) return null;
  if (speaker.reference !== "present") return "Voice check: not compared (no accepted voice sample)";
  const decisions = new Set((speaker.comparisons ?? []).map((c) => c.decision));
  if (decisions.size === 0) return "Voice check: no speech long enough to compare";
  if (decisions.has("LIKELY_NON_CANDIDATE")) return "Voice check: a voice differs from the enrolled voice";
  if (decisions.has("UNCERTAIN")) return decisions.has("LIKELY_CANDIDATE") ? "Voice check: candidate voice (some parts uncertain)" : "Voice check: uncertain";
  return "Voice check: candidate voice";
}

export function ObservationList({ result, eventStartedAt }: { result: AnalysisResult | null; eventStartedAt: string }) {
  const all = result?.observations ?? [];
  const info = all.filter(isInformational);
  const observations = all.filter((o) => !isInformational(o));
  const check = voiceCheck(result);
  const infoLine = info.length > 0
    ? <div className="small">{info.map((o) => `${OBSERVATION_LABELS[o.observationType] ?? o.observationType}` +
        (o.observationType === "SPEECH_DETECTED" ? ` (${Number(o.supportingEvidence.speechSeconds ?? 0).toFixed(1)} s)` : "")).join(" · ")}</div>
    : null;
  if (observations.length === 0) return check || infoLine ? <div>{infoLine}{check && <div className="small">{check}</div>}</div> : null;
  return (
    <div className="observations">
      {infoLine}
      {check && <div className="small">{check}</div>}
      <ul>
        {observations.map((o) => (
          <li key={o.observationId} className="observation">
            <span className="badge warn">{OBSERVATION_LABELS[o.observationType] ?? o.observationType}</span>{" "}
            <span className="small">
              {absoluteTime(eventStartedAt, o.startSeconds)} ({o.startSeconds.toFixed(1)}-{o.endSeconds.toFixed(1)} s of the clip,{" "}
              {(o.endSeconds - o.startSeconds).toFixed(1)} s) · confidence {o.confidence.toFixed(2)} ({CONFIDENCE_KIND[o.confidenceKind]})
            </span>
            <div className="small evidence">Evidence: {evidenceSummary(o.supportingEvidence) || "see result"} · {o.modelName}</div>
          </li>
        ))}
      </ul>
      <div className="small">Potential observations for human review - not a determination of misconduct.</div>
    </div>
  );
}
