// HTTP client for the backend (docs/contracts/http-api.v1.md). The session token is sent only in the
// Authorization header and never logged.
import type { EventStatusResponse, Problem, Session } from "../types/contracts";

export class ApiError extends Error {
  constructor(
    readonly status: number, // 0 = network error (backend unreachable)
    message: string,
    readonly problem?: Problem,
  ) {
    super(message);
  }

  /** Worth retrying later: network failure, timeout, rate limit or server-side error. */
  get retryable(): boolean {
    return this.status === 0 || this.status === 408 || this.status === 429 || this.status >= 500;
  }
}

export type Fetch = typeof fetch;

export class ApiClient {
  constructor(
    private readonly baseUrl: string,
    private readonly fetchImpl: Fetch = (...args) => fetch(...args),
  ) {}

  createSession(candidateId: string, examId: string, idempotencyKey: string): Promise<Session> {
    return this.json<Session>("POST", "/api/v1/sessions", null, {
      body: JSON.stringify({ candidateId, examId, client: { userAgent: navigator.userAgent.slice(0, 512), appVersion: "1.0.0" } }),
      headers: { "Content-Type": "application/json", "Idempotency-Key": idempotencyKey },
    });
  }

  endSession(sessionId: string, token: string): Promise<Session> {
    return this.json<Session>("POST", `/api/v1/sessions/${sessionId}/end`, token);
  }

  /**
   * Uploads one event (multipart: "metadata" JSON + "audio" WAV). 202 = new, 200 = same event again
   * (idempotent retry), 503 = stored by the backend but not yet queued (it is queued automatically).
   */
  async uploadEvent(sessionId: string, token: string, metadataJson: string, wav: Blob): Promise<{ status: number; event: EventStatusResponse }> {
    const form = new FormData();
    form.append("metadata", new Blob([metadataJson], { type: "application/json" }));
    form.append("audio", new Blob([wav], { type: "audio/wav" }), "event.wav");
    const response = await this.send("POST", `/api/v1/sessions/${sessionId}/events`, token, { body: form });
    const body = await response.json().catch(() => null);
    if (response.status === 200 || response.status === 202) return { status: response.status, event: body as EventStatusResponse };
    if (response.status === 503 && body?.event) return { status: 503, event: body.event as EventStatusResponse };
    throw toError(response.status, body);
  }

  getEvent(sessionId: string, token: string, eventId: string): Promise<EventStatusResponse> {
    return this.json("GET", `/api/v1/sessions/${sessionId}/events/${eventId}`, token);
  }

  async listEvents(sessionId: string, token: string): Promise<EventStatusResponse[]> {
    return (await this.json<{ items: EventStatusResponse[] }>("GET", `/api/v1/sessions/${sessionId}/events`, token)).items;
  }

  /** Opens GET /updates (Server-Sent Events) - see sse.ts. */
  openUpdates(sessionId: string, token: string, lastEventId: string | null, signal: AbortSignal): Promise<Response> {
    const headers: Record<string, string> = { Accept: "text/event-stream" };
    if (lastEventId !== null) headers["Last-Event-ID"] = lastEventId;
    return this.send("GET", `/api/v1/sessions/${sessionId}/updates`, token, { headers, signal });
  }

  private async json<T>(method: string, path: string, token: string | null, init: RequestInit = {}): Promise<T> {
    const response = await this.send(method, path, token, init);
    const body = await response.json().catch(() => null);
    if (!response.ok) throw toError(response.status, body);
    return body as T;
  }

  private async send(method: string, path: string, token: string | null, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers);
    if (token) headers.set("Authorization", `Bearer ${token}`);
    try {
      return await this.fetchImpl(this.baseUrl + path, { ...init, method, headers, cache: "no-store" });
    } catch (err) {
      if ((err as Error)?.name === "AbortError") throw err;
      throw new ApiError(0, "The backend is not reachable");
    }
  }
}

function toError(status: number, body: unknown): ApiError {
  const problem = body && typeof body === "object" ? (body as Problem) : undefined;
  const fieldErrors = problem?.errors ? Object.entries(problem.errors).map(([k, v]) => `${k}: ${v.join(", ")}`).join("; ") : "";
  const message = [problem?.title ?? `HTTP ${status}`, problem?.detail, fieldErrors].filter(Boolean).join(" - ");
  return new ApiError(status, message, problem);
}
