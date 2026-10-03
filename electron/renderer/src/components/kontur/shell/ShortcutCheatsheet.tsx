"use client";

import { useEffect } from "react";
import { Keyboard, X } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { Keycap, KcToolButton } from "@/components/kontur/ui";

interface Row {
  keys: string[];
  label: string;
}

interface Group {
  title: string;
  rows: Row[];
}

export function ShortcutCheatsheet({ onClose }: { onClose: () => void }) {
  const t = useT();
  const runnerStatus = useKontur((s) => s.runner.status);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [onClose]);

  const groups: Group[] = [
    {
      title: t("cheatsheet.global"),
      rows: [
        { keys: ["Ctrl", "Shift", "P"], label: t("shortcuts.commandPalette") },
        { keys: ["Ctrl", "N"], label: t("shortcuts.newChat") },
        { keys: ["Ctrl", "B"], label: t("shortcuts.sidebar") },
        { keys: ["Ctrl", "G"], label: t("shortcuts.canvas") },
        { keys: ["Ctrl", ","], label: t("shortcuts.settings") },
        { keys: ["Ctrl", "Alt", "I"], label: t("shortcuts.contextPanel") },
        { keys: ["Ctrl", "M"], label: t("shortcuts.voice") },
        { keys: ["Ctrl", "Shift", "K"], label: t("shortcuts.checkpointJump") },
        { keys: ["Ctrl", "Shift", "D"], label: t("project.cheatsheet") },
        { keys: ["?"], label: t("cheatsheet.openHint") },
      ],
    },
    {
      title: t("cheatsheet.navigation"),
      rows: [
        { keys: ["Alt", "←"], label: t("shortcuts.back") },
        { keys: ["Alt", "→"], label: t("shortcuts.forward") },
        { keys: ["Esc"], label: t("shortcuts.stop") },
      ],
    },
    {
      title: t("cheatsheet.chat"),
      rows: [
        { keys: ["Enter"], label: t("cheatsheet.send") },
        { keys: ["Shift", "Enter"], label: t("cheatsheet.newline") },
        { keys: ["/"], label: t("slash.title") },
        { keys: ["Esc"], label: t("cheatsheet.stopDictation") },
      ],
    },
    {
      title: t("cheatsheet.canvas"),
      rows: [
        { keys: ["Ctrl", "Z"], label: t("cheatsheet.undo") },
        { keys: ["Ctrl", "Y"], label: t("cheatsheet.redo") },
        { keys: ["Del"], label: t("cheatsheet.deleteNodes") },
        { keys: ["+", "−", "0"], label: t("cheatsheet.zoom") },
        { keys: ["Esc"], label: t("cheatsheet.clearSelection") },
        { keys: ["Shift", "drag"], label: t("cheatsheet.connectBulk") },
        { keys: ["click"], label: t("cheatsheet.edgeClick") },
        { keys: ["Ctrl", "F"], label: t("cheatsheet.canvasSearch") },
        { keys: ["drag"], label: t("cheatsheet.reconnect") },
        { keys: ["Alt", "drag"], label: t("cheatsheet.snap") },
        { keys: ["right-click"], label: t("cheatsheet.nodeMenu") },
        { keys: ["right-click", "bg"], label: t("cheatsheet.bgMenu") },
        { keys: ["right-click", "edge"], label: t("cheatsheet.edgeMenu") },
        { keys: ["F2"], label: t("cheatsheet.edgeNote") },
        { keys: ["dbl-click", "edge"], label: t("cheatsheet.edgeNoteDbl") },
        { keys: ["right-click", "multi"], label: t("cheatsheet.align") },
        { keys: ["drag", "rows"], label: t("cheatsheet.outlineMarquee") },
        { keys: ["drag", "rows"], label: t("cheatsheet.outlineEdgeMarquee") },
        { keys: ["legend", "click"], label: t("cheatsheet.legendFilter") },
      ],
    },
  ];

  return (
    <div className="fixed inset-0 z-[75] flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-label={t("cheatsheet.title")}>
      <div className="absolute inset-0 bg-scrim backdrop-blur-[2px]" onClick={onClose} aria-hidden />
      <div className="relative flex max-h-[86vh] w-full max-w-[720px] animate-rise flex-col overflow-hidden rounded-xl border border-line bg-surface-1 shadow-overlay">
        {/* header */}
        <div className="flex shrink-0 items-center gap-3 border-b border-line-faint px-5 py-3.5">
          <div className="flex h-8 w-8 items-center justify-center rounded-md border border-line bg-surface-2">
            <Keyboard size={15} className="text-accent" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-[14px] font-semibold text-fg-1">{t("cheatsheet.title")}</div>
            <div className="text-[11px] text-fg-3">{t("cheatsheet.subtitle")}</div>
          </div>
          <KcToolButton onClick={onClose} aria-label={t("common.close")} title={t("common.close")}>
            <X size={14} />
          </KcToolButton>
        </div>

        {/* groups */}
        <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">
          <div className="grid grid-cols-1 gap-x-6 gap-y-5 sm:grid-cols-2">
            {groups.map((g) => (
              <section key={g.title} aria-label={g.title}>
                <div className="mb-2 font-mono text-[10px] uppercase tracking-[0.12em] text-fg-3">{g.title}</div>
                <ul className="space-y-px">
                  {g.rows.map((r) => (
                    <li
                      key={r.label + r.keys.join()}
                      className="flex items-center justify-between gap-3 rounded-sm px-2 py-[5px] transition-colors duration-100 hover:bg-surface-hover"
                    >
                      <span className="min-w-0 truncate text-[12px] text-fg-2">{r.label}</span>
                      <span className="flex shrink-0 items-center gap-1">
                        {r.keys.map((k) => (
                          <Keycap key={k}>{k}</Keycap>
                        ))}
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            ))}
          </div>

          {/* slash commands hint */}
          <div className="mt-5 rounded-lg border border-line-faint bg-sunken px-3.5 py-3">
            <div className="mb-1.5 flex items-center gap-2">
              <span className="font-mono text-[12px] font-semibold text-accent">/</span>
              <span className="text-[11.5px] font-medium text-fg-2">{t("slash.title")}</span>
            </div>
            <div className="flex flex-wrap gap-1.5">
              {["/plan", "/ask", "/auto", "/full", "/demo", "/compact"].map((cmd) => (
                <span
                  key={cmd}
                  className="rounded-xs border border-line-faint bg-surface-2 px-1.5 py-0.5 font-mono text-[10.5px] text-fg-2"
                >
                  {cmd}
                </span>
              ))}
            </div>
          </div>
        </div>

        {/* footer */}
        <div className="flex shrink-0 items-center justify-between gap-3 border-t border-line-faint bg-surface-1 px-5 py-2.5">
          <span className="text-[10.5px] text-fg-3">
            {runnerStatus === "running" || runnerStatus === "awaiting-approval"
              ? t("cheatsheet.footNoteRunning")
              : t("cheatsheet.footNote")}
          </span>
          <span className="hidden items-center gap-1 text-[10.5px] text-fg-3 sm:flex">
            <Keycap>Esc</Keycap>
            {t("common.close")}
          </span>
        </div>
      </div>
    </div>
  );
}
