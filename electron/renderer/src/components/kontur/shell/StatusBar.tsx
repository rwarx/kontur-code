"use client";

import { FolderOpen, Keyboard, Link2, Redo2, Sparkles, Undo2 } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useModel } from "@/lib/kontur/useModel";
import { KcToolButton } from "@/components/kontur/ui";

export function StatusBar() {
  const t = useT();
  const runnerStatus = useKontur((s) => s.runner.status);
  const runnerLabel = useKontur((s) => s.runner.label);
  const selectedModelId = useKontur((s) => s.ui.selectedModelId);
  const canvas = useKontur((s) => s.canvas);
  const surface = useKontur((s) => s.surface);
  const setSurface = useKontur((s) => s.setSurface);
  const undoCanvas = useKontur((s) => s.undoCanvas);
  const redoCanvas = useKontur((s) => s.redoCanvas);
  const canUndo = useKontur((s) => s.canvasUndo.length > 0);
  const canRedo = useKontur((s) => s.canvasRedo.length > 0);
  const setCheatsheetOpen = useKontur((s) => s.setCheatsheetOpen);

  /* real open-workspace name from the shared store.workspaceRoot (item 5 single
     source of truth) — falls back to the app name in demo mode / when nothing is
     open (no hardcoded project label). */
  const root = useKontur((s) => s.workspaceRoot);
  const projectName = root ? (root.split(/[/\\]/).filter(Boolean).pop() ?? root) : t("app.name");

  const model = useModel(selectedModelId);
  const generating = runnerStatus === "running" || runnerStatus === "awaiting-approval";
  const spatial = surface === "canvas" || surface === "graph";
  const selectedEdge = canvas.edges.find((e) => e.id === canvas.selectedEdgeId) ?? null;

  return (
    <footer className="flex h-7 shrink-0 items-center gap-3 border-t border-line-faint bg-surface-1 px-3">
      {/* left: workspace */}
      <span className="flex min-w-0 items-center gap-1.5 text-[11px] text-fg-2" title={root ?? undefined}>
        <FolderOpen size={11} className="shrink-0 text-fg-3" />
        <span className="truncate">{projectName}</span>
      </span>

      <span className="hidden items-center gap-1.5 text-[11px] text-fg-3 md:flex">
        <span className="h-2.5 w-px bg-line" />
        <Sparkles size={11} className={generating ? "animate-pulse-dot text-accent" : "text-fg-3"} />
        <span className={generating ? "text-fg-2" : ""}>
          {generating ? (runnerLabel || t("status.generating")) : t("main.ai.idle")}
        </span>
      </span>

      {/* right cluster */}
      <div className="ml-auto flex items-center gap-2.5">
        <span className="hidden max-w-[180px] truncate text-[11px] text-fg-2 md:block" title={model?.id}>
          {model?.name ?? t("status.noModel")}
        </span>

        <KcToolButton
          size={20}
          onClick={() => setCheatsheetOpen(true)}
          aria-label={t("cheatsheet.palette")}
          title={t("cheatsheet.palette") + "  ?"}
          className="hidden sm:flex"
        >
          <Keyboard size={11} />
        </KcToolButton>

        {spatial && (
          <>
            {selectedEdge && (
              <button
                type="button"
                onClick={() => setSurface("canvas")}
                className="kc-focus-ring hidden items-center gap-1 rounded-[3px] border border-line-faint bg-sunken px-1.5 py-px font-mono text-[10px] uppercase tracking-wider text-fg-3 transition-colors duration-100 hover:border-accent-border/60 hover:text-accent sm:flex"
                title={t("status.edgeTitle")}
              >
                <Link2 size={10} aria-hidden />
                {selectedEdge.kind}
              </button>
            )}
            <button
              type="button"
              onClick={() => setSurface("graph")}
              className="kc-focus-ring hidden rounded-[3px] px-1 py-px font-mono text-[10.5px] text-fg-3 transition-colors duration-100 hover:bg-surface-hover hover:text-fg-2 sm:block"
              title={t("status.countsTitle")}
            >
              {t("status.counts", canvas.nodes.length, canvas.edges.length)}
            </button>
            <button
              type="button"
              onClick={() => setSurface("canvas")}
              className="kc-focus-ring rounded-[3px] px-1 py-px font-mono text-[10.5px] text-fg-3 transition-colors duration-100 hover:bg-surface-hover hover:text-fg-2"
              title={t("status.zoomTitle")}
            >
              {t("status.zoom", Math.round(canvas.zoom * 100))}
            </button>
            <span className="flex items-center gap-0.5">
              <KcToolButton size={20} onClick={undoCanvas} disabled={!canUndo} aria-label={t("status.undo")} title={t("status.undo")}>
                <Undo2 size={11} />
              </KcToolButton>
              <KcToolButton size={20} onClick={redoCanvas} disabled={!canRedo} aria-label={t("status.redo")} title={t("status.redo")}>
                <Redo2 size={11} />
              </KcToolButton>
            </span>
          </>
        )}
      </div>
    </footer>
  );
}
