"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { Bookmark, Check, ChevronRight, CircleAlert, FileCode, FileStack, FileText, History, Loader2, RotateCw, Sparkles, X } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { computeUnifiedDiff, diffStats } from "@/lib/kontur/diff";
import { isServerMode, syncWorkspaceFiles } from "@/lib/kontur/sync";
import { KcBadge, KcGhostButton, KcToolButton } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";
import { CodeEditor } from "./CodeEditor";
import { HunkReview } from "./HunkReview";
import { AgentChangesReview } from "./AgentChangesReview";
import { relativeTime } from "./helpers";

/* ============================================================
   CODE — editable document viewer with a tab strip.
   Edits flow through setFileContent → (server mode) workspace.write,
   with a live save-status indicator. Checkpoint review: compare the
   open file against any checkpoint snapshot that covers it (LCS
   unified diff), and restore the checkpoint state in one click.
   ============================================================ */

export default function CodeView() {
  const t = useT();
  const files = useKontur((s) => s.files);
  const codeTabs = useKontur((s) => s.codeTabs);
  const activeCodeTab = useKontur((s) => s.activeCodeTab);
  const setActiveCodeTab = useKontur((s) => s.setActiveCodeTab);
  const closeCodeTab = useKontur((s) => s.closeCodeTab);
  const setFileContent = useKontur((s) => s.setFileContent);
  const saveStatus = useKontur((s) => s.saveStatus);
  const pushToast = useKontur((s) => s.pushToast);
  const setSurface = useKontur((s) => s.setSurface);
  const checkpoints = useKontur((s) => s.checkpoints);
  const restoreCheckpoint = useKontur((s) => s.restoreCheckpoint);
  const ghostTextOn = useKontur((s) => s.ui.ghostText);
  const setUi = useKontur((s) => s.setUi);

  /* review state lives in the store — the Ctrl+Shift+K quick jump and the
     command palette can activate a review without mount races (lazy chunk) */
  const reviewId = useKontur((s) => s.codeReviewCheckpointId);
  const setReviewId = useKontur((s) => s.setCodeReviewCheckpoint);

  const [pickerOpen, setPickerOpen] = useState(false);
  const [allReviewId, setAllReviewId] = useState<string | null>(null);
  const pickerRef = useRef<HTMLDivElement>(null);
  const tabsRef = useRef<HTMLDivElement>(null);
  const [tabsOverflow, setTabsOverflow] = useState(false);

  /* tab strip overflow affordance (right-edge fade) */
  useEffect(() => {
    const el = tabsRef.current;
    if (!el) return;
    const check = () => setTabsOverflow(el.scrollWidth - el.clientWidth > 4);
    check();
    const ro = new ResizeObserver(check);
    ro.observe(el);
    return () => ro.disconnect();
  }, [codeTabs.length]);

  const file = activeCodeTab ? (files.find((f) => f.path === activeCodeTab) ?? null) : null;

  /* checkpoints that snapshot the open file, newest first */
  const covering = useMemo(
    () =>
      activeCodeTab
        ? checkpoints
            .filter((c) => c.filesSnapshot.some((snap) => snap.path === activeCodeTab))
            .sort((a, b) => b.createdAt - a.createdAt)
        : [],
    [checkpoints, activeCodeTab],
  );

  const reviewCp = reviewId ? (covering.find((c) => c.id === reviewId) ?? null) : null;
  const snapContent = reviewCp && file ? reviewCp.filesSnapshot.find((s) => s.path === file.path)?.content : undefined;
  /* LCS over <100-line files is sub-millisecond — no memo needed; the
     React Compiler memoizes the component render anyway. */
  const reviewDiff =
    snapContent != null && file ? computeUnifiedDiff(snapContent, file.content) : [];
  const stats = diffStats(reviewDiff);

  /* reset stale selection when the active tab changes — derived, not effected:
     `covering` is scoped to the open file, so a mismatch resolves to null. */

  /* close picker on outside click; Esc exits review */
  useEffect(() => {
    if (!pickerOpen) return;
    const onDown = (e: MouseEvent) => {
      if (pickerRef.current && !pickerRef.current.contains(e.target as Node)) setPickerOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [pickerOpen]);

  useEffect(() => {
    if (!reviewId) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setReviewId(null);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [reviewId]);

  /* note: review requests always arrive with the right tab already open —
     the quick jump calls openCodeTab(firstFileOfCheckpoint) itself, and the
     in-surface picker is scoped to the open file. Derived validity
     (covering[] lookup) therefore never misses. */

  const lines = file ? file.content.replace(/\n$/, "").split("\n").length : 0;

  if (!file) {
    return (
      <div className="flex h-full w-full flex-col items-center justify-center gap-2.5 bg-app p-6 text-center">
        <FileCode size={28} className="text-fg-3" />
        <p className="text-[13px] text-fg-2">{t("code.empty")}</p>
        <KcGhostButton className="mt-1" onClick={() => setSurface("files")}>
          <FileText size={13} />
          {t("code.openFiles")}
        </KcGhostButton>
      </div>
    );
  }

  const slash = file.path.lastIndexOf("/");
  const folder = slash > 0 ? file.path.slice(0, slash) : "";
  const name = file.path.slice(slash + 1);

  return (
    <div className="flex h-full w-full flex-col bg-app">
      {/* ---------------- tab strip ---------------- */}
      <div className="relative flex shrink-0 items-stretch border-b border-line-faint">
        <div
          ref={tabsRef}
          className="flex min-w-0 flex-1 items-stretch overflow-x-auto"
          role="tablist"
          aria-label={t("code.title")}
        >
          {codeTabs.map((path) => {
            const isActive = path === activeCodeTab;
            const tabFile = files.find((f) => f.path === path);
            const tabName = path.slice(path.lastIndexOf("/") + 1);
            return (
              <div
                key={path}
                role="tab"
                tabIndex={0}
                aria-selected={isActive}
                onClick={() => {
                  setActiveCodeTab(path);
                  setReviewId(null);
                }}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    setActiveCodeTab(path);
                    setReviewId(null);
                  }
                }}
                className={cn(
                  "group flex h-[34px] shrink-0 cursor-pointer select-none items-center gap-1.5 border-b-2 px-3 text-[12px] font-medium transition-colors duration-100",
                  isActive
                    ? "border-accent bg-app text-fg-1"
                    : "border-transparent text-fg-2 hover:bg-surface-hover hover:text-fg-1",
                )}
              >
                <FileText size={12} className={cn("shrink-0", tabFile?.modified ? "text-accent" : "text-fg-3")} />
                <span className="max-w-[160px] truncate">{tabName}</span>
                {tabFile?.modified && (
                  <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-label="modified" />
                )}
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    closeCodeTab(path);
                  }}
                  aria-label={t("common.close")}
                  className={cn(
                    "kc-focus-ring flex h-5 w-5 shrink-0 items-center justify-center rounded-xs text-fg-3 transition-all duration-100 hover:bg-surface-hover hover:text-fg-1",
                    isActive ? "opacity-100" : "opacity-0 group-hover:opacity-100 focus-visible:opacity-100",
                  )}
                >
                  <X size={11} />
                </button>
              </div>
            );
          })}
        </div>
        <div className="flex shrink-0 items-center gap-2 border-l border-line-faint pl-2 pr-3">
          <KcToolButton
            onClick={() => setUi({ ghostText: !ghostTextOn })}
            active={ghostTextOn}
            aria-label={ghostTextOn ? t("code.ghost.on") : t("code.ghost.off")}
            title={ghostTextOn ? t("code.ghost.on") : t("code.ghost.off")}
          >
            <Sparkles size={13} />
          </KcToolButton>
          <KcToolButton
            onClick={() => {
              // In server mode re-pull the workspace from disk (the agent may
              // have edited files); offline there is nothing to re-read.
              if (isServerMode()) {
                void syncWorkspaceFiles()
                  .then(() => pushToast(t("code.reloadedToast")))
                  .catch((err) =>
                    pushToast(t("code.reload"), err instanceof Error ? err.message : undefined, "destructive"),
                  );
              } else {
                pushToast(t("code.reloadedToast"));
              }
            }}
            aria-label={t("code.reload")}
            title={t("code.reload")}
          >
            <RotateCw size={13} />
          </KcToolButton>
          <SaveIndicator status={saveStatus[file.path]} t={t} />
        </div>
        {/* fade hinting the strip continues — only when it overflows */}
        {tabsOverflow && (
          <div
            className="pointer-events-none absolute right-[92px] top-0 z-[5] h-full w-8 bg-gradient-to-l from-app to-transparent"
            aria-hidden
          />
        )}
      </div>

      {/* ---------------- breadcrumb + checkpoint compare ---------------- */}
      <div className="flex shrink-0 items-center gap-2 border-b border-line-faint px-4 py-1.5">
        {folder ? (
          <span className="min-w-0 truncate font-mono text-[11px] text-fg-3">{folder}</span>
        ) : null}
        <ChevronRight size={11} className="shrink-0 text-fg-3" aria-hidden />
        <span className="min-w-0 truncate text-[12.5px] font-medium text-fg-1">{name}</span>
        {file.modified && <KcBadge tone="warning">{t("files.modified")}</KcBadge>}

        {/* right cluster: all-changes review · checkpoint compare · line count */}
        <div className="ml-auto flex shrink-0 items-center gap-2">
          {checkpoints.length > 0 && (
            <button
              type="button"
              onClick={() => {
                const newest = [...checkpoints].sort((a, b) => b.createdAt - a.createdAt)[0];
                if (newest) setAllReviewId(newest.id);
              }}
              aria-label={t("code.checkpoint.allChanges")}
              title={t("code.checkpoint.allChanges")}
              className="kc-focus-ring flex h-6 items-center gap-1.5 rounded-xs border border-line bg-surface-2 px-2 text-[11px] font-medium text-fg-2 transition-colors duration-100 hover:border-accent/40 hover:text-accent"
            >
              <FileStack size={11} />
              <span className="hidden sm:inline">{t("code.checkpoint.allChanges")}</span>
            </button>
          )}

          {/* checkpoint compare (scoped to the open file) */}
          {covering.length > 0 && (
            <div ref={pickerRef} className="relative shrink-0">
            <button
              type="button"
              onClick={() => {
                setPickerOpen((v) => !v);
                setReviewId(null);
              }}
              aria-haspopup="listbox"
              aria-expanded={pickerOpen}
              aria-label={t("code.checkpoint.compare")}
              title={t("code.checkpoint.compare")}
              className={cn(
                "kc-focus-ring flex h-6 items-center gap-1.5 rounded-xs border px-2 text-[11px] font-medium transition-colors duration-100",
                pickerOpen || reviewId
                  ? "border-warning/50 bg-warning-soft text-warning"
                  : "border-line bg-surface-2 text-fg-2 hover:border-warning/40 hover:text-warning",
              )}
            >
              <History size={11} />
              <span className="hidden sm:inline">{t("code.checkpoint.compare")}</span>
              <span className="font-mono text-[10px] text-fg-3">{covering.length}</span>
            </button>

            {pickerOpen && (
              <div
                role="listbox"
                aria-label={t("code.checkpoint.pick")}
                className="animate-rise absolute right-0 top-[calc(100%+4px)] z-30 w-[260px] overflow-hidden rounded-md border border-line bg-surface-2 shadow-overlay"
              >
                <div className="border-b border-line-faint px-2.5 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-fg-3">
                  {t("code.checkpoint.pick")}
                </div>
                <div className="max-h-56 overflow-y-auto">
                  {covering.map((cp) => (
                    <button
                      key={cp.id}
                      type="button"
                      role="option"
                      aria-selected={reviewId === cp.id}
                      onClick={() => {
                        setReviewId(cp.id);
                        setPickerOpen(false);
                      }}
                      className="flex w-full items-center gap-2 border-b border-line-faint/60 px-2.5 py-2 text-left transition-colors last:border-b-0 hover:bg-surface-hover"
                    >
                      <Bookmark size={11} className="shrink-0 text-warning" />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-[12px] font-medium text-fg-1">{cp.label}</span>
                        <span className="block truncate font-mono text-[10px] text-fg-3">
                          {relativeTime(cp.createdAt, t)} · {cp.filesSnapshot.length}{" "}
                          {cp.filesSnapshot.length === 1 ? "file" : "files"}
                        </span>
                      </span>
                      <ChevronRight size={11} className="shrink-0 text-fg-3" />
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}

          <span className="shrink-0 font-mono text-[10.5px] text-fg-3">
            {t("code.lineCount", lines)}
          </span>
        </div>
      </div>

      {/* ---------------- document / checkpoint review ---------------- */}
      {reviewCp && snapContent != null ? (
        <div className="flex min-h-0 flex-1 flex-col">
          {/* review header */}
          <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-warning/25 bg-warning-soft/40 px-4 py-2">
            <span className="flex h-5 items-center gap-1.5 rounded-xs bg-warning/10 px-1.5 font-mono text-[9.5px] font-semibold uppercase tracking-wider text-warning">
              <History size={10} />
              {t("code.checkpoint.reviewing")}
            </span>
            <span className="min-w-0 truncate text-[12.5px] font-medium text-fg-1">{reviewCp.label}</span>
            <span className="shrink-0 text-[11px] text-fg-3">
              {t("code.checkpoint.captured", relativeTime(reviewCp.createdAt, t))}
            </span>
            {reviewDiff.length > 0 ? (
              <span className="flex shrink-0 items-center gap-1.5 font-mono text-[11px]">
                <span className="text-success">+{stats.add}</span>
                <span className="text-error">−{stats.del}</span>
                <span className="text-fg-3">{t("code.checkpoint.direction")}</span>
              </span>
            ) : null}
            <div className="ml-auto flex shrink-0 items-center gap-1.5">
              <button
                type="button"
                onClick={() => {
                  restoreCheckpoint(reviewCp.id);
                  setReviewId(null);
                }}
                className="kc-focus-ring flex h-6 items-center gap-1 rounded-xs border border-warning/40 bg-surface-2 px-2 text-[11px] font-medium text-warning transition-colors hover:bg-warning-soft"
                title={t("code.checkpoint.restoreHint")}
              >
                <Bookmark size={11} />
                {t("code.checkpoint.restore")}
              </button>
              <KcToolButton onClick={() => setReviewId(null)} aria-label={t("common.close")} title={t("common.close")}>
                <X size={13} />
              </KcToolButton>
            </div>
          </div>

          {/* diff body */}
          <div className="min-h-0 flex-1 overflow-auto p-4">
            {reviewDiff.length === 0 ? (
              <div className="flex h-full min-h-[240px] flex-col items-center justify-center gap-2.5 rounded-md border border-line-faint bg-surface-1 p-6 text-center">
                <span className="flex h-9 w-9 items-center justify-center rounded-md bg-success-soft text-success">
                  <Check size={16} />
                </span>
                <p className="text-[13px] font-medium text-fg-1">{t("code.checkpoint.noChanges")}</p>
                <p className="max-w-[380px] text-[11.5px] text-fg-3">{t("code.checkpoint.noChangesHint")}</p>
              </div>
            ) : (
              <div className="mx-auto max-w-[860px] space-y-3">
                <p className="font-mono text-[10.5px] text-fg-3">
                  {file.path} · {t("code.checkpoint.direction")}
                </p>
                <HunkReview
                  base={snapContent}
                  current={file.content}
                  acceptLabel={t("code.checkpoint.accept")}
                  rejectLabel={t("code.checkpoint.reject")}
                  onApply={(merged) => {
                    const s = diffStats(computeUnifiedDiff(file.content, merged));
                    setFileContent(file.path, merged);
                    pushToast(t("code.checkpoint.applied", s.add + s.del));
                  }}
                />
              </div>
            )}
          </div>
        </div>
      ) : (
        <div className="min-h-0 flex-1">
          <CodeEditor
            key={file.path}
            path={file.path}
            content={file.content}
            onChange={(next) => setFileContent(file.path, next)}
            ghostText={ghostTextOn}
          />
        </div>
      )}

      {/* unified "all agent changes" review — spans every file in a checkpoint */}
      {allReviewId && (
        <AgentChangesReview initialCheckpointId={allReviewId} onClose={() => setAllReviewId(null)} />
      )}
    </div>
  );
}

/* Live write-through status for the open file. Idle (no pending/complete
   write) renders a neutral "editable" hint rather than the old read-only
   badge, so the surface never lies about what it can do. */
function SaveIndicator({
  status,
  t,
}: {
  status: "saving" | "saved" | "error" | undefined;
  t: ReturnType<typeof useT>;
}) {
  if (status === "saving") {
    return (
      <span className="flex items-center gap-1.5 text-[11px] font-medium text-fg-3">
        <Loader2 size={12} className="animate-spin" />
        {t("code.saving")}
      </span>
    );
  }
  if (status === "saved") {
    return (
      <span className="flex items-center gap-1.5 text-[11px] font-medium text-success">
        <Check size={12} />
        {t("code.saved")}
      </span>
    );
  }
  if (status === "error") {
    return (
      <span className="flex items-center gap-1.5 text-[11px] font-medium text-error">
        <CircleAlert size={12} />
        {t("code.saveError")}
      </span>
    );
  }
  return <KcBadge>{t("code.editable")}</KcBadge>;
}
