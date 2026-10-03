"use client";

import { useEffect, useMemo, useState } from "react";
import { Check, CheckCheck, RotateCcw, Undo2 } from "lucide-react";
import { applyHunkSelection, computeHunks, hunkDiffLines } from "@/lib/kontur/diff";
import { DiffView } from "@/components/kontur/chat/DiffView";
import { useT } from "@/lib/kontur/useT";
import { cn } from "@/lib/utils";

/* ============================================================
   Per-hunk accept/reject reviewer (Tier 1 #5).
   Diffs base→current, splits into hunks and lets the user keep
   (accept) or revert (reject) each one independently. "Apply"
   reconstructs the merged file via applyHunkSelection and hands
   it back through onApply — the caller decides where it goes
   (setFileContent / write-through). Shared by the checkpoint
   review and the unified "all agent changes" screen.
   ============================================================ */

export function HunkReview({
  base,
  current,
  onApply,
  acceptLabel,
  rejectLabel,
  compact = false,
}: {
  /** the older text (checkpoint snapshot / original) */
  base: string;
  /** the newer text (live file / AI suggestion) */
  current: string;
  /** receives the reconstructed content when the user applies the selection */
  onApply: (merged: string) => void;
  acceptLabel?: string;
  rejectLabel?: string;
  compact?: boolean;
}) {
  const t = useT();
  const hunks = useMemo(() => computeHunks(base, current), [base, current]);

  /* decision per hunk index — true = keep current (accept),
     false = revert to base (reject). Defaults to all-accepted;
     resets whenever the underlying hunk set changes. */
  const [accepted, setAccepted] = useState<Record<number, boolean>>({});
  useEffect(() => {
    const init: Record<number, boolean> = {};
    for (const h of hunks) init[h.index] = true;
    setAccepted(init);
  }, [hunks]);

  const acceptedSet = useMemo(
    () => new Set(hunks.filter((h) => accepted[h.index] !== false).map((h) => h.index)),
    [hunks, accepted],
  );
  const keptCount = acceptedSet.size;
  const revertedCount = hunks.length - keptCount;
  const merged = useMemo(
    () => applyHunkSelection(base, current, acceptedSet),
    [base, current, acceptedSet],
  );
  const dirty = merged !== current;

  if (hunks.length === 0) return null;

  const setAll = (v: boolean) => {
    const next: Record<number, boolean> = {};
    for (const h of hunks) next[h.index] = v;
    setAccepted(next);
  };

  return (
    <div className="space-y-2.5">
      {/* bulk bar */}
      <div
        className={cn(
          "flex flex-wrap items-center gap-2 rounded-md border border-line-faint bg-surface-1/95 px-2.5 py-1.5 backdrop-blur",
          !compact && "sticky top-0 z-10",
        )}
      >
        <button
          type="button"
          onClick={() => setAll(true)}
          className="kc-focus-ring flex h-6 items-center gap-1 rounded-xs border border-line px-2 text-[11px] font-medium text-fg-2 transition-colors hover:border-success/40 hover:text-success"
        >
          <CheckCheck size={11} />
          {t("code.checkpoint.acceptAll")}
        </button>
        <button
          type="button"
          onClick={() => setAll(false)}
          className="kc-focus-ring flex h-6 items-center gap-1 rounded-xs border border-line px-2 text-[11px] font-medium text-fg-2 transition-colors hover:border-error/40 hover:text-error"
        >
          <RotateCcw size={11} />
          {t("code.checkpoint.rejectAll")}
        </button>
        <span className="ml-auto font-mono text-[10.5px] text-fg-3">
          {t("code.checkpoint.selectionSummary", keptCount, revertedCount)}
        </span>
        <button
          type="button"
          disabled={!dirty}
          onClick={() => onApply(merged)}
          className={cn(
            "kc-focus-ring flex h-6 items-center gap-1 rounded-xs border px-2.5 text-[11px] font-semibold transition-colors",
            dirty
              ? "border-accent/50 bg-accent/10 text-accent hover:bg-accent/20"
              : "cursor-not-allowed border-line-faint text-fg-3/60",
          )}
        >
          <Check size={11} />
          {t("code.checkpoint.apply")}
        </button>
      </div>

      {/* per-hunk cards */}
      {hunks.map((h) => {
        const isAccepted = accepted[h.index] !== false;
        return (
          <div
            key={h.index}
            className={cn(
              "overflow-hidden rounded-md border transition-colors",
              isAccepted ? "border-line-faint" : "border-error/35 bg-error-soft/20",
            )}
          >
            <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2/50 px-2.5 py-1">
              <span className="font-mono text-[10.5px] font-semibold text-fg-2">
                {t("code.checkpoint.hunk", h.index + 1)}
              </span>
              {!isAccepted && (
                <span className="font-mono text-[9.5px] uppercase tracking-wider text-error">
                  {t("code.checkpoint.willRevert")}
                </span>
              )}
              <div className="ml-auto flex items-center gap-1">
                <button
                  type="button"
                  aria-pressed={isAccepted}
                  onClick={() => setAccepted((p) => ({ ...p, [h.index]: true }))}
                  className={cn(
                    "kc-focus-ring flex h-5 items-center gap-1 rounded-xs px-1.5 text-[10.5px] font-medium transition-colors",
                    isAccepted
                      ? "bg-success/15 text-success"
                      : "text-fg-3 hover:bg-surface-hover hover:text-fg-1",
                  )}
                >
                  <Check size={10} />
                  {acceptLabel ?? t("code.checkpoint.accept")}
                </button>
                <button
                  type="button"
                  aria-pressed={!isAccepted}
                  onClick={() => setAccepted((p) => ({ ...p, [h.index]: false }))}
                  className={cn(
                    "kc-focus-ring flex h-5 items-center gap-1 rounded-xs px-1.5 text-[10.5px] font-medium transition-colors",
                    !isAccepted
                      ? "bg-error/15 text-error"
                      : "text-fg-3 hover:bg-surface-hover hover:text-fg-1",
                  )}
                >
                  <Undo2 size={10} />
                  {rejectLabel ?? t("code.checkpoint.reject")}
                </button>
              </div>
            </div>
            <DiffView lines={hunkDiffLines(h)} collapsedDefault={false} maxHeight={compact ? 220 : 460} />
          </div>
        );
      })}
    </div>
  );
}
