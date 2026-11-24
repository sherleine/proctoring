import { describe, expect, it } from "vitest";
import type { ApiClient } from "../src/api/client";
import { SseParser, UpdateStream, type StreamState } from "../src/api/sse";
import type { EventStatusResponse } from "../src/types/contracts";

describe("SseParser", () => {
  it("parses events split across arbitrary chunk boundaries, ignoring comments", () => {
    const text = "retry: 3000\n\nid: 41\nevent: ready\ndata: {\"sessionId\":\"s\"}\n\n: heartbeat\n\nid: 42\r\nevent: event.updated\r\ndata: {\"a\":1}\r\n\r\n";
    const parser = new SseParser();
    const out = [];
    for (let i = 0; i < text.length; i += 7) out.push(...parser.push(text.slice(i, i + 7)));
    expect(out).toEqual([
      { id: "41", event: "ready", data: "{\"sessionId\":\"s\"}" },
      { id: "42", event: "event.updated", data: "{\"a\":1}" },
    ]);
    expect(parser.retryMs).toBe(3000);
  });
});

function streamOf(text: string, keepOpen = false): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(text));
      if (!keepOpen) controller.close();
    },
  });
}

const status = (id: string, s: EventStatusResponse["status"]) => JSON.stringify({ eventId: id, status: s, updatedAt: "2026-11-23T10:00:00Z" });

describe("UpdateStream", () => {
  it("delivers updates, reconnects with Last-Event-ID, and stops on 'end'", async () => {
    const lastIds: (string | null)[] = [];
    const responses = [
      () => new Response(streamOf(`id: 5\nevent: ready\ndata: {}\n\nid: 6\nevent: event.updated\ndata: ${status("e1", "PROCESSING")}\n\n`)),
      () => { throw new TypeError("network down"); },
      () => new Response(streamOf(`id: 6\nevent: ready\ndata: {}\n\nid: 9\nevent: event.updated\ndata: ${status("e1", "COMPLETED")}\n\nevent: end\ndata: {"reason":"token-expired"}\n\n`)),
    ];
    const api = { openUpdates: async (_s: string, _t: string, lastId: string | null) => { lastIds.push(lastId); return responses.shift()!(); } } as unknown as ApiClient;
    const seen: string[] = [];
    const states: StreamState[] = [];
    let resyncs = 0;
    let ended = "";
    const stream = new UpdateStream(api, "s", "tok", {
      onEvent: (e) => seen.push(`${e.eventId}:${e.status}`),
      onResync: () => resyncs++,
      onState: (s) => states.push(s),
      onEnd: (r) => (ended = r),
    }, { pollIntervalMs: 10, sleep: async () => {} });
    await stream.start();
    expect(seen).toEqual(["e1:PROCESSING", "e1:COMPLETED"]);
    expect(lastIds).toEqual([null, "6", "6"]); // resumed from the last received id
    expect(resyncs).toBe(2);
    expect(ended).toBe("token-expired");
    expect(states).toContain("reconnecting");
    expect(states.at(-1)).toBe("closed");
  });

  it("falls back to polling after repeated failures and keeps trying to reconnect", async () => {
    let attempts = 0;
    const api = { openUpdates: async () => { attempts++; if (attempts < 5) throw new TypeError("down"); return new Response(null, { status: 401 }); } } as unknown as ApiClient;
    const states: StreamState[] = [];
    let polls = 0;
    const stream = new UpdateStream(api, "s", "tok", { onEvent: () => {}, onResync: () => polls++, onState: (s) => states.push(s), onEnd: () => {} },
      { pollIntervalMs: 10, failuresBeforePolling: 3, sleep: async () => {} });
    await stream.start();
    expect(states).toContain("polling");
    expect(polls).toBeGreaterThanOrEqual(2);
    expect(attempts).toBe(5);
  });
});
