"use client";

/* ============================================================
   KONTUR CODE — canvas node card.
   Rendered at world coordinates inside the scaled world layer.
   Interaction is delegated to CanvasView via callbacks.
   Nodes may carry a persisted free-form note (annotated card).
   ============================================================ */

import type { PointerEvent as ReactPointerEvent, MouseEvent as ReactMouseEvent } from "react";
import type { CanvasNode } from "@/lib/kontur/types";
import { cn } from "@/lib/utils";
import { KIND_ICONS, kindColorVar, nodeH, nodeW } from "./canvas-utils";

export interface CanvasNodeCardProps {
  node: CanvasNode;
  selected: boolean;
  primary: boolean;
  tool: "select" | "pan";
  editingNote: boolean;
  /** true while a connect gesture drags FROM this node */
  connectingFrom?: boolean;
  /** true while a connect gesture hovers this node as drop target */
  connectTarget?: boolean;
  /** true when the node matches the active canvas search (dashed accent halo) */
  searchHit?: boolean;
  /** true when the canvas search is active and this node does NOT match */
  searchDim?: boolean;
  onPointerDown: (e: ReactPointerEvent<HTMLDivElement>, node: CanvasNode) => void;
  onStartConnect: (e: ReactPointerEvent<HTMLElement>, node: CanvasNode) => void;
  onDoubleClick: (node: CanvasNode) => void;
  onContextMenu: (e: ReactMouseEvent<HTMLDivElement>, node: CanvasNode) => void;
  onEditNote: (node: CanvasNode) => void;
  noteLabel: string;
  noteEditLabel: string;
  connectLabel: string;
}

export default function CanvasNodeCard({
  node,
  selected,
  primary,
  tool,
  editingNote,
  connectingFrom,
  connectTarget,
  searchHit,
  searchDim,
  onPointerDown,
  onStartConnect,
  onDoubleClick,
  onContextMenu,
  onEditNote,
  noteLabel,
  noteEditLabel,
  connectLabel,
}: CanvasNodeCardProps) {
  const Icon = KIND_ICONS[node.kind];
  const color = kindColorVar(node.kind);
  const meta = node.meta ?? node.path ?? null;
  /* path fragments read as mono */
  const monoMeta = Boolean(node.path);
  /* standalone note nodes read as sticky notes — warm paper, dashed edge */
  const isNote = node.kind === "note";

  return (
    <div
      data-node-id={node.id}
      role="button"
      aria-label={node.title + (node.note ? ` · ${noteLabel}` : "")}
      onPointerDown={(e) => onPointerDown(e, node)}
      onDoubleClick={() => onDoubleClick(node)}
      onContextMenu={(e) => onContextMenu(e, node)}
      className={cn(
        "absolute rounded-md border bg-surface-2 transition-colors duration-100",
        !selected && "hover:border-line-strong hover:bg-surface-hover",
        tool === "select" ? "cursor-move" : "cursor-grab",
        connectTarget && "ring-2 ring-accent/50",
      )}
      style={{
        left: node.x,
        top: node.y,
        width: nodeW(node),
        height: nodeH(node),
        borderWidth: selected ? 1.5 : 1,
        borderColor: isNote
          ? selected ? "var(--kc-accent)" : "var(--kc-warning)"
          : selected ? "var(--kc-accent)" : "var(--kc-line)",
        borderStyle: isNote ? "dashed" : "solid",
        background: isNote ? "var(--kc-warning-soft)" : undefined,
        outline: searchHit ? "2px dashed var(--kc-accent)" : undefined,
        outlineOffset: searchHit ? 3 : undefined,
        opacity: searchDim ? 0.35 : 1,
        transition: "opacity 140ms ease, border-color 100ms ease, background-color 100ms ease",
        boxShadow: selected
          ? primary
            ? "0 0 0 1.5px var(--kc-accent), 0 0 18px 2px var(--kc-accent-glow), var(--kc-shadow-subtle)"
            : "0 0 0 1.5px var(--kc-accent-border), 0 0 12px var(--kc-accent-glow), var(--kc-shadow-subtle)"
          : undefined,
        pointerEvents: tool === "pan" ? "none" : "auto",
      }}
    >
      {/* kind strip */}
      <div
        aria-hidden
        className="absolute inset-y-0 left-0 w-[3px] rounded-l-md"
        style={{ background: color }}
      />

      {/* connect handle — right edge, drag to another node to draw an edge */}
      {tool === "select" && selected && !editingNote && (
        <button
          type="button"
          aria-label={connectLabel}
          title={connectLabel}
          onPointerDown={(e) => {
            e.stopPropagation();
            onStartConnect(e, node);
          }}
          className={cn(
            "kc-focus-ring absolute top-1/2 -right-[10px] z-10 flex h-5 w-5 -translate-y-1/2 cursor-crosshair items-center justify-center rounded-full border bg-surface-3 shadow-subtle transition-all duration-100 hover:scale-110 active:scale-95",
            connectingFrom
              ? "border-accent bg-accent-soft text-accent"
              : "border-accent-border/70 text-accent hover:border-accent hover:bg-accent-soft",
          )}
        >
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
            <circle cx="5" cy="12" r="2.2" />
            <path d="M7.5 12h8.5" />
            <path d="M13 8.5 16.5 12 13 15.5" />
          </svg>
        </button>
      )}

      {/* note edit affordance — visible on selection, above content */}
      {tool === "select" && selected && !editingNote && (
        <button
          type="button"
          aria-label={node.note ? noteEditLabel : noteLabel}
          title={node.note ? noteEditLabel : noteLabel}
          onPointerDown={(e) => {
            e.stopPropagation();
          }}
          onClick={(e) => {
            e.stopPropagation();
            onEditNote(node);
          }}
          className={cn(
            "kc-focus-ring absolute -top-2.5 -right-2.5 z-10 flex h-[22px] w-[22px] items-center justify-center rounded-full border bg-surface-3 text-fg-2 shadow-subtle transition-colors duration-100",
            node.note
              ? "border-accent-border text-accent hover:bg-accent-soft hover:text-accent"
              : "border-line hover:border-line-strong hover:text-fg-1",
          )}
        >
          <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
            <path d="M15.5 3.5 3 16 2 21l5-1L19.5 8.5a2.1 2.1 0 0 0-4-5Z" />
          </svg>
        </button>
      )}

      <div className="flex h-full flex-col justify-center gap-[3px] overflow-hidden py-2.5 pl-[15px] pr-3">
        <div className="flex items-center gap-1.5">
          <Icon size={12} strokeWidth={2} className="shrink-0" style={{ color }} aria-hidden />
          <span className="truncate text-[12.5px] font-semibold leading-tight text-fg-1">
            {node.title}
          </span>
        </div>
        {meta && (
          <div
            className={cn(
              "truncate text-[10.5px] leading-tight text-fg-3",
              monoMeta && "font-mono",
            )}
          >
            {meta}
          </div>
        )}
        {node.note && (
          <div className="mt-0.5 border-t border-line-faint pt-1">
            <p className="line-clamp-2 text-[10px] leading-[1.45] text-fg-2">
              {node.note}
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
