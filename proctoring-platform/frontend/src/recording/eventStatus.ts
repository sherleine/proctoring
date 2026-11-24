// What an event's state means for the candidate screen. Pure functions (unit-tested).
// Wording is deliberately neutral: results are observations for human review, never verdicts.
import type { EventStatusResponse } from "../types/contracts";
import type { LocalEvent } from "../storage/eventStore";

export type Stage = "uploading" | "retry-pending" | "upload-failed" | "saved" | "saved-analysis-failed";

export interface StageInfo {
  stage: Stage;
  label: string;
  /** Optional server-side analysis state (the audio is what matters; analysis is informational). */
  analysis?: string;
  detail?: string;
  tone: "neutral" | "busy" | "ok" | "warn" | "error";
}

/** After this long without analysis the processing service is probably not running (audio is saved anyway). */
export const AI_SLOW_AFTER_MS = 60_000;

export function describe(e: LocalEvent, nowMs: number = Date.now()): StageInfo {
  if (e.upload === "pending" || e.upload === "uploading")
    return { stage: "uploading", label: "Uploading audio", tone: "busy" };
  if (e.upload === "retry-pending")
    return { stage: "retry-pending", label: "Upload failed - retrying automatically", detail: `${e.lastError ?? "Backend not reachable"} (attempt ${e.uploadAttempts})`, tone: "warn" };
  if (e.upload === "failed")
    return { stage: "upload-failed", label: "Upload failed - retry required", detail: e.lastError ?? undefined, tone: "error" };

  // Uploaded: the backend has stored the WAV. Analysis (optional) is shown as secondary information.
  switch (e.status) {
    case "FAILED":
    case "DEAD_LETTERED":
      return { stage: "saved-analysis-failed", label: "Audio saved", analysis: "server analysis failed",
        detail: e.backendError ? `${e.backendError.code}: ${e.backendError.message}` : undefined, tone: "warn" };
    case "COMPLETED":
      return { stage: "saved", label: "Audio saved", tone: "ok",
        analysis: e.result?.speech?.verified === false ? "analysed - server found no clear speech" : "analysed" };
    case "PROCESSING":
      return { stage: "saved", label: "Audio saved", analysis: "analysing…", tone: "ok" };
    case "RECEIVED":
      return { stage: "saved", label: "Audio saved", analysis: "waiting for the processing queue", tone: "ok" };
    default: {
      const waited = e.statusUpdatedAt ? nowMs - Date.parse(e.statusUpdatedAt) : 0;
      return { stage: "saved", label: "Audio saved", tone: "ok",
        analysis: waited > AI_SLOW_AFTER_MS ? "waiting for analysis (processing service may be off)" : "waiting for analysis" };
    }
  }
}

/**
 * The change to apply to a local event for a backend status message, or null when the message is older
 * than what we already have (SSE and polling can deliver the same state twice or out of order).
 */
export function backendChange(local: LocalEvent, remote: EventStatusResponse): Partial<LocalEvent> | null {
  if (local.statusUpdatedAt && Date.parse(remote.updatedAt) < Date.parse(local.statusUpdatedAt)) return null;
  if (local.status === remote.status && local.statusUpdatedAt === remote.updatedAt && (local.result !== null) === (remote.result !== null))
    return null;
  return {
    status: remote.status,
    statusUpdatedAt: remote.updatedAt,
    result: remote.result,
    backendError: remote.error,
    // A status from the backend proves the upload arrived (e.g. the tab closed during the upload response).
    ...(local.upload !== "uploaded" ? { upload: "uploaded" as const, lastError: null } : {}),
  };
}
