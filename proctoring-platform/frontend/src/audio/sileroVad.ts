/**
 * Silero VAD (v5/v6 ONNX) inference via ONNX Runtime Web.
 *
 * Model contract: input [1, 64 + 512] float32 (64 samples of context from the
 * previous frame + the 512-sample frame at 16 kHz), state [2, 1, 128], sr int64.
 * Outputs: output [1, 1] speech probability, stateN [2, 1, 128].
 */
import type * as OrtNs from "onnxruntime-web";

type Ort = typeof OrtNs;

const CONTEXT_SAMPLES = 64;
export const SILERO_FRAME_SAMPLES = 512;

export class SileroVad {
  private state: OrtNs.Tensor;
  private context = new Float32Array(CONTEXT_SAMPLES);
  private readonly input = new Float32Array(CONTEXT_SAMPLES + SILERO_FRAME_SAMPLES);

  private constructor(
    private readonly ort: Ort,
    private readonly session: OrtNs.InferenceSession,
    private readonly sr: OrtNs.Tensor,
  ) {
    this.state = this.zeroState();
  }

  static async create(ort: Ort, modelUrl: string): Promise<SileroVad> {
    const res = await fetch(modelUrl);
    if (!res.ok) throw new Error(`Could not download VAD model (${res.status} ${modelUrl})`);
    const bytes = new Uint8Array(await res.arrayBuffer());
    const session = await ort.InferenceSession.create(bytes, { executionProviders: ["wasm"] });
    const sr = new ort.Tensor("int64", BigInt64Array.from([16000n]), []);
    return new SileroVad(ort, session, sr);
  }

  /** Speech probability (0..1) for one 512-sample frame. Frames must be fed in order. */
  async process(frame: Float32Array): Promise<number> {
    if (frame.length !== SILERO_FRAME_SAMPLES) throw new Error(`VAD expects ${SILERO_FRAME_SAMPLES}-sample frames`);
    this.input.set(this.context, 0);
    this.input.set(frame, CONTEXT_SAMPLES);
    this.context = frame.slice(-CONTEXT_SAMPLES);
    const out = await this.session.run({
      input: new this.ort.Tensor("float32", this.input.slice(), [1, this.input.length]),
      state: this.state,
      sr: this.sr,
    });
    this.state = out.stateN as OrtNs.Tensor;
    return (out.output as OrtNs.Tensor).data[0] as number;
  }

  reset(): void {
    this.state = this.zeroState();
    this.context = new Float32Array(CONTEXT_SAMPLES);
  }

  async release(): Promise<void> {
    await this.session.release();
  }

  private zeroState(): OrtNs.Tensor {
    return new this.ort.Tensor("float32", new Float32Array(2 * 128), [2, 1, 128]);
  }
}
