import type { AnalysisResult } from "../types/contracts";

const CLEARED_CLASSIFICATIONS: ReadonlySet<string> = new Set(["likely_benign_other"]);

export interface ReviewSummary {
  valid: boolean;
  reason: string;
}

/** Same {valid, reason} as the AI service's POST /analyze (Swagger) - a port of
 *  ai-service/app/services/review_summary.py; keep the two in step. */
export function summarize(result: AnalysisResult): ReviewSummary {
  const flags = (result.observations ?? []).filter((o) => o.category === "review").map((o) => o.observationType as string);
  const aiReview = result.aiReview ?? null;

  if (flags.length === 0) {
    return { valid: true, reason: "No overlap, extra voices or external-assistance indicators were detected." };
  }

  const counts = new Map<string, number>();
  for (const flag of flags) counts.set(flag, (counts.get(flag) ?? 0) + 1);
  const flaggedBy = "Flagged by Tier 1: " +
    [...counts].map(([f, n]) => (n > 1 ? `${f} (x${n})` : f)).join(", ") + ".";

  if (aiReview?.attempted && aiReview.modelValid) {
    const rationale = aiReview.rationale ?? "";
    if (aiReview.classification && CLEARED_CLASSIFICATIONS.has(aiReview.classification)) {
      return { valid: true, reason: `${flaggedBy} AI review cleared it: ${rationale}`.trim() };
    }
    return { valid: false, reason: `${flaggedBy} AI review: ${rationale}`.trim() };
  }

  if (aiReview?.attempted) {
    return { valid: false, reason: `${flaggedBy} AI review did not complete, so the flag stands unconfirmed - needs a human look.` };
  }

  return { valid: false, reason: `${flaggedBy} Tier 2 review was not run (${aiReview?.reason ?? "unknown reason"}).` };
}

export function ObservationList({ result }: { result: AnalysisResult | null; eventStartedAt?: string }) {
  if (!result || result.status !== "completed") return null;
  const { valid, reason } = summarize(result);
  return (
    <div className="observations">
      <span className={`badge ${valid ? "ok" : "warn"}`}>{valid ? "Valid" : "Invalid"}</span>{" "}
      <span className="small">{reason}</span>
    </div>
  );
}
