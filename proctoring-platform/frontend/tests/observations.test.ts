// Contract 1.1 on the frontend: voice-sample metadata, observation display helpers, status wording.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import Ajv2020 from "ajv/dist/2020";
import addFormats from "ajv-formats";
import { describe, expect, it } from "vitest";
import { evidenceSummary, isInformational, OBSERVATION_LABELS, voiceCheck } from "../src/components/ObservationList";
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
    expect(isInformational(o("BACKGROUND_CONVERSATION_DETECTED", "review"))).toBe(false);
    expect(OBSERVATION_LABELS.MULTIPLE_SPEAKERS).toBe(OBSERVATION_LABELS.MULTIPLE_SPEAKERS_DETECTED); // legacy alias
  });

  it("summarises simple evidence values and skips nested objects and notes", () => {
    expect(evidenceSummary({ levelBelowMainSpeakerDb: 18.456, speechSeconds: 3.5, thresholds: { same: 0.4 }, note: "x", mainSpeaker: "S1" }))
      .toBe("levelBelowMainSpeakerDb: 18.456 · speechSeconds: 3.5 · mainSpeaker: S1");
  });

  it("shows how many different voices diarization found, without claiming who they are", () => {
    expect(voiceCheck(null)).toBeNull();
    expect(voiceCheck({ diarization: { available: false } } as unknown as AnalysisResult)).toBeNull();
    const diarized = (speakerCount: number, distinctVoices: number | null) =>
      ({ diarization: { available: true, speakerCount, distinctVoices } }) as unknown as AnalysisResult;
    expect(voiceCheck(diarized(1, 1))).toBe("Voices: one");
    expect(voiceCheck(diarized(2, 2))).toBe("Voices: 2 different voices");
    expect(voiceCheck(diarized(2, 1))).toBe("Voices: one"); // two diarized parts of the same voice
    expect(voiceCheck(diarized(2, null))).toBe("Voices: 2 different voices"); // voices not compared: diarization count
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
