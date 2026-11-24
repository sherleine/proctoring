/**
 * Event-triggered clip recorder (pure logic, no browser APIs - unit-testable).
 *
 * Fed one frame at a time together with its Silero speech probability:
 *  - listening: frames go into a rolling pre-speech buffer.
 *  - prob >= startThreshold (and SNR above a mild gate) -> speech event starts;
 *    the pre-speech buffer becomes the beginning of the clip.
 *  - recording: frames are appended; frames with prob >= endThreshold count as
 *    speech (hysteresis: continuing needs less evidence than starting).
 *  - `silenceMs` of consecutive sub-threshold frames -> clip ends; trailing
 *    silence beyond `postSpeechPadMs` is cut and returned to the pre-speech buffer.
 *  - events shorter than `minSpeechMs` of confident speech are discarded (misfire),
 *    so clicks and bumps do not create clips.
 *  - clips reaching `maxClipMs` are split; the next part continues with the very
 *    next frame, so no audio is lost or duplicated at the boundary.
 */
import { RollingBuffer } from "./rollingBuffer";

export type SplitReason = "speech_end" | "max_duration" | "session_stopped";

export interface ClipRecorderConfig {
  sampleRate: number;
  frameSamples: number;
  startThreshold: number;
  endThreshold: number;
  minSpeechMs: number;
  silenceMs: number;
  preSpeechMs: number;
  postSpeechPadMs: number;
  maxClipMs: number;
  /** Speech may only start when the frame is at least this many dB above the noise floor. */
  minStartSnrDb: number;
}

export interface FinalizedClip {
  eventId: string;
  partIndex: number;
  splitReason: SplitReason;
  samples: Float32Array;
  /** Absolute stream position (in samples) of samples[0]. */
  startSample: number;
  preSpeechSamples: number;
  speechFrames: number;
  totalFrames: number;
  maxProbability: number;
  meanSpeechProbability: number | null;
}

export type RecorderEvent =
  | { type: "speech-start"; eventId: string; sample: number }
  | { type: "clip"; clip: FinalizedClip }
  | { type: "speech-end"; eventId: string; sample: number; parts: number }
  | { type: "misfire"; eventId: string; speechMs: number };

interface Part {
  eventId: string;
  partIndex: number;
  pre: Float32Array;
  startSample: number;
  frames: Float32Array[];
  speechFrames: number;
  maxProbability: number;
  speechProbSum: number;
}

export class ClipRecorder {
  private readonly pre: RollingBuffer;
  private part: Part | null = null;
  private silentRun = 0;
  private eventConfidentFrames = 0;
  private streamSamples = 0;
  private eventCounter = 0;

  private readonly silenceFrames: number;
  private readonly padFrames: number;
  private readonly minSpeechFrames: number;
  private readonly maxClipSamples: number;

  constructor(readonly config: ClipRecorderConfig, private readonly newId: () => string = defaultId) {
    const frameMs = (config.frameSamples / config.sampleRate) * 1000;
    this.silenceFrames = Math.max(1, Math.round(config.silenceMs / frameMs));
    this.padFrames = Math.min(this.silenceFrames, Math.round(config.postSpeechPadMs / frameMs));
    this.minSpeechFrames = Math.max(1, Math.round(config.minSpeechMs / frameMs));
    this.maxClipSamples = Math.round((config.maxClipMs / 1000) * config.sampleRate);
    this.pre = new RollingBuffer(Math.max(1, Math.round((config.preSpeechMs / 1000) * config.sampleRate)));
  }

  get recording(): boolean {
    return this.part !== null;
  }

  get currentClipSamples(): number {
    if (!this.part) return 0;
    return this.part.pre.length + this.part.frames.length * this.config.frameSamples;
  }

  /** Remaining silence (ms) before the current clip ends, or null if speech is ongoing. */
  get silenceRemainingMs(): number | null {
    if (!this.part || this.silentRun === 0) return null;
    const frameMs = (this.config.frameSamples / this.config.sampleRate) * 1000;
    return (this.silenceFrames - this.silentRun) * frameMs;
  }

  process(frame: Float32Array, probability: number, snrDb: number | null): RecorderEvent[] {
    const position = this.streamSamples;
    this.streamSamples += frame.length;
    const events: RecorderEvent[] = [];
    const c = this.config;

    if (!this.part) {
      const snrOk = snrDb === null || snrDb >= c.minStartSnrDb;
      if (probability >= c.startThreshold && snrOk) {
        const pre = this.pre.drain();
        this.eventCounter++;
        this.part = {
          eventId: this.newId(),
          partIndex: 0,
          pre,
          startSample: position - pre.length,
          frames: [],
          speechFrames: 0,
          maxProbability: 0,
          speechProbSum: 0,
        };
        this.silentRun = 0;
        this.eventConfidentFrames = 0;
        events.push({ type: "speech-start", eventId: this.part.eventId, sample: position });
        this.append(frame, probability);
      } else {
        this.pre.push(frame);
      }
      return events;
    }

    this.append(frame, probability);
    if (this.silentRun >= this.silenceFrames) {
      events.push(...this.finish("speech_end"));
    } else if (this.currentClipSamples >= this.maxClipSamples) {
      events.push(...this.split());
    }
    return events;
  }

  /** Ends any clip in progress (e.g. monitoring stopped). */
  flush(): RecorderEvent[] {
    return this.part ? this.finish("session_stopped") : [];
  }

  private append(frame: Float32Array, probability: number): void {
    const p = this.part!;
    p.frames.push(frame);
    p.maxProbability = Math.max(p.maxProbability, probability);
    if (probability >= this.config.endThreshold) {
      p.speechFrames++;
      p.speechProbSum += probability;
      this.silentRun = 0;
      if (probability >= this.config.startThreshold) this.eventConfidentFrames++;
    } else {
      this.silentRun++;
    }
  }

  private split(): RecorderEvent[] {
    const p = this.part!;
    const events: RecorderEvent[] = [{ type: "clip", clip: this.toClip(p, "max_duration") }];
    this.part = {
      eventId: p.eventId,
      partIndex: p.partIndex + 1,
      pre: new Float32Array(0),
      startSample: p.startSample + p.pre.length + p.frames.length * this.config.frameSamples,
      frames: [],
      speechFrames: 0,
      maxProbability: 0,
      speechProbSum: 0,
    };
    return events;
  }

  private finish(reason: SplitReason): RecorderEvent[] {
    const p = this.part!;
    this.part = null;
    const trimCount = Math.max(0, Math.min(p.frames.length, this.silentRun - this.padFrames));
    const trimmed = p.frames.splice(p.frames.length - trimCount, trimCount);
    this.silentRun = 0;
    const events: RecorderEvent[] = [];
    const endSample = p.startSample + p.pre.length + p.frames.length * this.config.frameSamples;

    if (p.partIndex === 0 && this.eventConfidentFrames < this.minSpeechFrames) {
      // Too short to be speech: give the audio back to the pre-speech buffer.
      this.pre.push(p.pre);
      for (const f of p.frames) this.pre.push(f);
      const frameMs = (this.config.frameSamples / this.config.sampleRate) * 1000;
      events.push({ type: "misfire", eventId: p.eventId, speechMs: this.eventConfidentFrames * frameMs });
    } else {
      // A continuation part that holds only the trailing pause carries no speech: drop it.
      if (p.speechFrames > 0) events.push({ type: "clip", clip: this.toClip(p, reason) });
      events.push({ type: "speech-end", eventId: p.eventId, sample: endSample, parts: p.partIndex + 1 });
    }
    for (const f of trimmed) this.pre.push(f);
    return events;
  }

  private toClip(p: Part, reason: SplitReason): FinalizedClip {
    const fs = this.config.frameSamples;
    const samples = new Float32Array(p.pre.length + p.frames.length * fs);
    samples.set(p.pre, 0);
    p.frames.forEach((f, i) => samples.set(f, p.pre.length + i * fs));
    return {
      eventId: p.eventId,
      partIndex: p.partIndex,
      splitReason: reason,
      samples,
      startSample: p.startSample,
      preSpeechSamples: p.pre.length,
      speechFrames: p.speechFrames,
      totalFrames: p.frames.length,
      maxProbability: p.maxProbability,
      meanSpeechProbability: p.speechFrames > 0 ? p.speechProbSum / p.speechFrames : null,
    };
  }
}

function defaultId(): string {
  return crypto.randomUUID();
}
