"use client";

/* ============================================================
   Model resolution — id → ModelInfo.
   Server mode replaces the store `providers` with the live
   catalogue, whose ids are composite (`toCompositeModel`,
   e.g. "deepseek/deepseek-chat"). The static data.ts seed uses
   a different id shape, so a live selection never matches there
   and every label falls back to "No model". Resolve against the
   live store catalogue first, then the static seed (demo/offline).
   ============================================================ */

import { useMemo } from "react";
import { useKontur } from "./store";
import { findModel } from "./data";
import type { ModelInfo, Provider } from "./types";

export function resolveModel(providers: Provider[], modelId: string): ModelInfo | undefined {
  if (!modelId) return undefined;
  for (const p of providers) {
    const found = p.models.find((x) => x.id === modelId);
    if (found) return found;
  }
  return findModel(modelId);
}

/** Reactive resolver — re-runs when the catalogue or the id changes. */
export function useModel(modelId: string): ModelInfo | undefined {
  const providers = useKontur((s) => s.providers);
  return useMemo(() => resolveModel(providers, modelId), [providers, modelId]);
}
