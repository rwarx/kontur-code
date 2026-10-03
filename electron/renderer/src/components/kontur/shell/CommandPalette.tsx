"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Bot,
  Bookmark,
  Frame,
  Command,
  Download,
  FileCode2,
  FileDown,
  Files,
  FoldVertical,
  FolderOpen,
  GitFork,
  Keyboard,
  Languages,
  MessageSquare,
  Moon,
  Package,
  PanelLeft,
  PanelRight,
  Play,
  Plus,
  Redo2,
  RefreshCw,
  Settings,
  Sun,
  Undo2,
  Waypoints,
  Workflow,
} from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { graph, workspace } from "@/lib/kontur/backend";
import { exportCurrentSession, isServerMode, reindexCanvas, syncCanvas, syncWorkspaceFiles } from "@/lib/kontur/sync";
import {
  exportSessionJson,
  exportSessionMarkdown,
  exportSessionText,
} from "@/lib/kontur/export";
import type { Surface } from "@/lib/kontur/types";
import { openBundleImport } from "./BundleImport";

interface PaletteCommand {
  id: string;
  group: string;
  label: string;
  hint?: string;
  keywords?: string;
  icon: React.ElementType;
  shortcut?: string[];
  run: () => void;
}

function fuzzyScore(query: string, target: string): number {
  if (!query) return 1;
  const q = query.toLowerCase();
  const t = target.toLowerCase();
  if (t.includes(q)) return 100 - t.indexOf(q);
  let qi = 0;
  let score = 0;
  for (let i = 0; i < t.length && qi < q.length; i++) {
    if (t[i] === q[qi]) {
      score += 1;
      qi++;
    }
  }
  return qi === q.length ? score : 0;
}

export function CommandPalette() {
  const t = useT();
  const paletteOpen = useKontur((s) => s.paletteOpen);
  const setPaletteOpen = useKontur((s) => s.setPaletteOpen);
  const setCheckpointJumpOpen = useKontur((s) => s.setCheckpointJumpOpen);
  const checkpointCount = useKontur((s) => s.checkpoints.length);
  const showCheckpointJump = checkpointCount > 0;
  const setSurface = useKontur((s) => s.setSurface);
  const newSession = useKontur((s) => s.newSession);
  const toggleSidebar = useKontur((s) => s.toggleSidebar);
  const toggleContextPanel = useKontur((s) => s.toggleContextPanel);
  const forkSession = useKontur((s) => s.forkSession);
  const compactSession = useKontur((s) => s.compactSession);
  const pushToast = useKontur((s) => s.pushToast);
  const setUi = useKontur((s) => s.setUi);
  const ui = useKontur((s) => s.ui);
  const currentSessionId = useKontur((s) => s.currentSessionId);
  const runnerStatus = useKontur((s) => s.runner.status);
  const setRunner = useKontur((s) => s.setRunner);
  const sendMessageRef = useRef<((text: string) => void) | null>(null);
  const [query, setQuery] = useState("");
  const [index, setIndex] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (paletteOpen) {
      setQuery("");
      setIndex(0);
      void import("@/lib/kontur/scenario").then((m) => {
        sendMessageRef.current = m.sendMessage;
      });
    }
  }, [paletteOpen]);

  const commands: PaletteCommand[] = useMemo(() => {
    const go = (s: Surface) => () => setSurface(s);
    const canRunDemo = runnerStatus === "idle" || runnerStatus === "done" || runnerStatus === "stopped";
    const cmds: PaletteCommand[] = [
      { id: "new-chat", group: t("sidebar.section.sessions"), label: t("sidebar.newChat"), icon: Plus, shortcut: ["Ctrl", "N"], run: () => newSession() },
      { id: "demo", group: t("sidebar.section.sessions"), label: t("demo.run"), icon: Play, keywords: "agent scenario refresh token", run: () => {
        setRunner({ status: "idle", label: undefined });
        setSurface("chat");
        sendMessageRef.current?.("Users get logged out after about an hour — fix the refresh token renewal");
      } },
      { id: "fork", group: t("sidebar.section.sessions"), label: t("sidebar.fork"), icon: GitFork, keywords: "duplicate copy", run: () => forkSession(currentSessionId) },
      { id: "import-bundle", group: t("sidebar.section.sessions"), label: t("import.palette"), icon: FileDown, keywords: "import restore zip bundle session load open drop", run: () => openBundleImport() },
      { id: "compact", group: t("sidebar.section.sessions"), label: t("context.compact"), icon: FoldVertical, keywords: "tokens fold summarize", run: () => compactSession() },
      { id: "export-md", group: t("export.markdown").split(" as ")[0], label: t("export.markdown"), icon: Download, run: () => { const s = useKontur.getState().sessions.find((x) => x.id === currentSessionId); if (s) exportSessionMarkdown(s); } },
      { id: "export-json", group: t("export.markdown").split(" as ")[0], label: t("export.json"), icon: Download, run: () => { const s = useKontur.getState().sessions.find((x) => x.id === currentSessionId); if (s) exportSessionJson(s); } },
      { id: "export-txt", group: t("export.markdown").split(" as ")[0], label: t("export.text"), icon: Download, run: () => { const s = useKontur.getState().sessions.find((x) => x.id === currentSessionId); if (s) exportSessionText(s); } },
      ...(isServerMode()
        ? [
            {
              id: "open-workspace",
              group: t("sidebar.section.tools"),
              label: t("palette.openWorkspace"),
              icon: FolderOpen,
              keywords: "folder directory project root",
              run: () => {
                void (async () => {
                  if (!window.kontur) return;
                  const dir = await window.kontur.pickFolder();
                  if (!dir) return;
                  try {
                    await workspace.open(dir);
                    await syncWorkspaceFiles().catch(() => undefined);
                    // Walk the new folder so the canvas fills in (empty graph otherwise).
                    await reindexCanvas().catch(() => undefined);
                    pushToast(t("palette.openWorkspace"), dir);
                  } catch (err) {
                    pushToast(t("palette.openWorkspace"), err instanceof Error ? err.message : undefined, "destructive");
                  }
                })();
              },
            },
            {
              id: "reindex",
              group: t("sidebar.section.tools"),
              label: t("palette.reindex"),
              icon: RefreshCw,
              keywords: "graph refresh sync index",
              run: () => {
                void (async () => {
                  try {
                    const res = await graph.reindex();
                    await syncCanvas().catch(() => undefined);
                    pushToast(t("palette.reindex"), res.rejected.length > 0 ? `${res.rejected.length} rejected` : undefined);
                  } catch (err) {
                    pushToast(t("palette.reindex"), err instanceof Error ? err.message : undefined, "destructive");
                  }
                })();
              },
            },
            {
              id: "undo-graph",
              group: t("sidebar.section.tools"),
              label: t("palette.undoGraph"),
              icon: Undo2,
              keywords: "graph revert undo",
              run: () => {
                void graph
                  .undo()
                  .then(() => syncCanvas().catch(() => undefined))
                  .catch((err: unknown) =>
                    pushToast(t("palette.undoGraph"), err instanceof Error ? err.message : undefined, "destructive"),
                  );
              },
            },
            {
              id: "redo-graph",
              group: t("sidebar.section.tools"),
              label: t("palette.redoGraph"),
              icon: Redo2,
              keywords: "graph restore redo",
              run: () => {
                void graph
                  .redo()
                  .then(() => syncCanvas().catch(() => undefined))
                  .catch((err: unknown) =>
                    pushToast(t("palette.redoGraph"), err instanceof Error ? err.message : undefined, "destructive"),
                  );
              },
            },
            {
              id: "export-server",
              group: t("export.markdown").split(" as ")[0],
              label: t("palette.exportServer"),
              icon: Download,
              keywords: "backend transcript export",
              run: () => {
                void (async () => {
                  const text = await exportCurrentSession("markdown");
                  if (text == null) {
                    pushToast(t("palette.exportServer"), undefined, "destructive");
                    return;
                  }
                  const blob = new Blob([text], { type: "text/markdown;charset=utf-8" });
                  const url = URL.createObjectURL(blob);
                  const a = document.createElement("a");
                  a.href = url;
                  a.download = "transcript.md";
                  a.click();
                  setTimeout(() => URL.revokeObjectURL(url), 5000);
                })();
              },
            },
          ]
        : []),
      { id: "go-chat", group: t("sidebar.navigate"), label: t("nav.chat"), icon: MessageSquare, run: go("chat") },
      { id: "go-canvas", group: t("sidebar.navigate"), label: t("nav.canvas"), icon: Frame, shortcut: ["Ctrl", "G"], run: go("canvas") },
      { id: "go-graph", group: t("sidebar.navigate"), label: t("nav.graph"), icon: Waypoints, run: go("graph") },
      { id: "go-files", group: t("sidebar.navigate"), label: t("nav.files"), icon: Files, run: go("files") },
      { id: "go-code", group: t("sidebar.navigate"), label: t("nav.code"), icon: FileCode2, run: go("code") },
      { id: "go-trajectory", group: t("sidebar.navigate"), label: t("nav.trajectory"), icon: Bot, run: go("trajectory") },
      { id: "go-providers", group: t("sidebar.navigate"), label: t("nav.models"), icon: Package, run: go("models") },
      { id: "go-tasks", group: t("sidebar.navigate"), label: t("nav.tasks"), icon: Bot, run: go("tasks") },
      ...(showCheckpointJump
        ? [{ id: "checkpoint-jump", group: t("sidebar.navigate"), label: t("shortcuts.checkpointJump"), icon: Bookmark, shortcut: ["Ctrl", "Shift", "K"], keywords: "checkpoint snapshot restore diff history", run: () => setCheckpointJumpOpen(true) }]
        : []),
      { id: "go-workflows", group: t("sidebar.navigate"), label: t("nav.workflows"), icon: Workflow, keywords: "automation recipes skills run", run: go("workflows") },
      { id: "settings", group: t("sidebar.section.tools"), label: t("settings.title"), icon: Settings, shortcut: ["Ctrl", ","], run: go("settings") },
      { id: "cheatsheet", group: t("sidebar.section.tools"), label: t("cheatsheet.palette"), icon: Keyboard, shortcut: ["?"], keywords: "keyboard shortcuts help keys cheatsheet hotkeys bindings", run: () => useKontur.getState().setCheatsheetOpen(true) },
      { id: "project-export", group: t("sidebar.section.tools"), label: t("project.palette"), icon: Package, shortcut: ["Ctrl", "Shift", "D"], keywords: "download project zip archive source code whole single file export save", run: () => useKontur.getState().setProjectExportOpen(true) },
      { id: "toggle-sidebar", group: t("sidebar.section.tools"), label: t("shortcuts.sidebar"), icon: PanelLeft, shortcut: ["Ctrl", "B"], keywords: "hide show panel", run: () => toggleSidebar() },
      { id: "toggle-inspector", group: t("sidebar.section.tools"), label: t("shortcuts.contextPanel"), icon: PanelRight, shortcut: ["Ctrl", "Alt", "I"], keywords: "inspector right panel", run: () => toggleContextPanel() },
      {
        id: "toggle-theme",
        group: t("sidebar.section.tools"),
        label: ui.theme === "dark" ? t("theme.light") : t("theme.dark"),
        icon: ui.theme === "dark" ? Sun : Moon,
        keywords: "dark light appearance",
        run: () => setUi({ theme: ui.theme === "dark" ? "light" : "dark" }),
      },
      { id: "toggle-locale", group: t("sidebar.section.tools"), label: `${t("language")} · ${ui.locale.toUpperCase()} → ${ui.locale === "en" ? "RU" : ui.locale === "ru" ? "DE" : "EN"}`, icon: Languages, keywords: "language english russian german", run: () => {
        const next = ui.locale === "en" ? "ru" : ui.locale === "ru" ? "de" : "en";
        setUi({ locale: next });
        pushToast(t("language"), next.toUpperCase());
      } },
    ];
    return cmds.filter((c) => c.label);
  }, [t, ui.theme, ui.locale, setSurface, newSession, toggleSidebar, toggleContextPanel, forkSession, compactSession, setUi, pushToast, currentSessionId, runnerStatus, setRunner, showCheckpointJump, setCheckpointJumpOpen]);

  const filtered = useMemo(() => {
    if (!query.trim()) return commands;
    return commands
      .map((c) => ({
        c,
        score: Math.max(
          fuzzyScore(query, c.label),
          fuzzyScore(query, `${c.group} ${c.keywords ?? ""}`) * 0.7,
          fuzzyScore(query, c.id) * 0.85,
        ),
      }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score)
      .map((x) => x.c);
  }, [commands, query]);

  const groups = useMemo(() => {
    const map = new Map<string, PaletteCommand[]>();
    filtered.forEach((c) => {
      const arr = map.get(c.group) ?? [];
      arr.push(c);
      map.set(c.group, arr);
    });
    return [...map.entries()];
  }, [filtered]);

  useEffect(() => {
    setIndex(0);
  }, [query]);

  useEffect(() => {
    const el = listRef.current?.querySelector<HTMLElement>(`[data-index="${index}"]`);
    el?.scrollIntoView({ block: "nearest" });
  }, [index]);

  if (!paletteOpen) return null;

  const flat = filtered;

  const runIndex = (i: number) => {
    const cmd = flat[i];
    if (!cmd) return;
    setPaletteOpen(false);
    cmd.run();
  };

  return (
    <div className="fixed inset-0 z-50" role="dialog" aria-modal="true" aria-label={t("sidebar.commandPalette")}>
      <div className="absolute inset-0 bg-scrim" onClick={() => setPaletteOpen(false)} aria-hidden />
      <div className="absolute left-1/2 top-[10vh] w-[620px] max-w-[calc(100vw-32px)] -translate-x-1/2 animate-settle overflow-hidden rounded-lg border border-line-strong bg-surface-3 shadow-overlay">
        <div className="flex items-center gap-2.5 border-b border-line-faint px-4 py-3">
          <Command size={14} className="shrink-0 text-fg-3" />
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setIndex((i) => Math.min(i + 1, flat.length - 1));
              } else if (e.key === "ArrowUp") {
                e.preventDefault();
                setIndex((i) => Math.max(i - 1, 0));
              } else if (e.key === "Enter") {
                e.preventDefault();
                runIndex(index);
              } else if (e.key === "Escape") {
                setPaletteOpen(false);
              }
            }}
            placeholder={t("palette.placeholder")}
            className="w-full bg-transparent text-[14px] text-fg-1 outline-none placeholder:text-fg-3"
            aria-label={t("palette.placeholder")}
          />
          <span className="kc-keycap">Esc</span>
        </div>
        <div ref={listRef} className="max-h-[380px] overflow-y-auto p-2">
          {flat.length === 0 && (
            <p className="px-3 py-6 text-center text-[12.5px] text-fg-3">{t("palette.noMatches")}</p>
          )}
          {groups.map(([group, items]) => (
            <div key={group} className="mb-1">
              <p className="kc-overline px-2 py-1.5">{group}</p>
              {items.map((c) => {
                const i = flat.indexOf(c);
                const Icon = c.icon;
                return (
                  <button
                    key={c.id}
                    type="button"
                    data-index={i}
                    onMouseEnter={() => setIndex(i)}
                    onClick={() => runIndex(i)}
                    className={`flex w-full items-center gap-2.5 rounded-sm px-2.5 py-[7px] text-left transition-colors ${
                      i === index ? "bg-accent-soft text-accent" : "text-fg-2 hover:bg-surface-hover"
                    }`}
                  >
                    <Icon size={13} className={i === index ? "text-accent" : "text-fg-3"} />
                    <span className={`text-[12.5px] ${i === index ? "font-medium text-accent" : "text-fg-1"}`}>
                      {c.label}
                    </span>
                    {c.shortcut && (
                      <span className="ml-auto flex items-center gap-0.5">
                        {c.shortcut.map((k) => (
                          <span key={k} className="kc-keycap">{k}</span>
                        ))}
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
        <div className="border-t border-line-faint px-4 py-2">
          <p className="text-[10.5px] text-fg-3">{t("palette.hints")}</p>
        </div>
      </div>
    </div>
  );
}
