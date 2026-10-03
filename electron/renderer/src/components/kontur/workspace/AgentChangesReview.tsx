"use client";

import { useEffect, useMemo, useState } from "react";
import { Bookmark, Check, FileStack, FileText, X } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { computeUnifiedDiff, diffStats } from "@/lib/kontur/diff";
import { HunkReview } from "./HunkReview";
import { relativeTime } from "./helpers";
import { cn } from "@/lib/utils";

/* ============================================================
   Unified "all agent changes" screen (Tier 1 #5, part 2).
   A full-surface overlay that reviews EVERY file captured in a
   checkpoint against the current workspace content, each file
   with its own per-hunk accept/reject (HunkReview). The baseline
   checkpoint is switchable. Applying a file writes the merged
   content back through setFileContent (which, in server mode,
   write-throughs to disk via the sync override).
   ============================================================ */

export function AgentChangesReview({
  initialCheckpointId,
  onClose,
}: {
  initialCheckpointId: string;
  onClose: () => void;
}) {
  const t = useT();
  const checkpoints = useKontur((s) => s.checkpoints);
  const files = useKontur((s) => s.files);
  const setFileContent = useKontur((s) => s.setFileContent);
  const restoreCheckpoint = useKontur((s) => s.restoreCheckpoint);
  const pushToast = useKontur((s) => s.pushToast);
  const openCodeTab = useKontur((s) => s.openCodeTab);

  const ordered = useMemo(
    () => [...checkpoints].sort((a, b) => b.createdAt - a.createdAt),
    [checkpoints],
  );
  const [cpId, setCpId] = useState(initialCheckpointId);
  const cp = ordered.find((c) => c.id === cpId) ?? ordered[0] ?? null;

  /* files whose current content differs from the checkpoint snapshot */
  const changed = useMemo(() => {
    if (!cp) return [];
    return cp.filesSnapshot
      .map((snap) => {
        const cur = files.find((f) => f.path === snap.path);
        return { path: snap.path, base: snap.content, current: cur ? cur.content : snap.content };
      })
      .filter((x) => x.base !== x.current);
  }, [cp, files]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  if (!cp) return null;

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-app">
      {/* header */}
      <div className="flex shrink-0 items-center gap-2 border-b border-line px-4 py-2.5">
        <span className="flex h-6 items-center gap-1.5 rounded-xs bg-accent/10 px-2 font-mono text-[10px] font-semibold uppercase tracking-wider text-accent">
          <FileStack size={12} />
          {t("code.checkpoint.allChanges")}
        </span>
        <span className="min-w-0 truncate text-[13px] font-medium text-fg-1">
          {t("code.checkpoint.allChangesTitle", cp.label)}
        </span>
        <span className="shrink-0 font-mono text-[11px] text-fg-3">
          {t("code.checkpoint.filesChanged", changed.length)}
        </span>
        <div className="ml-auto flex shrink-0 items-center gap-1.5">
          <button
            type="button"
            onClick={() => {
              restoreCheckpoint(cp.id);
              onClose();
            }}
            className="kc-focus-ring flex h-7 items-center gap-1 rounded-xs border border-warning/40 bg-surface-2 px-2.5 text-[11.5px] font-medium text-warning transition-colors hover:bg-warning-soft"
            title={t("code.checkpoint.restoreHint")}
          >
            <Bookmark size={12} />
            {t("code.checkpoint.restore")}
          </button>
          <button
            type="button"
            onClick={onClose}
            aria-label={t("common.close")}
            className="kc-focus-ring flex h-7 w-7 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
          >
            <X size={15} />
          </button>
        </div>
      </div>
      {/* checkpoint switcher (only when more than one exists) */}
      {ordered.length > 1 && (
        <div className="flex shrink-0 items-center gap-1.5 overflow-x-auto border-b border-line-faint bg-surface-1 px-4 py-1.5">
          {ordered.map((c) => (
            <button
              key={c.id}
              type="button"
              onClick={() => setCpId(c.id)}
              className={cn(
                "flex h-6 shrink-0 items-center gap-1 rounded-xs border px-2 text-[11px] font-medium transition-colors",
                c.id === cp.id
                  ? "border-accent/50 bg-accent/10 text-accent"
                  : "border-line bg-surface-2 text-fg-2 hover:text-fg-1",
              )}
              title={relativeTime(c.createdAt, t)}
            >
              <Bookmark size={10} />
              <span className="max-w-[160px] truncate">{c.label}</span>
            </button>
          ))}
        </div>
      )}

      {/* body */}
      <div className="min-h-0 flex-1 overflow-auto p-4">
        {changed.length === 0 ? (
          <div className="flex h-full min-h-[240px] flex-col items-center justify-center gap-2.5 text-center">
            <span className="flex h-9 w-9 items-center justify-center rounded-md bg-success-soft text-success">
              <Check size={16} />
            </span>
            <p className="text-[13px] font-medium text-fg-1">{t("code.checkpoint.noFilesChanged")}</p>
          </div>
        ) : (
          <div className="mx-auto max-w-[900px] space-y-4">
            {changed.map((f) => {
              const stats = diffStats(computeUnifiedDiff(f.base, f.current));
              return (
                <div key={f.path} className="overflow-hidden rounded-md border border-line-faint bg-surface-1">
                  <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2/50 px-3 py-2">
                    <FileText size={12} className="shrink-0 text-fg-3" />
                    <button
                      type="button"
                      onClick={() => {
                        openCodeTab(f.path);
                        onClose();
                      }}
                      className="min-w-0 truncate font-mono text-[11.5px] text-fg-1 hover:text-accent hover:underline"
                      title={f.path}
                    >
                      {f.path}
                    </button>
                    <span className="ml-auto flex shrink-0 items-center gap-1.5 font-mono text-[10.5px]">
                      <span className="text-success">+{stats.add}</span>
                      <span className="text-error">−{stats.del}</span>
                    </span>
                  </div>
                  <div className="p-3">
                    <HunkReview
                      base={f.base}
                      current={f.current}
                      compact
                      acceptLabel={t("code.checkpoint.accept")}
                      rejectLabel={t("code.checkpoint.reject")}
                      onApply={(merged) => {
                        const s = diffStats(computeUnifiedDiff(f.current, merged));
                        setFileContent(f.path, merged);
                        pushToast(t("code.checkpoint.applied", s.add + s.del));
                      }}
                    />
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
