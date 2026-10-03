"use client";

import { useMemo } from "react";
import { highlightCodeLine } from "@/components/kontur/chat/Markdown";
import { cn } from "@/lib/utils";

/* ============================================================
   CodePane — read-only code renderer shared by Files (preview)
   and Code (document). Line-number gutter sticks to the left
   edge while the code scrolls horizontally.
   ============================================================ */

export function CodePane({ content, className }: { content: string; className?: string }) {
  const lines = useMemo(() => content.replace(/\n$/, "").split("\n"), [content]);
  return (
    <div className={cn("h-full overflow-auto bg-app", className)} tabIndex={0} role="region">
      <pre className="min-w-max py-2 font-mono text-[12.5px] leading-[1.55] text-fg-1">
        {lines.map((line, i) => (
          <div key={i} className="flex min-h-[1.55em]">
            <span
              aria-hidden
              className="sticky left-0 z-10 w-10 shrink-0 select-none border-r border-line-faint bg-app pr-2 text-right text-[10.5px] leading-[19.4px] text-fg-3"
            >
              {i + 1}
            </span>
            <code className="whitespace-pre pl-3 pr-5">{highlightCodeLine(line, `cp${i}-`)}</code>
          </div>
        ))}
      </pre>
    </div>
  );
}
