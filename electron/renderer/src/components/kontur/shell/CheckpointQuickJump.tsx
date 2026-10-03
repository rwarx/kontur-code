"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Bookmark, History, Waypoints } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { cn } from "@/lib/utils";

/* ============================================================
   Checkpoint quick jump (Ctrl+Shift+K) — keyboard-first picker
   over every checkpoint. Selecting one opens its first snapshot
   file in the Code surface with checkpoint review active.
   ============================================================ */

export function CheckpointQuickJump({ onClose }: { onClose: () => void }) {
  const t = useT();
  const checkpoints = useKontur((s) => s.checkpoints);
  const openCodeTab = useKontur((s) => s.openCodeTab);
  const setSurface = useKontur((s) => s.setSurface);

  const [query, setQuery] = useState("");
  const [index, setIndex] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  /* newest first */
  const sorted = useMemo(() => [...checkpoints].sort((a, b) => b.createdAt - a.createdAt), [checkpoints]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return sorted;
    return sorted.filter((cp) => cp.label.toLowerCase().includes(q) || cp.filesSnapshot.some((f) => f.path.toLowerCase().includes(q)));
  }, [sorted, query]);

  useEffect(() => {
    const el = listRef.current?.querySelector<HTMLElement>(`[data-index="${index}"]`);
    el?.scrollIntoView({ block: "nearest" });
  }, [index]);

  const onQuery = (q: string) => {
    setQuery(q);
    setIndex(0); /* reset selection with the new list — user event, safe */
  };

  const runIndex = (i: number) => {
    const cp = filtered[i];
    if (!cp) return;
    const firstFile = cp.filesSnapshot[0]?.path;
    if (firstFile) {
      openCodeTab(firstFile);
      setSurface("code");
    }
    /* store-driven review request — survives the lazy CodeView mount */
    useKontur.getState().setCodeReviewCheckpoint(cp.id);
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50" role="dialog" aria-modal="true" aria-label={t("checkpointJump.title")}>
      <div className="absolute inset-0 bg-scrim" onClick={onClose} aria-hidden />
      <div className="absolute left-1/2 top-[12vh] w-[480px] max-w-[calc(100vw-32px)] -translate-x-1/2 animate-settle overflow-hidden rounded-lg border border-line-strong bg-surface-3 shadow-overlay">
        <div className="flex items-center gap-2.5 border-b border-line-faint px-4 py-3">
          <History size={14} className="shrink-0 text-warning" />
          <input
            autoFocus
            value={query}
            onChange={(e) => onQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setIndex((i) => Math.min(i + 1, filtered.length - 1));
              } else if (e.key === "ArrowUp") {
                e.preventDefault();
                setIndex((i) => Math.max(i - 1, 0));
              } else if (e.key === "Enter") {
                e.preventDefault();
                runIndex(index);
              } else if (e.key === "Escape") {
                onClose();
              }
            }}
            placeholder={t("checkpointJump.placeholder")}
            className="w-full bg-transparent text-[14px] text-fg-1 outline-none placeholder:text-fg-3"
            aria-label={t("checkpointJump.placeholder")}
          />
          <span className="kc-keycap">Esc</span>
        </div>

        <div ref={listRef} className="max-h-[340px] overflow-y-auto p-2">
          {filtered.length === 0 ? (
            <p className="px-3 py-6 text-center text-[12.5px] text-fg-3">{t("checkpointJump.empty")}</p>
          ) : (
            filtered.map((cp, i) => (
              <button
                key={cp.id}
                type="button"
                data-index={i}
                onMouseEnter={() => setIndex(i)}
                onClick={() => runIndex(i)}
                className={cn(
                  "kc-focus-ring flex w-full items-center gap-2.5 rounded-sm px-2.5 py-[7px] text-left transition-colors",
                  i === index ? "bg-warning-soft" : "hover:bg-surface-hover",
                )}
              >
                <Bookmark size={13} className={i === index ? "shrink-0 text-warning" : "shrink-0 text-fg-3"} />
                <span className="min-w-0 flex-1">
                  <span className={cn("block truncate text-[12.5px]", i === index ? "font-medium text-fg-1" : "text-fg-1")}>
                    {cp.label}
                  </span>
                  <span className="block truncate font-mono text-[10.5px] text-fg-3">
                    {cp.filesSnapshot.length} {cp.filesSnapshot.length === 1 ? "file" : "files"} · {cp.filesSnapshot[0]?.path.slice(cp.filesSnapshot[0].path.lastIndexOf("/") + 1)}
                    {cp.filesSnapshot.length > 1 ? ` +${cp.filesSnapshot.length - 1}` : ""}
                  </span>
                </span>
                {cp.canvasSnapshot && (
                  <span
                    className="flex shrink-0 items-center gap-1 rounded-xs border border-line-faint bg-sunken px-1.5 py-px font-mono text-[9.5px] text-fg-3"
                    title={t("trajectory.checkpointGraph", cp.canvasSnapshot.nodes.length)}
                  >
                    <Waypoints size={9} aria-hidden />
                    {cp.canvasSnapshot.nodes.length}
                  </span>
                )}
              </button>
            ))
          )}
        </div>

        <div className="flex items-center justify-between border-t border-line-faint px-3 py-1.5">
          <span className="font-mono text-[10px] text-fg-3">Ctrl Shift K</span>
          <span className="text-[10.5px] text-fg-3">{t("checkpointJump.hint")}</span>
        </div>
      </div>
    </div>
  );
}
