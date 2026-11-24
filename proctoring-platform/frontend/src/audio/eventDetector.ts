// Turns a stream of 16 kHz audio frames + Silero speech probabilities into speech events.
// Pure logic (no browser APIs), so every rule is unit-tested.
//
// State machine (one instance runs for the whole exam; nothing is ever disposed between events):
//
//   IDLE ──(p >= start)──► SPEECH_DETECTED ──(speech >= minSpeech)──► RECORDING ──(silence >= silenceMs)──► FINALIZING ─► IDLE
//              ▲                 │ (gap > candidateGapMs: too short, discarded)            │
//              └─────────────────┘                                                        └─(clip reaches maxEvent)─► part closed,
//                                                                                             RECORDING continues `overlap` earlier
//
// FINALIZING cuts the clip out of the rolling buffer and resets ONLY the event state: the rolling buffer,
// the sample counter and the noise estimate are kept, so the next event can start on the very next frame
// and still has its pre-roll.
//
// Clip = [first speech - preRoll, last speech + tailPad]: pauses shorter than `silenceMs` stay inside one
// event; the silence that ends an event is NOT saved beyond `tailPad` (which protects word endings).

export type DetectorState = "IDLE" | "SPEECH_DETECTED" | "RECORDING" | "FINALIZING";

export interface DetectorConfig {
  sampleRate: number; // 16000
  startThreshold: number; // probability that starts speech
  endThreshold: number; // probability that still counts as speech once started (hysteresis)
  minSpeechMs: number; // a candidate needs this much speech to become an event
  candidateGapMs: number; // silence that discards a candidate (short sounds)
  silenceMs: number; // silence that ends an event
  preRollMs: number; // audio kept before the first speech frame
  tailPadMs: number; // audio kept after the last speech frame
  maxEventMs: number; // longest clip; longer speech is split
  overlapMs: number; // the next part repeats this much audio
  bufferMs: number; // rolling buffer (>= preRoll)
}

export interface DetectedEvent {
  groupId: string; // same for all parts of one continuous speech event
  partIndex: number;
  isFinal: boolean;
  startSample: number; // absolute 16 kHz sample indices since capture start
  endSample: number;
  speechStartSample: number;
  speechEndSample: number;
  samples: Float32Array;
  maxProbability: number;
  speechSeconds: number;
  preEventSeconds: number;
  overlapSeconds: number;
  noiseFloorDbfs: number | null;
}

export type DetectorOutput =
  | { type: "speech-start"; groupId: string; partIndex: number; clipStartSample: number; speechStartSample: number }
  | { type: "event"; event: DetectedEvent };

interface Recording {
  groupId: string;
  partIndex: number;
  clipStart: number;
  speechStart: number;
  lastSpeechEnd: number;
  speechSamples: number;
  maxProbability: number;
  overlapSamples: number;
}

interface Candidate {
  start: number;
  speech: number;
  lastSpeechEnd: number;
  maxProbability: number;
}

export class EventDetector {
  private readonly ring: Float32Array;
  private total = 0; // samples received so far (never reset during the exam)
  private candidate: Candidate | null = null;
  private recording: Recording | null = null;
  private noiseFloor: number | null = null;
  private eventsEmitted = 0;
  private readonly ms: (ms: number) => number;

  constructor(
    private readonly cfg: DetectorConfig,
    private readonly newId: () => string = () => crypto.randomUUID(),
  ) {
    if (cfg.endThreshold > cfg.startThreshold) throw new Error("endThreshold must not exceed startThreshold");
    if (cfg.overlapMs >= cfg.maxEventMs) throw new Error("overlap must be shorter than the maximum event");
    if (cfg.tailPadMs > cfg.silenceMs) throw new Error("tailPad must not exceed the silence that ends an event");
    this.ms = (ms) => Math.round((ms / 1000) * cfg.sampleRate);
    this.ring = new Float32Array(this.ms(Math.max(cfg.bufferMs, cfg.preRollMs) + cfg.maxEventMs + cfg.silenceMs + 1000));
  }

  get state(): DetectorState {
    return this.recording ? "RECORDING" : this.candidate ? "SPEECH_DETECTED" : "IDLE";
  }

  /** True while an event is being recorded (for the UI). */
  get isActive(): boolean {
    return this.recording !== null;
  }

  get samplesSeen(): number {
    return this.total;
  }

  get eventCount(): number {
    return this.eventsEmitted;
  }

  /** Feeds one frame (usually 512 samples) and its speech probability. Never throws for valid input. */
  push(frame: Float32Array, probability: number): DetectorOutput[] {
    const p = Number.isFinite(probability) ? probability : 0;
    const frameStart = this.total;
    this.write(frame);
    const frameEnd = this.total;
    return this.recording ? this.whileRecording(frame, p, frameEnd) : this.whileIdle(frame, p, frameStart, frameEnd);
  }

  /** Ends capture: a recording in progress is finalized; an unconfirmed candidate is dropped. */
  flush(): DetectorOutput[] {
    this.candidate = null;
    const r = this.recording;
    if (!r) return [];
    const end = Math.min(this.total, r.lastSpeechEnd + this.ms(this.cfg.tailPadMs));
    return end - r.clipStart >= this.ms(100) ? [this.finalize(end, true)] : (this.recording = null, []);
  }

  private whileIdle(frame: Float32Array, p: number, frameStart: number, frameEnd: number): DetectorOutput[] {
    if (p < this.cfg.endThreshold) this.trackNoise(frame);
    const c = this.candidate;
    if (!c) {
      if (p >= this.cfg.startThreshold) this.candidate = { start: frameStart, speech: frame.length, lastSpeechEnd: frameEnd, maxProbability: p };
    } else if (p >= this.cfg.endThreshold) {
      c.speech += frame.length;
      c.lastSpeechEnd = frameEnd;
      c.maxProbability = Math.max(c.maxProbability, p);
    } else if (frameEnd - c.lastSpeechEnd > this.ms(this.cfg.candidateGapMs)) {
      this.candidate = null; // SPEECH_DETECTED -> IDLE: too short (cough, click, single syllable)
    }

    const confirmed = this.candidate;
    if (!confirmed || confirmed.speech < this.ms(this.cfg.minSpeechMs)) return [];
    // SPEECH_DETECTED -> RECORDING. Pre-roll comes from the rolling buffer (limited to what was captured).
    const clipStart = Math.max(0, this.total - this.ring.length, confirmed.start - this.ms(this.cfg.preRollMs));
    this.recording = { groupId: this.newId(), partIndex: 0, clipStart, speechStart: confirmed.start, lastSpeechEnd: confirmed.lastSpeechEnd,
      speechSamples: confirmed.speech, maxProbability: confirmed.maxProbability, overlapSamples: 0 };
    this.candidate = null;
    return [{ type: "speech-start", groupId: this.recording.groupId, partIndex: 0, clipStartSample: clipStart, speechStartSample: confirmed.start }];
  }

  private whileRecording(frame: Float32Array, p: number, frameEnd: number): DetectorOutput[] {
    const r = this.recording!;
    if (p >= this.cfg.endThreshold) {
      r.lastSpeechEnd = frameEnd;
      r.speechSamples += frame.length;
      r.maxProbability = Math.max(r.maxProbability, p);
    }
    if (frameEnd - r.lastSpeechEnd >= this.ms(this.cfg.silenceMs)) {
      // RECORDING -> FINALIZING -> IDLE: keep only a short pad of the closing silence.
      return [this.finalize(Math.min(frameEnd, r.lastSpeechEnd + this.ms(this.cfg.tailPadMs)), true)];
    }
    if (frameEnd - r.clipStart >= this.ms(this.cfg.maxEventMs)) {
      // Long speech: close this part and continue in a new one that repeats `overlap` of audio.
      const out = this.finalize(frameEnd, false);
      const overlap = this.ms(this.cfg.overlapMs);
      const next = frameEnd - overlap;
      this.recording = { groupId: r.groupId, partIndex: r.partIndex + 1, clipStart: next, speechStart: next,
        lastSpeechEnd: frameEnd, speechSamples: 0, maxProbability: 0, overlapSamples: overlap };
      return [out, { type: "speech-start", groupId: r.groupId, partIndex: r.partIndex + 1, clipStartSample: next, speechStartSample: next }];
    }
    return [];
  }

  /** FINALIZING: cut the clip and reset only the per-event state. Leaves the detector in IDLE. */
  private finalize(end: number, isFinal: boolean): DetectorOutput {
    const r = this.recording!;
    this.recording = null;
    this.candidate = null;
    this.eventsEmitted++;
    const start = r.clipStart;
    return {
      type: "event",
      event: {
        groupId: r.groupId,
        partIndex: r.partIndex,
        isFinal,
        startSample: start,
        endSample: end,
        speechStartSample: r.speechStart,
        speechEndSample: Math.min(end, r.lastSpeechEnd),
        samples: this.read(start, end),
        maxProbability: round(r.maxProbability, 3),
        speechSeconds: round(r.speechSamples / this.cfg.sampleRate, 3),
        preEventSeconds: round((r.speechStart - start) / this.cfg.sampleRate, 3),
        overlapSeconds: round(r.overlapSamples / this.cfg.sampleRate, 3),
        noiseFloorDbfs: this.noiseFloor === null ? null : round(this.noiseFloor, 1),
      },
    };
  }

  private write(frame: Float32Array): void {
    const n = this.ring.length;
    for (let i = 0; i < frame.length; i++) this.ring[(this.total + i) % n] = frame[i];
    this.total += frame.length;
  }

  private read(start: number, end: number): Float32Array {
    // Cannot happen with the buffer sizing above; clamp instead of throwing so detection never stops.
    const from = Math.max(start, this.total - this.ring.length);
    const out = new Float32Array(Math.max(0, end - from));
    for (let i = 0; i < out.length; i++) out[i] = this.ring[(from + i) % this.ring.length];
    return out;
  }

  private trackNoise(frame: Float32Array): void {
    let sum = 0;
    for (let i = 0; i < frame.length; i++) sum += frame[i] * frame[i];
    const db = 20 * Math.log10(Math.sqrt(sum / frame.length) || 1e-6);
    this.noiseFloor = this.noiseFloor === null ? db : this.noiseFloor * 0.98 + db * 0.02;
  }
}

function round(value: number, digits: number): number {
  const f = 10 ** digits;
  return Math.round(value * f) / f;
}
