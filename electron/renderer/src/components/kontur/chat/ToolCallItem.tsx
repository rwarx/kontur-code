"use client";

import { useState } from "react";
import {
  AlertTriangle,
  Ban,
  Check,
  ChevronDown,
  Loader2,
  Terminal,
} from "lucide-react";
import type { ToolCall } from "@/lib/kontur/types";
import { useT } from "@/lib/kontur/useT";
import { KcBadge } from "@/components/kontur/ui";
import { DiffView } from "./DiffView";

function stateVisual(state: ToolCall["state"]) {
  switch (state) {
    case "running":
      return { icon: Loader2, className: "text-warning animate-spin-slow", label: "tool.running" };
    case "succeeded":
      return { icon: Check, className: "text-success", label: "tool.done" };
    case "failed":
      return { icon: AlertTriangle, className: "text-error", label: "tool.failed" };
    case "denied":
      return { icon: Ban, className: "text-error", label: "tool.denied" };
    default:
      return { icon: Terminal, className: "text-fg-3", label: "tool.waiting" };
  }
}

export function ToolCallItem({ call }: { call: ToolCall }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const { icon: Icon, className, label } = stateVisual(call.state);
  const hasDetail = Boolean(call.body || (call.diff && call.diff.length));

  return (
    <div
      className={`animate-rise relative overflow-hidden rounded-md border border-line-faint bg-surface-1 transition-colors duration-150 ${
        call.state === "running"
          ? "border-l-2 border-l-warning/70"
          : call.state === "failed" || call.state === "denied"
            ? "border-l-2 border-l-error/60"
            : ""
      }`}
    >
      {/* indeterminate progress rail while the tool runs */}
      {call.state === "running" && (
        <span className="pointer-events-none absolute bottom-0 left-0 h-[2px] w-1/4 animate-progress-line rounded-full bg-warning/70" aria-hidden />
      )}
      <button
        type="button"
        onClick={() => hasDetail && setOpen((v) => !v)}
        disabled={!hasDetail}
        aria-expanded={open}
        className={`flex w-full items-center gap-2 px-2.5 py-1.5 text-left transition-colors ${
          hasDetail ? "hover:bg-surface-hover" : "cursor-default"
        }`}
      >
        <Icon size={12} className={`shrink-0 ${className}`} aria-hidden />
        <span className="shrink-0 font-mono text-[11px] text-fg-2">{call.tool}</span>
        {call.risk !== "read" && (
          <span title={t("tool.risk.hint")}>
            <KcBadge tone={call.risk === "execute" ? "warning" : "neutral"}>
              {t(`tool.risk.${call.risk}`)}
            </KcBadge>
          </span>
        )}
        <span className="min-w-0 flex-1 truncate text-[12px] text-fg-1">{call.headline}</span>
        <span
          className={`shrink-0 text-[10.5px] ${
            call.state === "failed" || call.state === "denied"
              ? "text-error"
              : call.state === "running"
                ? "text-warning"
                : call.state === "succeeded"
                ? "text-success"
                : "text-fg-3"
          }`}
        >
          {t(label)}
        </span>
        {call.durationMs !== undefined && (
          <span className="shrink-0 font-mono text-[10px] text-fg-3">
            {(call.durationMs / 1000).toFixed(1)}s
          </span>
        )}
        {hasDetail && (
          <ChevronDown
            size={12}
            className={`shrink-0 text-fg-3 transition-transform duration-150 ${open ? "rotate-180" : ""}`}
            aria-hidden
          />
        )}
      </button>
      {open && hasDetail && (
        <div className="animate-fade border-t border-line-faint p-2">
          {call.diff && call.diff.length > 0 ? (
            <DiffView lines={call.diff} />
          ) : (
            call.body && (
              <pre className="max-h-[220px] overflow-auto whitespace-pre-wrap rounded-sm bg-sunken p-2.5 font-mono text-[11px] leading-[1.55] text-fg-2">
                {call.body}
              </pre>
            )
          )}
        </div>
      )}
    </div>
  );
}
