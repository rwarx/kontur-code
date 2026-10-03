"use client";

import { useState } from "react";
import { ChevronDown } from "lucide-react";
import type { DiffLine } from "@/lib/kontur/types";

export function DiffView({
  lines,
  collapsedDefault = false,
  maxHeight = 260,
}: {
  lines: DiffLine[];
  collapsedDefault?: boolean;
  maxHeight?: number;
}) {
  const [collapsed, setCollapsed] = useState(collapsedDefault);
  const additions = lines.filter((l) => l.kind === "add").length;
  const deletions = lines.filter((l) => l.kind === "del").length;

  return (
    <div className="overflow-hidden rounded-md border border-line-faint bg-sunken">
      <div className="flex items-center gap-2 border-b border-line-faint px-2.5 py-1.5">
        <span className="font-mono text-[10.5px] text-fg-3">
          unified diff
        </span>
        <span className="ml-auto flex items-center gap-1.5 font-mono text-[10.5px]">
          <span className="text-success">+{additions}</span>
          <span className="text-error">−{deletions}</span>
        </span>
        <button
          type="button"
          onClick={() => setCollapsed((v) => !v)}
          className="flex h-5 w-5 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
          aria-label={collapsed ? "Expand diff" : "Collapse diff"}
          aria-expanded={!collapsed}
        >
          <ChevronDown size={11} className={`transition-transform duration-150 ${collapsed ? "" : "rotate-180"}`} />
        </button>
      </div>
      {!collapsed && (
        <div className="overflow-auto" style={{ maxHeight }}>
          <table className="w-full border-collapse font-mono text-[11.5px] leading-[1.6]">
            <tbody>
              {lines.map((line, i) => {
                if (line.kind === "header") {
                  return (
                    <tr key={i} className="bg-surface-2/60">
                      <td colSpan={3} className="px-2.5 py-1 text-[10.5px] font-semibold text-fg-2">
                        {line.text}
                      </td>
                    </tr>
                  );
                }
                const prefix =
                  line.kind === "add" ? "+" : line.kind === "del" ? "−" : " ";
                const cls =
                  line.kind === "add" ? "kc-diff-add" : line.kind === "del" ? "kc-diff-del" : "";
                return (
                  <tr key={i} className={cls}>
                    <td className="w-8 select-none pr-2 text-right text-fg-3/50">{i + 1}</td>
                    <td className="w-4 select-none text-center text-fg-3/70">{prefix}</td>
                    <td className="whitespace-pre pr-3">{line.text || " "}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
