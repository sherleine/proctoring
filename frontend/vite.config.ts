import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

// Environment variables are read from the repository root (.env) so all three
// services share one configuration file. Only the prefixes below reach the
// browser bundle - never add a prefix that matches a secret (HF_TOKEN, LLM_API_KEY).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, "..", "");
  const backendUrl = `http://127.0.0.1:${env.BACKEND_PORT || 8000}`;
  return {
    envDir: "..",
    envPrefix: ["VITE_", "EVENT_", "AUDIO_SAMPLE_RATE", "AUDIO_CHANNELS", "MAX_CLIP_DURATION", "PRE_SPEECH_BUFFER", "SILENCE_DURATION"],
    plugins: [react()],
    server: {
      port: Number(env.FRONTEND_PORT || 5173),
      strictPort: true,
      proxy: {
        // The React app calls relative /api/... URLs; Vite forwards them to FastAPI.
        "/api": { target: backendUrl, changeOrigin: true },
      },
      fs: { allow: [".."] },
    },
    worker: { format: "es" },
    optimizeDeps: { exclude: ["onnxruntime-web"] },
    test: { environment: "node", root: "..", include: ["tests/frontend/**/*.test.ts"] },
  };
});
