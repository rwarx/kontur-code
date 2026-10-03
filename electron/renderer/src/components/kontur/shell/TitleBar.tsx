"use client";

import { useEffect, useRef, useState } from "react";
import {
  ArrowLeft,
  ArrowRight,
  Bot,
  ChevronDown,
  Copy,
  Minus,
  PanelLeftClose,
  PanelLeftOpen,
  PanelRightClose,
  PanelRightOpen,
  Search,
  Sparkles,
  Square,
  X,
} from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useModel } from "@/lib/kontur/useModel";
import { KcToolButton, StatusDot } from "@/components/kontur/ui";
import { ModelPicker } from "@/components/kontur/chat/ModelPicker";

export function TitleBar() {
  const t = useT();
  const surface = useKontur((s) => s.surface);
  const runnerStatus = useKontur((s) => s.runner.status);
  const runnerLabel = useKontur((s) => s.runner.label);
  const ui = useKontur((s) => s.ui);
  const toggleSidebar = useKontur((s) => s.toggleSidebar);
  const toggleContextPanel = useKontur((s) => s.toggleContextPanel);
  const togglePalette = useKontur((s) => s.togglePalette);
  const setSidebarOpenMobile = useKontur((s) => s.setSidebarOpenMobile);
  const goHistory = useKontur((s) => s.goHistory);
  const historyIndex = useKontur((s) => s.historyIndex);
  const historyLen = useKontur((s) => s.history.length);
  const [modelOpen, setModelOpen] = useState(false);
  const modelRef = useRef<HTMLDivElement>(null);

  const model = useModel(ui.selectedModelId);
  const aiWorking = runnerStatus === "running" || runnerStatus === "awaiting-approval";
  const aiStateText = runnerStatus === "awaiting-approval"
    ? t("main.ai.waitingApproval")
    : runnerStatus === "running"
      ? (runnerLabel || t("main.ai.working"))
      : runnerStatus === "done"
        ? t("main.ai.idle")
        : t("main.ai.idle");

  useEffect(() => {
    if (!modelOpen) return;
    const onDown = (e: MouseEvent) => {
      if (modelRef.current && !modelRef.current.contains(e.target as Node)) setModelOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [modelOpen]);

  return (
    <header className="kc-drag grid h-10 shrink-0 grid-cols-[1fr_auto_1fr] items-center gap-1.5 border-b border-line-faint bg-surface-1 px-2">
      {/* left cluster — brand + the persistent AI status pill, moved into the
          corner. The pill never disappears: idle shows a calm muted dot +
          "Idle"; while the agent runs it becomes a white-neon pill whose dot
          (pulse-dot) and glow ring (pulse-ring) beat together at 1.6s. */}
      <div className="flex min-w-0 items-center gap-1">
        <KcToolButton
          onClick={() => setSidebarOpenMobile(true)}
          className="lg:hidden"
          aria-label="Open sidebar"
        >
          <PanelRightOpen size={14} />
        </KcToolButton>
        <KcToolButton onClick={toggleSidebar} active={!ui.sidebarCollapsed} aria-label="Toggle sidebar" className="hidden lg:flex">
          {ui.sidebarCollapsed ? <PanelLeftOpen size={14} /> : <PanelLeftClose size={14} />}
        </KcToolButton>
        <div className="ml-1 flex h-7 w-7 items-center justify-center rounded-sm bg-accent text-on-accent" aria-hidden>
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
            <path d="M4 19V9l8-5 8 5v10" />
            <path d="M9 19v-6h6v6" />
          </svg>
        </div>
        <span className="ml-0.5 shrink-0 text-[13px] font-semibold tracking-[-0.01em] text-fg-1">
          {t("app.name")}
        </span>
        <div
          role="status"
          aria-live="polite"
          className={`ml-1.5 flex h-[26px] shrink-0 items-center gap-2 rounded-sm border px-2.5 ${
            aiWorking ? "border-accent-border/40 bg-sunken" : "border-line bg-sunken/60"
          }`}
        >
          {aiWorking ? (
            <span className="animate-pulse-ring rounded-full" aria-hidden>
              <StatusDot color="accent" pulse size={6} />
            </span>
          ) : (
            <StatusDot color="muted" size={6} />
          )}
          <span
            className={`max-w-[140px] truncate text-[12px] ${
              aiWorking ? "font-medium text-fg-1" : "text-fg-3"
            }`}
          >
            {aiStateText}
          </span>
        </div>
      </div>

      {/* center — history arrows + the command-palette search, centered. */}
      <div className="flex items-center gap-1 justify-self-center">
        <KcToolButton
          size={24}
          onClick={() => goHistory(-1)}
          disabled={historyIndex <= 0}
          aria-label="Back"
          className="hidden sm:flex"
        >
          <ArrowLeft size={13} />
        </KcToolButton>
        <KcToolButton
          size={24}
          onClick={() => goHistory(1)}
          disabled={historyIndex >= historyLen - 1}
          aria-label="Forward"
          className="hidden sm:flex"
        >
          <ArrowRight size={13} />
        </KcToolButton>

        <button
          type="button"
          onClick={togglePalette}
          className="kc-focus-ring group hidden h-[26px] w-[200px] items-center gap-2 rounded-sm border border-line bg-sunken px-2.5 text-fg-3 transition-all duration-150 hover:border-line-strong hover:text-fg-2 active:scale-[0.995] sm:flex lg:w-[240px] xl:w-[280px]"
        >
          <Search size={12} className="shrink-0 transition-colors group-hover:text-fg-2" />
          <span className="truncate text-[12px]">{t("main.searchPlaceholder")}</span>
          <span className="ml-auto flex items-center gap-0.5 opacity-70 transition-opacity duration-150 group-hover:opacity-100">
            <span className="kc-keycap">Ctrl</span>
            <span className="kc-keycap">Shift</span>
            <span className="kc-keycap">P</span>
          </span>
        </button>
        <KcToolButton onClick={togglePalette} aria-label="Command palette" className="sm:hidden">
          <Search size={14} />
        </KcToolButton>
      </div>

      {/* right cluster */}
      <div className="flex min-w-0 items-center gap-1 justify-self-end">
        <KcToolButton onClick={toggleContextPanel} active={ui.contextPanelOpen} aria-label={t("main.inspector")}>
          <PanelRightClose size={14} />
        </KcToolButton>
        <div className="relative" ref={modelRef}>
          <button
            type="button"
            onClick={() => setModelOpen((v) => !v)}
            className="kc-focus-ring flex h-[26px] items-center gap-1.5 rounded-sm px-2 text-[12px] text-fg-2 transition-colors hover:bg-surface-hover hover:text-fg-1"
            aria-label={t("composer.selectModel")}
          >
            {runnerStatus === "running" ? (
              <Sparkles size={13} className="animate-pulse-dot text-accent" />
            ) : (
              <Bot size={13} className="text-fg-3" />
            )}
            <span className="max-w-[150px] truncate font-medium text-fg-1">
              {model?.name ?? t("status.noModel")}
            </span>
            <ChevronDown size={12} className="text-fg-3" />
          </button>
          {modelOpen && (
            <div className="kc-no-drag absolute right-0 top-[calc(100%+6px)] z-50 animate-settle">
              <ModelPicker onClose={() => setModelOpen(false)} />
            </div>
          )}
        </div>
        <WindowControls />
      </div>
    </header>
  );
}

/* Custom window controls for the frameless Electron shell. Renders nothing in
   the plain browser build (where `window.kontur?.win` is undefined), so the
   OS-less chrome only appears inside the real desktop app. */
function WindowControls() {
  const win = typeof window !== "undefined" ? window.kontur?.win : undefined;
  const [maximized, setMaximized] = useState(false);

  useEffect(() => {
    if (!win) return;
    let active = true;
    win.isMaximized().then((v) => active && setMaximized(v)).catch(() => {});
    const off = win.onMaximizedChanged((v) => active && setMaximized(v));
    return () => {
      active = false;
      off?.();
    };
  }, [win]);

  if (!win) return null;

  return (
    <div className="kc-no-drag ml-1 flex items-center">
      <button
        type="button"
        onClick={() => win.minimize()}
        aria-label="Minimize window"
        className="kc-focus-ring flex h-7 w-11 items-center justify-center rounded-sm text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
      >
        <Minus size={15} />
      </button>
      <button
        type="button"
        onClick={() => win.maximizeToggle()}
        aria-label={maximized ? "Restore window" : "Maximize window"}
        className="kc-focus-ring flex h-7 w-11 items-center justify-center rounded-sm text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
      >
        {maximized ? <Copy size={12} /> : <Square size={12} />}
      </button>
      <button
        type="button"
        onClick={() => win.close()}
        aria-label="Close window"
        className="kc-focus-ring flex h-7 w-11 items-center justify-center rounded-sm text-fg-3 transition-colors hover:bg-error hover:text-white"
      >
        <X size={16} />
      </button>
    </div>
  );
}
