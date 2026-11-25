// Contract 1.1 on the frontend: voice-sample metadata, observation display helpers, status wording.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import Ajv2020 from "ajv/dist/2020";
import addFormats from "ajv-formats";
import { describe, expect, it } from "vitest";
import { encodeWav, validateWav } from "../src/audio/wav";
import { evidenceSummary, isInformational, OBSERVATION_LABELS, voiceCheck } from "../src/components/ObservationList";
import { buildEnrolmentRecord } from "../src/recording/eventMetadata";
import { describe as describeEvent } from "../src/recording/eventStatus";
import type { LocalEvent } from "../src/storage/eventStore";
import type { AnalysisResult } from "../src/types/contracts";

const SCHEMAS = join(__dirname, "..", "..", "docs", "contracts", "schemas");
const T0 = Date.parse("2026-11-24T10:00:00.000Z");

function schema(name: string) {
  const ajv = new Ajv2020({ strict: false });
  addFormats(ajv);
  return ajv.compile(JSON.parse(readFileSync(join(SCHEMAS, name), "utf8")));
}

describe("voice sample (enrolment)", () => {
  it("builds a contract-valid enrolment upload that is marked as such locally", async () => {
    const wav = encodeWav(new Float32Array(16000 * 15).fill(0.05));
    const r = await buildEnrolmentRecord({
      wav, sessionId: "ses_ABCDEFGHJKMNPQRSTVWXYZ012", sequenceNumber: 0, startMs: T0, speechSeconds: 12.5, maxProbability: 0.99,
      vad: { startThreshold: 0.5, endThreshold: 0.35, modelName: "silero-vad-v5" }, nowMs: T0,
    });
    const metadata = JSON.parse(r.metadataJson);
    const validate = schema("audio-event-metadata.v1.json");
    expect(validate(metadata), JSON.stringify(validate.errors)).toBe(true);
    expect(metadata.eventType).toBe("enrolment");
    expect(metadata.trigger.type).toBe("enrolment");
    expect(metadata.durationSeconds).toBe(15);
    expect(Date.parse(metadata.endedAt) - Date.parse(metadata.startedAt)).toBe(15000);
    expect(r.kind).toBe("enrolment");
    expect(validateWav(await r.wav!.arrayBuffer()).durationSeconds).toBe(15);
  });

  it("never claims more speech than the clip contains", async () => {
    const wav = encodeWav(new Float32Array(16000 * 9).fill(0.05));
    const r = await buildEnrolmentRecord({
      wav, sessionId: "ses_ABCDEFGHJKMNPQRSTVWXYZ012", sequenceNumber: 1, startMs: T0, speechSeconds: 40, maxProbability: 1.2,
      vad: { startThreshold: 0.5, endThreshold: 0.35, modelName: "silero-vad-v5" },
    });
    const metadata = JSON.parse(r.metadataJson);
    expect(metadata.trigger.speechSeconds).toBe(9);
    expect(metadata.trigger.maxProbability).toBe(1);
  });
});

describe("observation display", () => {
  it("has a neutral label for every contract observation type", () => {
    const types = (JSON.parse(readFileSync(join(SCHEMAS, "analysis-result.v1.json"), "utf8")).$defs.observation.properties
      .observationType.enum) as string[];
    expect(Object.keys(OBSERVATION_LABELS).sort()).toEqual([...types].sort());
    for (const label of Object.values(OBSERVATION_LABELS)) expect(label.toLowerCase()).not.toContain("cheat");
  });

  it("treats SPEECH_DETECTED / NO_SPEECH_DETECTED as informational, with or without a category", () => {
    const o = (observationType: string, category?: "informational" | "review") =>
      ({ observationType, category }) as unknown as import("../src/types/contracts").Observation;
    expect(isInformational(o("SPEECH_DETECTED"))).toBe(true);
    expect(isInformational(o("NO_SPEECH_DETECTED", "informational"))).toBe(true);
    expect(isInformational(o("MULTIPLE_SPEAKERS_DETECTED"))).toBe(false);
    expect(isInformational(o("NON_CANDIDATE_VOICE", "review"))).toBe(false);
    expect(OBSERVATION_LABELS.MULTIPLE_SPEAKERS).toBe(OBSERVATION_LABELS.MULTIPLE_SPEAKERS_DETECTED); // legacy alias
  });

  it("summarises simple evidence values and skips nested objects and notes", () => {
    expect(evidenceSummary({ similarityToCandidate: 0.04567, regionSeconds: 3.5, thresholds: { same: 0.4 }, note: "x", decision: "LIKELY_NON_CANDIDATE" }))
      .toBe("similarityToCandidate: 0.046 · regionSeconds: 3.5 · decision: LIKELY_NON_CANDIDATE");
  });

  it("describes the voice check without implying misconduct", () => {
    const base = { speaker: { available: true, reference: "present" as const, comparisons: [] } } as unknown as AnalysisResult;
    expect(voiceCheck(null)).toBeNull();
    expect(voiceCheck({ speaker: { available: false, reference: "missing" } } as unknown as AnalysisResult)).toContain("not compared");
    expect(voiceCheck(base)).toContain("no speech long enough");
    const withDecisions = (...d: string[]) => ({ speaker: { available: true, reference: "present",
      comparisons: d.map((decision) => ({ start: 0, end: 3, similarity: 0.5, decision })) } }) as unknown as AnalysisResult;
    expect(voiceCheck(withDecisions("LIKELY_CANDIDATE"))).toBe("Voice check: candidate voice");
    expect(voiceCheck(withDecisions("LIKELY_CANDIDATE", "UNCERTAIN"))).toContain("some parts uncertain");
    expect(voiceCheck(withDecisions("LIKELY_CANDIDATE", "LIKELY_NON_CANDIDATE"))).toContain("differs");
  });
});

describe("status wording", () => {
  const ev = (status: LocalEvent["status"]) => ({ upload: "uploaded", status, statusUpdatedAt: null, result: null, backendError: null }) as unknown as LocalEvent;
  it("shows received / AI processing / processing failure", () => {
    expect(describeEvent(ev("RECEIVED")).analysis).toBe("speech event received");
    expect(describeEvent(ev("PROCESSING")).analysis).toBe("AI processing…");
    expect(describeEvent(ev("FAILED")).analysis).toBe("AI processing failure");
  });
});
