// Copies runtime assets that must be served as static files:
//  - Silero VAD ONNX model (shipped inside @ricky0123/vad-web, MIT-licensed model from snakers4/silero-vad)
//  - ONNX Runtime Web WASM binaries + their JS loaders
// Run automatically before `npm run dev` / `npm run build`.
import { copyFileSync, existsSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const nm = join(root, "node_modules");
const pub = join(root, "public");

function copy(src, destDir) {
  if (!existsSync(src)) {
    console.error(`[copy-assets] missing ${src} - did you run "npm install"?`);
    process.exit(1);
  }
  mkdirSync(destDir, { recursive: true });
  const dest = join(destDir, src.split(/[\\/]/).pop());
  copyFileSync(src, dest);
  return dest;
}

const vadDist = join(nm, "@ricky0123", "vad-web", "dist");
for (const model of ["silero_vad_v5.onnx", "silero_vad_v6.onnx"]) {
  copy(join(vadDist, model), join(pub, "models"));
}

const ortDist = join(nm, "onnxruntime-web", "dist");
// Only the plain (non-JSEP/WebGPU) SIMD build is needed: VAD runs on the CPU via WASM.
const ortFiles = readdirSync(ortDist).filter((f) => /^ort-wasm-simd-threaded\.(wasm|mjs)$/.test(f));
for (const f of ortFiles) copy(join(ortDist, f), join(pub, "ort"));

console.log(`[copy-assets] copied Silero models + ${ortFiles.length} ORT runtime files to public/`);
