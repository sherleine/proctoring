// Live updates from GET /api/v1/sessions/{id}/updates (Server-Sent Events, http-api.v1.md "Real-time updates").
// The browser's EventSource cannot send an Authorization header, so the stream is read with fetch().
// Reconnects resume with Last-Event-ID; after repeated failures the caller is asked to poll GET /events.
import type { ApiClient } from "./client";
import { ApiError } from "./client";
import type { EventStatusResponse } from "../types/contracts";

export interface SseMessage {
  id: string | null;
  event: string;
  data: string;
}

/** Incremental text/event-stream parser (chunks may split lines anywhere). */
export class SseParser {
  private buffer = "";
  private id: string | null = null;
  private event = "";
  private data: string[] = [];
  retryMs: number | null = null;

  push(chunk: string): SseMessage[] {
    this.buffer += chunk;
    const out: SseMessage[] = [];
    let newline: number;
    while ((newline = this.buffer.search(/\r\n|\r|\n/)) >= 0) {
      const line = this.buffer.slice(0, newline);
      const sepLength = this.buffer.startsWith("\r\n", newline) ? 2 : 1;
      this.buffer = this.buffer.slice(newline + sepLength);
      if (line === "") {
        if (this.data.length > 0 || this.event) out.push({ id: this.id, event: this.event || "message", data: this.data.join("\n") });
        this.event = "";
        this.data = [];
        continue;
      }
      if (line.startsWith(":")) continue; // comment / heartbeat
      const colon = line.indexOf(":");
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? "" : line.slice(colon + 1).replace(/^ /, "");
      if (field === "id") this.id = value;
      else if (field === "event") this.event = value;
      else if (field === "data") this.data.push(value);
      else if (field === "retry" && /^\d+$/.test(value)) this.retryMs = Number(value);
    }
    return out;
  }
}

export type StreamState = "connecting" | "live" | "reconnecting" | "polling" | "closed";

export interface UpdateStreamHandlers {
  onEvent(event: EventStatusResponse): void;
  /** Stream (re)started without history, or history was pruned: reload GET /events. */
  onResync(): void;
  onState(state: StreamState, detail?: string): void;
  /** The token is no longer valid; the stream stopped for good. */
  onEnd(reason: string): void;
}

export interface UpdateStreamOptions {
  pollIntervalMs: number;
  maxBackoffMs?: number;
  failuresBeforePolling?: number;
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
}

const defaultSleep = (ms: number, signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    const t = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(t); resolve(); }, { once: true });
  });

export class UpdateStream {
  private readonly abort = new AbortController();
  private lastEventId: string | null = null;
  private failures = 0;
  private running = false;

  constructor(
    private readonly api: ApiClient,
    private readonly sessionId: string,
    private readonly token: string,
    private readonly handlers: UpdateStreamHandlers,
    private readonly options: UpdateStreamOptions,
  ) {}

  get resumeId(): string | null {
    return this.lastEventId;
  }

  start(): Promise<void> {
    if (this.running) return Promise.resolve();
    this.running = true;
    return this.loop();
  }

  stop(): void {
    this.abort.abort();
    this.handlers.onState("closed");
  }

  private async loop(): Promise<void> {
    const sleep = this.options.sleep ?? defaultSleep;
    const signal = this.abort.signal;
    let retryMs = 3000;
    while (!signal.aborted) {
      this.handlers.onState(this.failures === 0 ? "connecting" : "reconnecting");
      try {
        const response = await this.api.openUpdates(this.sessionId, this.token, this.lastEventId, signal);
        if (response.status === 401 || response.status === 404) {
          this.handlers.onEnd(response.status === 401 ? "token-expired" : "unauthorized");
          this.handlers.onState("closed");
          return;
        }
        if (!response.ok || !response.body) throw new ApiError(response.status, `HTTP ${response.status}`);
        const ended = await this.read(response.body, (ms) => (retryMs = ms));
        if (ended) return;
        // The server closed the stream normally (e.g. restart): reconnect.
        this.failures = Math.max(this.failures, 1);
      } catch (err) {
        if (signal.aborted) return;
        this.failures++;
      }
      if (signal.aborted) return;
      const failuresBeforePolling = this.options.failuresBeforePolling ?? 3;
      if (this.failures >= failuresBeforePolling) {
        this.handlers.onState("polling", "Live updates unavailable - checking for results periodically");
        this.handlers.onResync(); // the caller polls GET /events
        await sleep(this.options.pollIntervalMs, signal);
      } else {
        const backoff = Math.min(this.options.maxBackoffMs ?? 30_000, retryMs * 2 ** Math.max(0, this.failures - 1));
        await sleep(backoff, signal);
      }
    }
  }

  /** Returns true when the server ended the stream for good ("end" event). */
  private async read(body: ReadableStream<Uint8Array>, setRetry: (ms: number) => void): Promise<boolean> {
    const parser = new SseParser();
    const decoder = new TextDecoder();
    const reader = body.getReader();
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) return false;
        for (const message of parser.push(decoder.decode(value, { stream: true }))) {
          if (parser.retryMs) setRetry(parser.retryMs);
          if (message.id !== null) this.lastEventId = message.id;
          switch (message.event) {
            case "ready":
            case "resync":
              this.failures = 0;
              this.handlers.onState("live");
              this.handlers.onResync();
              break;
            case "event.updated":
              this.handlers.onEvent(JSON.parse(message.data) as EventStatusResponse);
              break;
            case "end": {
              const reason = (JSON.parse(message.data) as { reason?: string }).reason ?? "ended";
              this.handlers.onEnd(reason);
              this.handlers.onState("closed");
              return true;
            }
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  }
}
