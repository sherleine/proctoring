

const SEGMENT_MS = 6000;

export interface VideoClip {
  blob: Blob;
  mimeType: string;
  startedAtEpochMs: number;
}

interface Slot {
  recorder: MediaRecorder | null;
  chunks: Blob[];
  startedAt: number;
  held: boolean;
  timer: ReturnType<typeof setTimeout> | null;
}

export function pickVideoMimeType(): string | null {
  if (typeof MediaRecorder === "undefined") return null;
  return ["video/webm;codecs=vp8", "video/webm;codecs=vp9", "video/webm"].find((t) => MediaRecorder.isTypeSupported(t)) ?? null;
}

export class VideoClipRecorder {
  private readonly slots: Slot[] = [];
  private readonly videoOnly: MediaStream;
  private running = false;

  constructor(stream: MediaStream, readonly mimeType: string) {
    this.videoOnly = new MediaStream(stream.getVideoTracks()); // audio is captured separately as WAV
  }

  start(): void {
    this.running = true;
    for (let i = 0; i < 2; i++) {
      const slot: Slot = { recorder: null, chunks: [], startedAt: 0, held: false, timer: null };
      this.slots.push(slot);
      setTimeout(() => this.restart(slot), (i * SEGMENT_MS) / 2);
    }
  }

  
  beginClip(clipStartEpochMs: number): object | null {
    if (!this.running) return null;
    const candidates = this.slots.filter((s) => s.recorder && !s.held && s.recorder.state === "recording");
    if (candidates.length === 0) return null;
    const covering = candidates.filter((s) => s.startedAt <= clipStartEpochMs).sort((a, b) => b.startedAt - a.startedAt);
    const slot = covering[0] ?? candidates.sort((a, b) => a.startedAt - b.startedAt)[0];
    slot.held = true;
    if (slot.timer) clearTimeout(slot.timer);
    return slot;
  }

  
  async endClip(handle: object | null): Promise<VideoClip | null> {
    const slot = this.slots.find((s) => s === handle);
    if (!slot?.held || !slot.recorder) return null;
    const clip = await this.finish(slot);
    slot.held = false;
    if (this.running) this.restart(slot);
    return clip;
  }

  stop(): void {
    this.running = false;
    for (const slot of this.slots) {
      if (slot.timer) clearTimeout(slot.timer);
      if (slot.recorder && slot.recorder.state !== "inactive") slot.recorder.stop();
      slot.recorder = null;
    }
  }

  private restart(slot: Slot): void {
    if (!this.running || slot.held) return;
    if (slot.recorder && slot.recorder.state !== "inactive") {
      slot.recorder.ondataavailable = null;
      slot.recorder.stop(); 
    }
    if (!this.videoOnly.getVideoTracks().some((t) => t.readyState === "live")) {
      slot.recorder = null;
      return;
    }
    const recorder = new MediaRecorder(this.videoOnly, { mimeType: this.mimeType, videoBitsPerSecond: 600_000 });
    slot.recorder = recorder;
    slot.chunks = [];
    slot.startedAt = Date.now();
    recorder.ondataavailable = (e) => { if (e.data.size > 0) slot.chunks.push(e.data); };
    recorder.onstart = () => (slot.startedAt = Date.now());
    recorder.start(1000);
    slot.timer = setTimeout(() => this.restart(slot), SEGMENT_MS);
  }

  private finish(slot: Slot): Promise<VideoClip | null> {
    const recorder = slot.recorder!;
    if (recorder.state === "inactive") return Promise.resolve(null);
    return new Promise((resolve) => {
      recorder.onstop = () => {
        const blob = new Blob(slot.chunks, { type: this.mimeType });
        resolve(blob.size > 0 ? { blob, mimeType: this.mimeType, startedAtEpochMs: slot.startedAt } : null);
      };
      recorder.stop();
    });
  }
}
