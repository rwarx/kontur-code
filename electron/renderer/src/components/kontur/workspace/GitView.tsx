"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ArrowUpFromLine,
  Check,
  ChevronDown,
  CircleAlert,
  DownloadCloud,
  FileDiff,
  GitBranch,
  GitCommit as GitCommitIcon,
  GitFork,
  History,
  Loader2,
  Plus,
  RefreshCw,
  Undo2,
} from "lucide-react";
import {
  git,
  type GitBranchInfo,
  type GitCommit,
  type GitFileState,
  type GitResult,
  type GitStatus,
} from "@/lib/kontur/backend";
import { isServerMode, refreshOpenTabs, syncWorkspaceFiles } from "@/lib/kontur/sync";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import type { DiffLine } from "@/lib/kontur/types";
import { DiffView } from "@/components/kontur/chat/DiffView";
import { KcBadge, KcGhostButton, KcPrimaryButton, KcToolButton, Overline } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";

/* ============================================================
   GIT — status / stage / diff / commit / branch + remote ops
   (Tier 1 #6). Self-contained: talks to the `git` backend client
   directly, gated on server mode. After checkout / pull it re-syncs
   the workspace files + open tabs so the editor reflects the new
   tree. Mutations return GitResult (never throw) — both success and
   a rejected push/empty commit surface as a toast.
   ============================================================ */

/* git raw unified diff → DiffLine[] for the shared DiffView. */
function parseGitDiff(raw: string): DiffLine[] {
  const out: DiffLine[] = [];
  for (const line of raw.split("\n")) {
    if (line.startsWith("@@")) out.push({ kind: "header", text: line });
    else if (
      line.startsWith("diff ") ||
      line.startsWith("index ") ||
      line.startsWith("+++") ||
      line.startsWith("---") ||
      line.startsWith("new file") ||
      line.startsWith("deleted file") ||
      line.startsWith("rename ") ||
      line.startsWith("similarity ") ||
      line.startsWith("old mode") ||
      line.startsWith("new mode") ||
      line.startsWith("\\")
    )
      out.push({ kind: "notice", text: line });
    else if (line.startsWith("+")) out.push({ kind: "add", text: line.slice(1) });
    else if (line.startsWith("-")) out.push({ kind: "del", text: line.slice(1) });
    else out.push({ kind: "context", text: line.startsWith(" ") ? line.slice(1) : line });
  }
  while (out.length && out[out.length - 1].kind === "context" && out[out.length - 1].text === "") out.pop();
  return out;
}

type Tone = "neutral" | "accent" | "success" | "warning" | "error";
const STATE_TAG: Record<GitFileState, { letter: string; tone: Tone } | null> = {
  None: null,
  Added: { letter: "A", tone: "success" },
  Modified: { letter: "M", tone: "warning" },
  Deleted: { letter: "D", tone: "error" },
  Renamed: { letter: "R", tone: "accent" },
  Copied: { letter: "C", tone: "accent" },
  Untracked: { letter: "U", tone: "neutral" },
  Conflicted: { letter: "!", tone: "error" },
};

function baseName(path: string): string {
  const i = path.lastIndexOf("/");
  return i >= 0 ? path.slice(i + 1) : path;
}
function dirName(path: string): string {
  const i = path.lastIndexOf("/");
  return i > 0 ? path.slice(0, i) : "";
}

export default function GitView() {
  const t = useT();
  const pushToast = useKontur((s) => s.pushToast);
  const workspaceRoot = useKontur((s) => s.workspaceRoot);

  const [isRepo, setIsRepo] = useState<boolean | null>(null);
  const [status, setStatus] = useState<GitStatus | null>(null);
  const [branches, setBranches] = useState<GitBranchInfo[]>([]);
  const [history, setHistory] = useState<GitCommit[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [diffLines, setDiffLines] = useState<DiffLine[]>([]);
  const [diffLoading, setDiffLoading] = useState(false);

  const [commitMessage, setCommitMessage] = useState("");
  const [newBranch, setNewBranch] = useState("");
  const [branchMenuOpen, setBranchMenuOpen] = useState(false);
  const [creatingBranch, setCreatingBranch] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);

  const serverMode = isServerMode();

  const refresh = useCallback(async () => {
    if (!isServerMode()) return;
    setLoading(true);
    try {
      const repo = await git.repo();
      setIsRepo(repo.isRepository);
      if (!repo.isRepository) {
        setStatus(null);
        setBranches([]);
        setHistory([]);
        return;
      }
      const [st, brs, hist] = await Promise.all([
        git.status(),
        git.branches().catch(() => [] as GitBranchInfo[]),
        git.history(30).catch(() => [] as GitCommit[]),
      ]);
      setStatus(st);
      setBranches(brs);
      setHistory(hist);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh, workspaceRoot]);

  /* diff for the selected file (or the whole worktree) — reloads when the
     selection or the status changes (staging/committing shifts the diff). */
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      if (!isServerMode() || !isRepo) {
        setDiffLines([]);
        return;
      }
      setDiffLoading(true);
      try {
        const d = selectedPath ? await git.fileDiff(selectedPath) : await git.diff();
        if (!cancelled) setDiffLines(parseGitDiff(d.rawDiff));
      } catch {
        if (!cancelled) setDiffLines([]);
      } finally {
        if (!cancelled) setDiffLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [selectedPath, isRepo, status]);

  const current = useMemo(() => branches.find((b) => b.isCurrent) ?? null, [branches]);
  const staged = useMemo(() => (status?.files ?? []).filter((f) => f.staged !== "None"), [status]);
  const unstaged = useMemo(() => (status?.files ?? []).filter((f) => f.unstaged !== "None"), [status]);

  const runOp = useCallback(
    async (label: string, fn: () => Promise<GitResult>, opts?: { syncFiles?: boolean }): Promise<boolean> => {
      setBusy(label);
      let ok = false;
      try {
        const res = await fn();
        ok = res.success;
        if (res.success) {
          pushToast(label, res.output?.trim() || undefined);
          if (opts?.syncFiles) {
            await syncWorkspaceFiles().catch(() => undefined);
            await refreshOpenTabs().catch(() => undefined);
          }
        } else {
          pushToast(label, res.error?.trim() || res.output?.trim() || t("git.failed"), "destructive");
        }
      } catch (e) {
        pushToast(label, e instanceof Error ? e.message : String(e), "destructive");
      } finally {
        setBusy(null);
        await refresh();
      }
      return ok;
    },
    [pushToast, refresh, t],
  );

  if (!serverMode) {
    return (
      <GitGate
        title={t("git.needServer.title")}
        desc={t("git.needServer.desc")}
      />
    );
  }
  if (isRepo === false) {
    return (
      <GitGate
        title={t("git.noRepo.title")}
        desc={t("git.noRepo.desc")}
        action={
          <KcGhostButton onClick={() => void refresh()}>
            <RefreshCw size={13} />
            {t("git.refresh")}
          </KcGhostButton>
        }
      />
    );
  }

  const branchLabel = current?.name ?? status?.branch ?? "—";
  const hasUpstream = !!status?.upstreamBranch;

  return (
    <div className="flex h-full w-full flex-col bg-app">
      {/* ---------------- branch + remote bar ---------------- */}
      <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-line-faint px-3 py-2">
        {/* branch selector */}
        <div className="relative">
          <button
            type="button"
            onClick={() => setBranchMenuOpen((v) => !v)}
            aria-haspopup="listbox"
            aria-expanded={branchMenuOpen}
            className="kc-focus-ring flex h-7 items-center gap-1.5 rounded-sm border border-line bg-surface-2 px-2.5 text-[12px] font-medium text-fg-1 transition-colors hover:border-line-strong"
          >
            <GitBranch size={13} className="text-accent" />
            <span className="max-w-[180px] truncate">{branchLabel}</span>
            {current && (current.ahead > 0 || current.behind > 0) && (
              <span className="flex items-center gap-1 font-mono text-[10px] text-fg-3">
                {current.ahead > 0 && (
                  <span className="flex items-center">
                    <ArrowUp size={9} />
                    {current.ahead}
                  </span>
                )}
                {current.behind > 0 && (
                  <span className="flex items-center">
                    <ArrowDown size={9} />
                    {current.behind}
                  </span>
                )}
              </span>
            )}
            <ChevronDown size={12} className="text-fg-3" />
          </button>
          {branchMenuOpen && (
            <>
              <div className="fixed inset-0 z-20" onClick={() => setBranchMenuOpen(false)} aria-hidden />
              <div
                role="listbox"
                className="animate-rise absolute left-0 top-[calc(100%+4px)] z-30 w-[240px] overflow-hidden rounded-md border border-line bg-surface-2 shadow-overlay"
              >
                <div className="border-b border-line-faint px-2.5 py-1.5">
                  <Overline>{t("git.branches")}</Overline>
                </div>
                <div className="max-h-56 overflow-y-auto">
                  {branches.length === 0 && (
                    <p className="px-2.5 py-2 text-[11.5px] text-fg-3">{t("git.noBranches")}</p>
                  )}
                  {branches.map((b) => (
                    <button
                      key={b.name}
                      type="button"
                      role="option"
                      aria-selected={b.isCurrent}
                      disabled={b.isCurrent || !!busy}
                      onClick={() => {
                        setBranchMenuOpen(false);
                        void runOp(t("git.checkout"), () => git.checkout(b.name), { syncFiles: true });
                      }}
                      className={cn(
                        "flex w-full items-center gap-2 border-b border-line-faint/60 px-2.5 py-2 text-left transition-colors last:border-b-0 disabled:cursor-default",
                        b.isCurrent ? "text-accent" : "text-fg-2 hover:bg-surface-hover hover:text-fg-1",
                      )}
                    >
                      <GitBranch size={11} className={b.isCurrent ? "text-accent" : "text-fg-3"} />
                      <span className="min-w-0 flex-1 truncate text-[12px] font-medium">{b.name}</span>
                      {b.isCurrent && <Check size={12} className="text-accent" />}
                    </button>
                  ))}
                </div>
              </div>
            </>
          )}
        </div>

        {/* create branch */}
        {creatingBranch ? (
          <div className="flex items-center gap-1">
            <input
              autoFocus
              value={newBranch}
              onChange={(e) => setNewBranch(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && newBranch.trim()) {
                  const name = newBranch.trim();
                  setCreatingBranch(false);
                  setNewBranch("");
                  void runOp(t("git.branchCreate"), () => git.createBranch(name), { syncFiles: true });
                }
                if (e.key === "Escape") {
                  setCreatingBranch(false);
                  setNewBranch("");
                }
              }}
              placeholder={t("git.branchNamePlaceholder")}
              className="h-7 w-[160px] rounded-sm border border-accent-border bg-sunken px-2 text-[12px] text-fg-1 outline-none"
            />
            <KcToolButton
              onClick={() => {
                const name = newBranch.trim();
                if (!name) return;
                setCreatingBranch(false);
                setNewBranch("");
                void runOp(t("git.branchCreate"), () => git.createBranch(name), { syncFiles: true });
              }}
              aria-label={t("git.branchCreate")}
            >
              <Check size={14} />
            </KcToolButton>
          </div>
        ) : (
          <KcToolButton onClick={() => setCreatingBranch(true)} aria-label={t("git.branchCreate")} title={t("git.branchCreate")}>
            <GitFork size={14} />
          </KcToolButton>
        )}

        <div className="mx-1 h-5 w-px bg-line-faint" />

        {/* remote ops */}
        <KcGhostButton onClick={() => void runOp(t("git.fetch"), () => git.fetch())} disabled={!!busy} title={t("git.fetchHint")}>
          <DownloadCloud size={13} />
          <span className="hidden md:inline">{t("git.fetch")}</span>
        </KcGhostButton>
        <KcGhostButton
          onClick={() => void runOp(t("git.pull"), () => git.pull(), { syncFiles: true })}
          disabled={!!busy}
          title={t("git.pullHint")}
        >
          <ArrowDownToLine size={13} />
          <span className="hidden md:inline">{t("git.pull")}</span>
          {current && current.behind > 0 && <KcBadge tone="accent">{current.behind}</KcBadge>}
        </KcGhostButton>
        <KcGhostButton
          onClick={() => void runOp(hasUpstream ? t("git.push") : t("git.publish"), () => git.push({ setUpstream: !hasUpstream }))}
          disabled={!!busy}
          title={hasUpstream ? t("git.pushHint") : t("git.publishHint")}
        >
          <ArrowUpFromLine size={13} />
          <span className="hidden md:inline">{hasUpstream ? t("git.push") : t("git.publish")}</span>
          {current && current.ahead > 0 && <KcBadge tone="accent">{current.ahead}</KcBadge>}
        </KcGhostButton>

        <div className="ml-auto flex items-center gap-1.5">
          {busy && <Loader2 size={13} className="animate-spin text-fg-3" />}
          {busy && <span className="text-[11px] text-fg-3">{busy}…</span>}
          <KcToolButton onClick={() => void refresh()} aria-label={t("git.refresh")} title={t("git.refresh")}>
            <RefreshCw size={13} className={loading ? "animate-spin" : ""} />
          </KcToolButton>
        </div>
      </div>

      {error && (
        <div className="flex shrink-0 items-center gap-2 border-b border-error/25 bg-error-soft/40 px-3 py-1.5 text-[11.5px] text-error">
          <CircleAlert size={12} />
          <span className="min-w-0 truncate">{error}</span>
        </div>
      )}

      {/* ---------------- body: changes | diff ---------------- */}
      <div className="flex min-h-0 flex-1">
        {/* left: changes + commit */}
        <div className="flex w-[320px] shrink-0 flex-col border-r border-line-faint">
          <div className="min-h-0 flex-1 overflow-y-auto">
            {status?.isClean ? (
              <div className="flex flex-col items-center justify-center gap-2 px-4 py-10 text-center">
                <span className="flex h-9 w-9 items-center justify-center rounded-md bg-success-soft text-success">
                  <Check size={16} />
                </span>
                <p className="text-[12.5px] font-medium text-fg-1">{t("git.clean")}</p>
              </div>
            ) : (
              <>
                <ChangeGroup
                  title={t("git.staged")}
                  files={staged}
                  which="staged"
                  selectedPath={selectedPath}
                  onSelect={setSelectedPath}
                  busy={!!busy}
                  onAll={staged.length ? () => void runOp(t("git.unstageAll"), () => git.unstage()) : undefined}
                  allLabel={t("git.unstageAll")}
                  fileActionLabel={t("git.unstage")}
                  onFile={(p) => void runOp(t("git.unstage"), () => git.unstage([p]))}
                  fileActionIcon={<Undo2 size={12} />}
                />
                <ChangeGroup
                  title={t("git.changes")}
                  files={unstaged}
                  which="unstaged"
                  selectedPath={selectedPath}
                  onSelect={setSelectedPath}
                  busy={!!busy}
                  onAll={unstaged.length ? () => void runOp(t("git.stageAll"), () => git.stage()) : undefined}
                  allLabel={t("git.stageAll")}
                  fileActionLabel={t("git.stage")}
                  onFile={(p) => void runOp(t("git.stage"), () => git.stage([p]))}
                  fileActionIcon={<Plus size={12} />}
                />
              </>
            )}
          </div>

          {/* commit box */}
          <div className="shrink-0 border-t border-line-faint p-2">
            <textarea
              value={commitMessage}
              onChange={(e) => setCommitMessage(e.target.value)}
              placeholder={t("git.commitPlaceholder")}
              rows={3}
              className="w-full resize-none rounded-sm border border-line bg-sunken px-2 py-1.5 text-[12px] text-fg-1 outline-none placeholder:text-fg-3 focus:border-line-strong"
            />
            <div className="mt-1.5 flex items-center gap-2">
              <span className="font-mono text-[10.5px] text-fg-3">{t("git.stagedCount", staged.length)}</span>
              <KcPrimaryButton
                className="ml-auto"
                disabled={!commitMessage.trim() || staged.length === 0 || !!busy}
                onClick={() => {
                  const msg = commitMessage.trim();
                  void runOp(t("git.commit"), () => git.createCommit(msg)).then((ok) => {
                    if (ok) setCommitMessage("");
                  });
                }}
              >
                <Check size={12} />
                {t("git.commit")}
              </KcPrimaryButton>
            </div>
          </div>
        </div>

        {/* right: diff + history */}
        <div className="flex min-w-0 flex-1 flex-col">
          <div className="flex shrink-0 items-center gap-2 border-b border-line-faint px-3 py-1.5">
            <FileDiff size={12} className="text-fg-3" />
            <button
              type="button"
              onClick={() => setSelectedPath(null)}
              className={cn(
                "kc-focus-ring rounded-xs text-[11.5px] font-medium transition-colors",
                selectedPath ? "text-fg-3 hover:text-accent" : "text-fg-1",
              )}
            >
              {t("git.allChanges")}
            </button>
            {selectedPath && (
              <>
                <span className="text-fg-3">/</span>
                <span className="min-w-0 truncate font-mono text-[11px] text-fg-1">{selectedPath}</span>
              </>
            )}
          </div>
          <div className="min-h-0 flex-1 overflow-auto p-3">
            {diffLoading ? (
              <div className="flex h-full items-center justify-center">
                <Loader2 size={18} className="animate-spin text-fg-3" />
              </div>
            ) : diffLines.length === 0 ? (
              <div className="flex h-full min-h-[160px] flex-col items-center justify-center gap-2 text-center">
                <FileDiff size={22} className="text-fg-3" />
                <p className="text-[12px] text-fg-3">{t("git.noDiff")}</p>
              </div>
            ) : (
              <DiffView lines={diffLines} maxHeight={100000} />
            )}
          </div>

          {/* history */}
          {history.length > 0 && (
            <div className="shrink-0 border-t border-line-faint">
              <div className="flex items-center gap-1.5 px-3 py-1.5">
                <History size={11} className="text-fg-3" />
                <Overline>{t("git.history")}</Overline>
              </div>
              <div className="max-h-[176px] overflow-y-auto px-2 pb-2">
                {history.map((c) => (
                  <div
                    key={c.sha}
                    className="flex items-start gap-2 rounded-xs px-1.5 py-1 hover:bg-surface-hover"
                    title={`${c.author} · ${c.date}`}
                  >
                    <GitCommitIcon size={12} className="mt-0.5 shrink-0 text-fg-3" />
                    <span className="min-w-0 flex-1 truncate text-[11.5px] text-fg-2">{c.message}</span>
                    <span className="shrink-0 font-mono text-[10px] text-fg-3">{c.shortSha}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* --------- change group (staged / unstaged file list) --------- */
function ChangeGroup({
  title,
  files,
  which,
  selectedPath,
  onSelect,
  onAll,
  allLabel,
  onFile,
  fileActionIcon,
  fileActionLabel,
  busy,
}: {
  title: string;
  files: { path: string; staged: GitFileState; unstaged: GitFileState }[];
  which: "staged" | "unstaged";
  selectedPath: string | null;
  onSelect: (p: string) => void;
  onAll?: () => void;
  allLabel: string;
  onFile: (p: string) => void;
  fileActionIcon: React.ReactNode;
  fileActionLabel: string;
  busy: boolean;
}) {
  if (files.length === 0) return null;
  return (
    <div className="border-b border-line-faint/60">
      <div className="flex items-center gap-1.5 px-3 py-1.5">
        <Overline>{title}</Overline>
        <span className="font-mono text-[10px] text-fg-3">{files.length}</span>
        {onAll && (
          <button
            type="button"
            onClick={onAll}
            disabled={busy}
            className="kc-focus-ring ml-auto rounded-xs text-[10.5px] font-medium text-fg-3 transition-colors hover:text-accent disabled:opacity-40"
          >
            {allLabel}
          </button>
        )}
      </div>
      <div className="pb-1">
        {files.map((f) => {
          const state = which === "staged" ? f.staged : f.unstaged;
          const tag = STATE_TAG[state];
          const active = selectedPath === f.path;
          return (
            <div
              key={`${which}:${f.path}`}
              className={cn(
                "group flex h-7 items-center gap-1.5 px-3 transition-colors",
                active ? "bg-accent-soft" : "hover:bg-surface-hover",
              )}
            >
              <button type="button" onClick={() => onSelect(f.path)} className="flex min-w-0 flex-1 items-center gap-1.5 text-left" title={f.path}>
                {tag && (
                  <span
                    className={cn(
                      "flex h-4 w-4 shrink-0 items-center justify-center rounded-xs font-mono text-[9px] font-bold",
                      tag.tone === "success" && "bg-success-soft text-success",
                      tag.tone === "warning" && "bg-warning-soft text-warning",
                      tag.tone === "error" && "bg-error-soft text-error",
                      tag.tone === "accent" && "bg-accent-soft text-accent",
                      tag.tone === "neutral" && "bg-surface-3 text-fg-3",
                    )}
                  >
                    {tag.letter}
                  </span>
                )}
                <span className={cn("shrink-0 truncate text-[12px]", active ? "text-accent" : "text-fg-1")}>{baseName(f.path)}</span>
                {dirName(f.path) && <span className="min-w-0 truncate font-mono text-[10px] text-fg-3">{dirName(f.path)}</span>}
              </button>
              <button
                type="button"
                onClick={() => onFile(f.path)}
                disabled={busy}
                aria-label={fileActionLabel}
                title={fileActionLabel}
                className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs text-fg-3 opacity-0 transition-all hover:bg-surface-3 hover:text-fg-1 group-hover:opacity-100 disabled:opacity-40"
              >
                {fileActionIcon}
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/* --------- gate / empty state --------- */
function GitGate({ title, desc, action }: { title: string; desc: string; action?: React.ReactNode }) {
  return (
    <div className="flex h-full w-full flex-col items-center justify-center gap-2.5 bg-app p-6 text-center">
      <GitBranch size={26} className="text-fg-3" />
      <p className="text-[13px] font-medium text-fg-1">{title}</p>
      <p className="max-w-[420px] text-[11.5px] leading-relaxed text-fg-3">{desc}</p>
      {action}
    </div>
  );
}
