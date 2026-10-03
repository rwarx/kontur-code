import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import path from "node:path";

export default defineConfig({
  root: ".",
  base: "./",
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "renderer/src"),
    },
  },
  build: {
    outDir: "dist/renderer",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    strictPort: true,
  },
});
