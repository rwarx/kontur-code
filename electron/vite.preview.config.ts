import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

// Preview-only config (used by the Claude Browser preview via dev-preview.cmd).
// Mirrors vite.config.ts, with two tweaks that only matter for running the dev
// server from this machine's project path (which contains spaces, "+", and
// Cyrillic characters):
//   - resolve.preserveSymlinks: keep the ASCII junction path instead of resolving
//     back to the real Cyrillic path.
//   - cacheDir: put the dependency-optimizer output under a pure-ASCII directory.
//     Vite 7.3.6's optimizer crashes ("Cannot read properties of undefined
//     (reading 'imports')") when its esbuild metafile keys live under the
//     special-character project path; an ASCII cacheDir sidesteps the mismatch.
// Not used by the production build (npm run dist).
export default defineConfig({
  root: ".",
  base: "./",
  cacheDir: "C:/Users/NoName/vite-cache",
  plugins: [react(), tailwindcss()],
  resolve: {
    preserveSymlinks: true,
    alias: {
      "@": path.resolve(__dirname, "renderer/src"),
    },
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
