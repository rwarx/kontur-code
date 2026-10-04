"use client";

import { useEffect, useRef } from "react";
import { applyAppZoom, useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useToast } from "@/hooks/use-toast";
import { TitleBar } from "./shell/TitleBar";
import { Sidebar } from "./shell/Sidebar";
import { StatusBar } from "./shell/StatusBar";
import { WorkspaceHeader } from "./shell/WorkspaceHeader";
import { ContextPanel } from "./shell/ContextPanel";
import { Onboarding } from "./shell/Onboarding";
import { CommandPalette } from "./shell/CommandPalette";
import { CheckpointQuickJump } from "./shell/CheckpointQuickJump";
import { BundleImport } from "./shell/BundleImport";
import { ProjectExportDialog } from "./shell/ProjectExportDialog";
import { ProjectGate } from "./shell/ProjectGate";
import { ProviderStudio } from "./chat/ProviderStudio";
import { ShortcutCheatsheet } from "./shell/ShortcutCheatsheet";
import { ChatView } from "./chat/ChatView";
import { triggerDictation } from "./chat/Composer";
import { stopGeneration } from "@/lib/kontur/scenario";
import { isServerMode } from "@/lib/kontur/sync";

/* surface router — lazy chunks for the heavy surfaces */
import { lazy, Suspense } from "react";
const CanvasView = lazy(() => import("./canvas/CanvasView"));
const GraphOutlineView = lazy(() => import("./canvas/GraphOutlineView"));
const FilesView = lazy(() => import("./workspace/FilesView"));
const CodeView = lazy(() => import("./workspace/CodeView"));
const GitView = lazy(() => import("./workspace/GitView"));
const ModelsView = lazy(() => import("./settings/ModelsView"));
const TasksView = lazy(() => import("./workspace/TasksView"));
const WorkflowsView = lazy(() => import("./workspace/WorkflowsView"));
const TrajectoryView = lazy(() => import("./workspace/TrajectoryView"));
const SettingsView = lazy(() => import("./settings/SettingsView"));

function SurfaceFallback() {
  return (
    <div className="flex h-full items-center justify-center">
      <span className="h-5 w-5 animate-spin-slow rounded-full border-2 border-line border-t-accent" />
    </div>
  );
}

function ToasterBridge() {
  const toasts = useKontur((s) => s.toasts);
  const dismissToast = useKontur((s) => s.dismissToast);
  const { toast } = useToast();
  const seen = useRef(new Set<number>());

  useEffect(() => {
    for (const t of toasts) {
      if (seen.current.has(t.id)) continue;
      seen.current.add(t.id);
      toast({
        title: t.title,
        description: t.description,
        variant: t.variant === "destructive" ? "destructive" : undefined,
      });
      setTimeout(() => dismissToast(t.id), 5000);
    }
  }, [toasts, toast, dismissToast]);

  return null;
}

export function KonturApp() {
  const theme = useKontur((s) => s.ui.theme);
  const locale = useKontur((s) => s.ui.locale);
  const surface = useKontur((s) => s.surface);
  const workMode = useKontur((s) => s.ui.workMode);
  const workspaceRoot = useKontur((s) => s.workspaceRoot);
  const contextPanelOpen = useKontur((s) => s.ui.contextPanelOpen);
  const appZoom = useKontur((s) => s.ui.appZoom);
  const hydrated = useKontur((s) => s.hydrated);
  const checkpointJumpOpen = useKontur((s) => s.checkpointJumpOpen);
  const setCheckpointJumpOpen = useKontur((s) => s.setCheckpointJumpOpen);
  const cheatsheetOpen = useKontur((s) => s.cheatsheetOpen);
  const setCheatsheetOpen = useKontur((s) => s.setCheatsheetOpen);
  const projectExportOpen = useKontur((s) => s.projectExportOpen);
  const setProjectExportOpen = useKontur((s) => s.setProjectExportOpen);
  const providerStudioOpen = useKontur((s) => s.providerStudioOpen);
  const modeChosen = useKontur((s) => s.ui.modeChosen);
  const t = useT();
  useT(); // subscribe for re-render on locale change

  /* theme class on <html> */
  useEffect(() => {
    const root = document.documentElement;
    root.classList.toggle("light", theme === "light");
    root.style.colorScheme = theme;
  }, [theme]);

  /* interface zoom (app scale) — native in the desktop shell, CSS fallback in
     the browser build. Re-applied on rehydrate since Chromium zoom resets on a
     cold start / reload. */
  useEffect(() => {
    applyAppZoom(appZoom ?? 1);
  }, [appZoom, hydrated]);

  useEffect(() => {
    document.documentElement.lang = locale;
  }, [locale]);

  /* global keyboard layer */
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const s = useKontur.getState();
      const mod = e.ctrlKey || e.metaKey;

      if (e.key === "Escape") {
        if (s.paletteOpen) {
          s.setPaletteOpen(false);
          return;
        }
        if (s.checkpointJumpOpen) {
          s.setCheckpointJumpOpen(false);
          return;
        }
        if (s.cheatsheetOpen) {
          s.setCheatsheetOpen(false);
          return;
        }
        if (s.projectExportOpen) {
          s.setProjectExportOpen(false);
          return;
        }
        if (s.providerStudioOpen) {
          s.setProviderStudioOpen(false);
          return;
        }
        if (s.sidebarOpenMobile) {
          s.setSidebarOpenMobile(false);
          return;
        }
        /* the canvas owns Escape while a local overlay/gesture is open
         *  (kind picker, search, connect/reconnect drag) — its own chain
         *  closes it; that keypress must not stop a running generation */
        if (s.canvasOverlayActive) return;
        const generating = s.runner.status === "running" || s.runner.status === "awaiting-approval";
        if (generating) {
          e.preventDefault();
          stopGeneration();
        }
        return;
      }

      if (!mod) {
        if (e.altKey && e.key === "ArrowLeft") {
          e.preventDefault();
          s.goHistory(-1);
        } else if (e.altKey && e.key === "ArrowRight") {
          e.preventDefault();
          s.goHistory(1);
        } else if (e.key === "?" || (e.key === "/" && e.shiftKey)) {
          /* ? opens the shortcut cheatsheet — unless typing in a field */
          const el = e.target as HTMLElement | null;
          const typing =
            !!el &&
            (el.tagName === "INPUT" ||
              el.tagName === "TEXTAREA" ||
              el.isContentEditable ||
              el.closest?.("[data-note-editor],[role=listbox],[cmdk-root]"));
          if (!typing) {
            e.preventDefault();
            s.setCheatsheetOpen(!s.cheatsheetOpen);
          }
        }
        return;
      }

      const key = e.key.toLowerCase();
      if (e.shiftKey && key === "p") {
        e.preventDefault();
        s.togglePalette();
      } else if (e.shiftKey && key === "k") {
        /* Ctrl+Shift+K — checkpoint quick jump */
        e.preventDefault();
        s.setCheckpointJumpOpen(!s.checkpointJumpOpen);
      } else if (e.shiftKey && key === "d") {
        /* Ctrl+Shift+D — download the whole project */
        e.preventDefault();
        s.setProjectExportOpen(true);
      } else if (key === "k") {
        e.preventDefault();
        s.setPaletteOpen(true);
      } else if (key === "n" && !e.shiftKey) {
        e.preventDefault();
        s.newSession();
      } else if (key === "b") {
        e.preventDefault();
        s.toggleSidebar();
      } else if (key === "g") {
        e.preventDefault();
        s.setSurface("canvas");
      } else if (key === "m") {
        e.preventDefault();
        triggerDictation();
      } else if (key === ",") {
        // Was `", "` with a trailing space, so this branch was unreachable: `e.key` is `","` on
        // its own. The Ctrl+, shortcut advertised in the cheatsheet and in the command palette did
        // nothing at all.
        e.preventDefault();
        s.setSurface("settings");
      } else if (e.altKey && key === "i") {
        e.preventDefault();
        s.toggleContextPanel();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  /* block browser zoom-reload gestures inside the app is not needed; nothing else */

  if (!hydrated) {
    return (
      <div className="flex h-screen w-screen items-center justify-center bg-app">
        <span className="h-6 w-6 animate-spin-slow rounded-full border-2 border-line border-t-accent" />
      </div>
    );
  }

  const surfaces: Record<string, React.ReactNode> = {
    chat: <ChatView />,
    canvas: (
      <Suspense fallback={<SurfaceFallback />}>
        <CanvasView />
      </Suspense>
    ),
    graph: (
      <Suspense fallback={<SurfaceFallback />}>
        <GraphOutlineView />
      </Suspense>
    ),
    files: (
      <Suspense fallback={<SurfaceFallback />}>
        <FilesView />
      </Suspense>
    ),
    code: (
      <Suspense fallback={<SurfaceFallback />}>
        <CodeView />
      </Suspense>
    ),
    git: (
      <Suspense fallback={<SurfaceFallback />}>
        <GitView />
      </Suspense>
    ),
    models: (
      <Suspense fallback={<SurfaceFallback />}>
        <ModelsView />
      </Suspense>
    ),
    tasks: (
      <Suspense fallback={<SurfaceFallback />}>
        <TasksView />
      </Suspense>
    ),
    workflows: (
      <Suspense fallback={<SurfaceFallback />}>
        <WorkflowsView />
      </Suspense>
    ),
    trajectory: (
      <Suspense fallback={<SurfaceFallback />}>
        <TrajectoryView />
      </Suspense>
    ),
    settings: (
      <Suspense fallback={<SurfaceFallback />}>
        <SettingsView />
      </Suspense>
    ),
  };

  /* Code mode is workspace-bound: require a project before any code-mode
     surface renders (Chat & Cowork stay project-free). Global config pages
     (settings/models) stay reachable so providers etc. can be set up first. */
  const needsProject =
    isServerMode() &&
    workMode === "code" &&
    !workspaceRoot &&
    surface !== "settings" &&
    surface !== "models";

  return (
    <div className="flex h-screen w-screen flex-col overflow-hidden bg-app text-fg-1" aria-label={t("app.name")}>
      <TitleBar />
      <div className="flex min-h-0 flex-1">
        <Sidebar />
        <main className="flex min-w-0 flex-1 flex-col">
          <WorkspaceHeader />
          <div className="min-h-0 flex-1">
            {needsProject ? <ProjectGate /> : (surfaces[surface] ?? surfaces.chat)}
          </div>
        </main>
        {/* context panel — docked on xl+, overlay below */}
        {contextPanelOpen && (
          <>
            <aside className="hidden w-[300px] shrink-0 border-l border-line-faint xl:block">
              <ContextPanel />
            </aside>
            <div className="fixed inset-0 z-40 xl:hidden">
              <div
                className="absolute inset-0 bg-scrim"
                onClick={() => useKontur.getState().toggleContextPanel()}
                aria-hidden
              />
              <div className="absolute inset-y-0 right-0 w-[300px] max-w-[85vw] animate-rise border-l border-line-faint bg-surface-1 shadow-overlay">
                <ContextPanel />
              </div>
            </div>
          </>
        )}
      </div>
      <StatusBar />
      <CommandPalette />
      {checkpointJumpOpen && <CheckpointQuickJump onClose={() => setCheckpointJumpOpen(false)} />}
      {cheatsheetOpen && <ShortcutCheatsheet onClose={() => setCheatsheetOpen(false)} />}
      {projectExportOpen && (
        <ProjectExportDialog onClose={() => setProjectExportOpen(false)} />
      )}
      {providerStudioOpen && <ProviderStudio />}
      <BundleImport />
      <ToasterBridge />
      {!modeChosen && <Onboarding />}
    </div>
  );
}
