/* ============================================================
   Work-mode → surface map. The top-level Chat / Cowork / Code
   switch scopes which workspace surfaces exist:
     chat   — a plain LLM chat (like any browser chat), no tools;
     cowork — an agent for documents / text / business analysis;
     code   — an agent at the code level, folding in the whole
              dev workspace (canvas, graph, files, code, trajectory).
   Tasks live in every mode. The app-level surfaces (marketplace,
   automations, settings) are always reachable from the rail/footer,
   so they are intentionally NOT listed here.
   ============================================================ */

import type { Surface, WorkMode } from "./types";

/* WORKSPACE nav shown in the sidebar for each mode (in display order). */
export const MODE_SURFACES: Record<WorkMode, Surface[]> = {
  chat: ["chat", "tasks"],
  cowork: ["chat", "files", "tasks"],
  code: ["chat", "canvas", "graph", "files", "code", "git", "trajectory", "tasks"],
};

/* where a mode lands when you switch into it */
export const modeDefaultSurface: Record<WorkMode, Surface> = {
  chat: "chat",
  cowork: "chat",
  code: "code",
};

/* app-level surfaces reachable from the rail/footer in every mode */
const ALWAYS_REACHABLE: Surface[] = ["models", "workflows", "settings"];

export function surfaceAllowedInMode(surface: Surface, mode: WorkMode): boolean {
  return ALWAYS_REACHABLE.includes(surface) || MODE_SURFACES[mode].includes(surface);
}
