// Copies runtime files that the browser loads as static assets (run automatically before dev/build):
//  - the Silero VAD v5 ONNX model (MIT, snakers4/silero-vad; shipped inside @ricky0123/vad-web)
// Served from /models, so the browser downloads it once and caches it. (The ONNX Runtime WASM files are
// bundled by Vite from the onnxruntime-web package.)
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const nm = join(root, "node_modules");
const pub = join(root, "public");

function copy(src, destDir) {
  if (!existsSync(src)) {
    console.error(`[copy-assets] missing ${src} - run "npm install" first`);
    process.exit(1);
  }
  mkdirSync(destDir, { recursive: true });
  copyFileSync(src, join(destDir, src.split(/[\\/]/).pop()));
}

copy(join(nm, "@ricky0123", "vad-web", "dist", "silero_vad_v5.onnx"), join(pub, "models"));
console.log("[copy-assets] Silero VAD model -> public/models");
