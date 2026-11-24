// AudioWorklet (audio thread): microphone -> mono -> 16 kHz (band-limited resampling) -> 512-sample frames,
// posted to the VAD worker through a MessagePort. The audio thread never blocks on the model.
import { StreamingResampler } from "./resampler";

declare const sampleRate: number;
declare const currentTime: number;
declare function registerProcessor(name: string, ctor: unknown): void;
declare class AudioWorkletProcessor {
  readonly port: MessagePort;
}

const FRAME = 512;
const OUT_RATE = 16_000;

class CaptureProcessor extends AudioWorkletProcessor {
  private readonly resampler = new StreamingResampler(sampleRate, OUT_RATE);
  private target: MessagePort | null = null;
  private frame = new Float32Array(FRAME);
  private filled = 0;
  private started = false;
  private stopped = false;

  constructor() {
    super();
    this.port.onmessage = (e: MessageEvent) => {
      if (e.data?.type === "port") this.target = e.data.port as MessagePort;
      if (e.data?.type === "stop") this.stopped = true;
    };
  }

  process(inputs: Float32Array[][]): boolean {
    if (this.stopped) return false;
    const channels = inputs[0];
    if (!this.target || !channels || channels.length === 0) return true;
    if (!this.started) {
      this.started = true;
      // Audio-clock time of the first captured sample: lets the main thread map sample indices to wall time.
      this.target.postMessage({ type: "started", contextTime: currentTime, inputRate: sampleRate });
    }
    const n = channels[0].length;
    const mono = new Float32Array(n);
    for (const ch of channels) for (let i = 0; i < n; i++) mono[i] += ch[i] / channels.length;
    const out = this.resampler.process(mono);
    for (let i = 0; i < out.length; i++) {
      this.frame[this.filled++] = out[i];
      if (this.filled === FRAME) {
        this.target.postMessage({ type: "frame", samples: this.frame }, [this.frame.buffer]);
        this.frame = new Float32Array(FRAME);
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("capture-processor", CaptureProcessor);
