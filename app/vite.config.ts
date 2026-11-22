import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Tauri runs the dev server on a fixed port (src-tauri/tauri.conf.json devUrl) and sets TAURI_ENV_* when it builds.
const host = process.env.TAURI_DEV_HOST;

export default defineConfig({
  plugins: [react(), tailwindcss()],
  clearScreen: false,
  server: {
    port: 1420,
    strictPort: true,
    host: host || false,
    hmr: host ? { protocol: "ws", host, port: 1421 } : undefined,
    watch: { ignored: ["**/src-tauri/**"] },
  },
  envPrefix: ["VITE_", "TAURI_ENV_"],
  build: {
    // WebView2 on Windows is Chromium
    target: process.env.TAURI_ENV_PLATFORM === "windows" ? "chrome105" : "es2022",
    minify: !process.env.TAURI_ENV_DEBUG,
    sourcemap: Boolean(process.env.TAURI_ENV_DEBUG),
    // a desktop app loads from disk, one big chunk is fine
    chunkSizeWarningLimit: 2000,
  },
});
