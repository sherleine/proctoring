import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true },
  worker: { format: "es" },
  // onnxruntime-web loads its own WASM files at runtime; pre-bundling it breaks that.
  optimizeDeps: { exclude: ["onnxruntime-web"] },
  test: {
    environment: "node",
    include: ["tests/**/*.test.ts"],
    passWithNoTests: true,
  },
});
