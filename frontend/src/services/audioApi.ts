/**
 * HTTP client for the backend API. The only module that knows backend URLs.
 *
 * Not used by the UI yet: event clips are stored locally (IndexedDB). Session,
 * status and result calls follow the backend contract planned for Phase 5.
 */
import { appConfig } from "../config/appConfig";
import type { AnalysisResult } from "../types/analysis";
import type { ApiErrorBody, ClipStatusResponse, HealthResponse, SessionResponse } from "../types/api";

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body?: ApiErrorBody,
  ) {
    super(message);
  }

  /** Network failures and 5xx/429 are worth retrying; 4xx validation errors are not. */
  get retryable(): boolean {
    return this.status === 0 || this.status === 429 || this.status >= 500;
  }

  /** The endpoint does not exist on this backend (not deployed yet). */
  get endpointMissing(): boolean {
    // A missing route returns FastAPI's default {"detail": "Not Found"}; a missing
    // resource (e.g. unknown session) returns our structured error body instead.
    return (this.status === 404 && !this.body) || this.status === 405;
  }
}

async function request<T>(path: string, init?: RequestInit & { timeoutMs?: number }): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), init?.timeoutMs ?? 10000);
  let res: Response;
  try {
    res = await fetch(`${appConfig.apiBaseUrl}${path}`, { ...init, signal: controller.signal });
  } catch (err) {
    const reason = (err as Error).name === "AbortError" ? "request timed out" : (err as Error).message;
    throw new ApiError(`Backend unreachable: ${reason}`, 0);
  } finally {
    clearTimeout(timer);
  }
  const text = await res.text();
  let body: unknown = undefined;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    /* non-JSON body, e.g. proxy error page */
  }
  if (!res.ok) {
    const err = body as Partial<ApiErrorBody> | undefined;
    const structured = err && typeof err.error_code === "string" ? (err as ApiErrorBody) : undefined;
    throw new ApiError(structured?.message ?? `HTTP ${res.status}`, res.status, structured);
  }
  return body as T;
}

const json = (body: unknown): RequestInit => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

export const audioApi = {
  getHealth: () => request<HealthResponse>("/api/health", { timeoutMs: 6000 }),

  createSession: () => request<SessionResponse>("/api/sessions", json({})),

  stopSession: (sessionId: string) => request<SessionResponse>(`/api/sessions/${encodeURIComponent(sessionId)}/stop`, json({})),

  getClipStatus: (clipId: string) => request<ClipStatusResponse>(`/api/audio/${encodeURIComponent(clipId)}/status`),

  getClipResult: (clipId: string) => request<AnalysisResult>(`/api/audio/${encodeURIComponent(clipId)}/result`),
};
