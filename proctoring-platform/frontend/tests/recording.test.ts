import "fake-indexeddb/auto";
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import Ajv2020 from "ajv/dist/2020";
import addFormats from "ajv-formats";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ApiClient, ApiError } from "../src/api/client";
import { encodeWav } from "../src/audio/wav";
import { backendChange, describe as describeEvent } from "../src/recording/eventStatus";
import { buildEventRecord, sha256Hex } from "../src/recording/eventMetadata";
import { UploadQueue } from "../src/recording/uploadQueue";
import { EventStore, type LocalEvent } from "../src/storage/eventStore";
import type { EventStatusResponse } from "../src/types/contracts";

const SCHEMAS = join(__dirname, "..", "..", "docs", "contracts", "schemas");
const T0 = Date.parse("2026-11-23T10:00:00.000Z");

function detected(overrides = {}) {
  return {
    groupId: "11111111-1111-4111-8111-111111111111", partIndex: 0, isFinal: true,
    startSample: 16000, endSample: 16000 + 48000, speechStartSample: 16000 + 40000 - 8000,
    maxProbability: 0.97, speechSeconds: 1.2, preEventSeconds: 2, overlapSeconds: 0, noiseFloorDbfs: -62.4, ...overrides,
  };
}

async function record(overrides: Partial<LocalEvent> = {}, sessionId = "ses_ABCDEFGHJKMNPQRSTVWXYZ012") {
  const wav = encodeWav(new Float32Array(48000).fill(0.1));
  const r = await buildEventRecord({
    event: detected(), wav, sessionId, sequenceNumber: 3, epochOfSample: (s) => T0 + (s / 16000) * 1000,
    vad: { startThreshold: 0.5, endThreshold: 0.35, modelName: "silero-vad-v5" }, video: null, nowMs: T0,
  });
  return { ...r, ...overrides };
}

describe("event metadata", () => {
  it("matches audio-event-metadata.v1.json and describes the WAV exactly", async () => {
    const r = await record();
    const m = JSON.parse(r.metadataJson);
    expect(m.durationSeconds).toBe(3);
    expect(m.startedAt).toBe("2026-11-23T10:00:01.000Z");
    expect(m.endedAt).toBe("2026-11-23T10:00:04.000Z");
    expect(Date.parse(m.endedAt) - Date.parse(m.startedAt)).toBe(m.durationSeconds * 1000); // backend cross-check
    expect(Date.parse(m.speechStartedAt)).toBeGreaterThanOrEqual(Date.parse(m.startedAt));
    expect(m.audio).toEqual({ format: "wav", codec: "pcm_s16le", sampleRate: 16000, channels: 1, sizeBytes: 44 + 96000,
      sha256: await sha256Hex(await r.wav!.arrayBuffer()) });
    expect(m.video).toEqual({ recordedLocally: false, uploaded: false });
    expect(m.part).toEqual({ groupId: detected().groupId, index: 0, isFinal: true, overlapSeconds: 0 });
    expect(r.eventId).toBe(m.eventId);

    if (existsSync(SCHEMAS)) {
      const ajv = new Ajv2020({ strict: false });
      addFormats(ajv);
      const validate = ajv.compile(JSON.parse(readFileSync(join(SCHEMAS, "audio-event-metadata.v1.json"), "utf8")));
      expect(validate(m), JSON.stringify(validate.errors)).toBe(true);
    }
  });

  it("refuses to build an event from an invalid WAV", async () => {
    const bad = encodeWav(new Float32Array(100));
    new DataView(bad).setUint32(24, 48000, true);
    await expect(buildEventRecord({ event: detected(), wav: bad, sessionId: "s", sequenceNumber: 0, epochOfSample: () => T0,
      vad: { startThreshold: 0.5, endThreshold: 0.35, modelName: "m" }, video: null })).rejects.toThrow(/16000/);
  });
});

// ------------------------------------------------------------------ IndexedDB

let store: EventStore;
beforeEach(async () => {
  await new Promise<void>((resolve) => {
    const request = indexedDB.deleteDatabase("proctoring-poc");
    request.onsuccess = request.onerror = () => resolve();
  });
  store = await EventStore.open();
});
afterEach(() => store.close());

describe("EventStore", () => {
  it("persists events with their WAV and video blobs and lists them per session", async () => {
    const a = await record({ eventId: "a", sequenceNumber: 2, video: new Blob(["webm"], { type: "video/webm" }) });
    const b = await record({ eventId: "b", sequenceNumber: 1 });
    const other = await record({ eventId: "c" }, "ses_OTHER");
    for (const e of [a, b, other]) await store.put(e);
    const listed = await store.listBySession(a.sessionId);
    expect(listed.map((e) => e.eventId)).toEqual(["b", "a"]);
    expect(await (listed[1].video as Blob).text()).toBe("webm");
    expect((await (listed[0].wav as Blob).arrayBuffer()).byteLength).toBe(b.wavBytes);
  });

  it("updates and applies retention", async () => {
    await store.put(await record({ eventId: "old", createdAtMs: T0 - 10 * 86_400_000 }));
    await store.put(await record({ eventId: "new" }));
    await store.update("new", () => ({ upload: "uploaded", wav: null }));
    expect((await store.get("new"))!.upload).toBe("uploaded");
    expect(await store.deleteOlderThan(T0 - 7 * 86_400_000)).toBe(1);
    expect((await store.listAll()).map((e) => e.eventId)).toEqual(["new"]);
  });
});

// ------------------------------------------------------------------ upload queue

function backendEvent(id: string, status: EventStatusResponse["status"] = "QUEUED"): EventStatusResponse {
  return { eventId: id, sessionId: "s", sequenceNumber: 3, status, receivedAt: "2026-11-23T10:00:05Z", updatedAt: "2026-11-23T10:00:05Z",
    startedAt: "2026-11-23T10:00:01Z", durationSeconds: 3, attempts: 0, error: null, result: null };
}

class FakeApi {
  calls: { metadataJson: string; size: number }[] = [];
  constructor(private readonly responses: (() => Promise<{ status: number; event: EventStatusResponse }>)[]) {}
  async uploadEvent(_s: string, _t: string, metadataJson: string, wav: Blob) {
    this.calls.push({ metadataJson, size: wav.size });
    const next = this.responses.shift();
    if (!next) throw new Error("unexpected upload");
    return next();
  }
}

function queue(api: FakeApi, clock: { now: number }, changes: LocalEvent[] = []) {
  return new UploadQueue(store, api as unknown as ApiClient, () => "tok_test", (e) => changes.push(e), { baseBackoffMs: 1000, now: () => clock.now });
}

describe("UploadQueue", () => {
  it("uploads, stores the backend status and keeps the local WAV for playback", async () => {
    const r = await record({ eventId: "e1" });
    const api = new FakeApi([async () => ({ status: 202, event: backendEvent("e1") })]);
    const q = queue(api, { now: T0 });
    await q.enqueue(r);
    const saved = (await store.get("e1"))!;
    expect(saved.upload).toBe("uploaded");
    expect(saved.status).toBe("QUEUED");
    expect(saved.wav).not.toBeNull();
    expect(api.calls).toHaveLength(1);
    q.stop();
  });

  it("retries network failures with back-off, re-sending identical metadata and audio", async () => {
    const clock = { now: T0 };
    const api = new FakeApi([
      async () => { throw new ApiError(0, "The backend is not reachable"); },
      async () => { throw new ApiError(502, "Bad gateway"); },
      async () => ({ status: 200, event: backendEvent("e2") }), // the first attempt had actually arrived
    ]);
    const q = queue(api, clock);
    await q.enqueue(await record({ eventId: "e2" }));
    let saved = (await store.get("e2"))!;
    expect(saved.upload).toBe("retry-pending");
    expect(saved.nextAttemptAt).toBe(T0 + 1000);
    expect(saved.wav).not.toBeNull(); // kept until the backend has it

    await q.kick(); // not due yet
    expect(api.calls).toHaveLength(1);
    clock.now += 1000;
    await q.kick();
    saved = (await store.get("e2"))!;
    expect(saved.uploadAttempts).toBe(2);
    expect(saved.nextAttemptAt).toBe(clock.now + 2000); // doubled
    clock.now += 2000;
    await q.kick();
    expect((await store.get("e2"))!.upload).toBe("uploaded");
    expect(new Set(api.calls.map((c) => c.metadataJson)).size).toBe(1);
    expect(new Set(api.calls.map((c) => c.size)).size).toBe(1);
    q.stop();
  });

  it("treats 503 with an event body as stored, and validation errors as permanent", async () => {
    const api = new FakeApi([
      async () => ({ status: 503, event: backendEvent("ok", "RECEIVED") }),
      async () => { throw new ApiError(422, "Validation failed - durationSeconds: differs"); },
    ]);
    const q = queue(api, { now: T0 });
    await q.enqueue(await record({ eventId: "ok", createdAtMs: T0 }));
    await q.enqueue(await record({ eventId: "bad", createdAtMs: T0 + 1 }));
    expect((await store.get("ok"))!.status).toBe("RECEIVED");
    const bad = (await store.get("bad"))!;
    expect(bad.upload).toBe("failed");
    expect(bad.lastError).toMatch(/durationSeconds/);
    expect(bad.wav).not.toBeNull(); // kept for a manual retry
    q.stop();
  });

  it("does not upload an event again once it is uploaded", async () => {
    const api = new FakeApi([async () => ({ status: 202, event: backendEvent("once") })]);
    const q = queue(api, { now: T0 });
    await q.enqueue(await record({ eventId: "once" }));
    await q.kick();
    await q.retry("once");
    expect(api.calls).toHaveLength(1);
    q.stop();
  });

  it("resumes uploads interrupted by a page reload", async () => {
    await store.put(await record({ eventId: "interrupted", upload: "uploading" }));
    const api = new FakeApi([async () => ({ status: 202, event: backendEvent("interrupted") })]);
    const q = queue(api, { now: T0 });
    await q.kick();
    expect((await store.get("interrupted"))!.upload).toBe("uploaded");
    q.stop();
  });
});

// ------------------------------------------------------------------ status shown to the candidate

describe("event status", () => {
  it("distinguishes every stage of the lifecycle", async () => {
    const base = await record();
    const result = (verified: boolean) => ({ speech: { available: true, verified }, transcription: { available: verified, text: "hi" } }) as never;
    const cases: [Partial<LocalEvent>, string, string][] = [
      [{ upload: "pending" }, "uploading", "Uploading audio"],
      [{ upload: "retry-pending", uploadAttempts: 2 }, "retry-pending", "Upload failed - retrying automatically"],
      [{ upload: "failed" }, "upload-failed", "Upload failed - retry required"],
      [{ upload: "uploaded", status: "RECEIVED" }, "saved", "Audio saved"],
      [{ upload: "uploaded", status: "QUEUED", statusUpdatedAt: new Date(T0).toISOString() }, "saved", "Audio saved"],
      [{ upload: "uploaded", status: "PROCESSING" }, "saved", "Audio saved"],
      [{ upload: "uploaded", status: "COMPLETED", result: result(true) }, "saved", "Audio saved"],
      [{ upload: "uploaded", status: "COMPLETED", result: result(false) }, "saved", "Audio saved"],
      [{ upload: "uploaded", status: "FAILED", backendError: { code: "AUDIO_NOT_FOUND", message: "x" } }, "saved-analysis-failed", "Audio saved"],
    ];
    for (const [patch, stage, label] of cases) {
      const info = describeEvent({ ...base, ...patch }, T0);
      expect(info.stage).toBe(stage);
      expect(info.label).toBe(label);
    }
    // analysis is secondary information; it never decides whether the audio counts as saved
    expect(describeEvent({ ...base, upload: "uploaded", status: "COMPLETED", result: result(false) }, T0).analysis).toMatch(/no clear speech/);
    const slow = describeEvent({ ...base, upload: "uploaded", status: "QUEUED", statusUpdatedAt: new Date(T0).toISOString() }, T0 + 120_000);
    expect(slow.analysis).toMatch(/processing service may be off/);
  });

  it("applies only newer backend states", async () => {
    const local = { ...(await record()), upload: "uploaded" as const, status: "PROCESSING" as const, statusUpdatedAt: "2026-11-23T10:00:10Z" };
    expect(backendChange(local, { ...backendEvent(local.eventId, "QUEUED"), updatedAt: "2026-11-23T10:00:05Z" })).toBeNull();
    expect(backendChange(local, { ...backendEvent(local.eventId, "COMPLETED"), updatedAt: "2026-11-23T10:00:12Z" })?.status).toBe("COMPLETED");
    const fromSse = backendChange({ ...local, upload: "uploading", statusUpdatedAt: null }, backendEvent(local.eventId));
    expect(fromSse?.upload).toBe("uploaded"); // the backend has it: no second upload
  });
});
