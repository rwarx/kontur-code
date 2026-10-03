"use client";

/* ============================================================
   KONTUR CODE — Graph outline surface.
   Flat, grouped, filterable index of the canvas graph. Clicking a
   row selects the node on the canvas and centers the view on it
   (via the `kontur:focus-node` window event). Dragging across rows
   multi-selects the nodes (list marquee → canvas selection); the
   selection is shared with the canvas surface.
   ============================================================ */

import { useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from "react";
import { ChevronRight, Crosshair, Link2, StickyNote, Trash2, X } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import type { CanvasEdge, CanvasNode, NodeKind } from "@/lib/kontur/types";
import { KcToolButton, Overline } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";
import { ALL_EDGE_KINDS, EDGE_KIND_ICONS, KIND_ICONS, KIND_ORDER, dispatchFocusNode, kindColorVar } from "./canvas-utils";

export default function GraphOutlineView() {
  const t = useT();
  const nodes = useKontur((s) => s.canvas.nodes);
  const edges = useKontur((s) => s.canvas.edges);
  const primaryId = useKontur((s) => s.canvas.primaryId);
  const selectedIds = useKontur((s) => s.canvas.selectedIds);
  const selectedEdgeId = useKontur((s) => s.canvas.selectedEdgeId);
  const selectedEdgeIds = useKontur((s) => s.canvas.selectedEdgeIds);
  const hiddenEdgeKinds = useKontur((s) => s.canvas.hiddenEdgeKinds);
  const [query, setQuery] = useState("");
  const [showEdges, setShowEdges] = useState(true);

  /* list marquee — pointer down on a row, drag across rows, release:
   *  every row crossed joins the selection (primary = anchor).
   *  Node rows select nodes; edge rows bulk-select connections.
   *  A tiny 3px threshold keeps plain clicks single-select. */
  const listRef = useRef<HTMLDivElement | null>(null);
  const marqueeRef = useRef<{
    rowType: "node" | "edge";
    anchorId: string;
    startY: number;
    moved: boolean;
    additive: boolean;
  } | null>(null);
  const suppressClickRef = useRef(false);

  const nodeTitle = (id: string): string =>
    nodes.find((n) => n.id === id)?.title ?? id;

  const q = query.trim().toLowerCase();
  const hiddenKindSet = new Set(hiddenEdgeKinds);
  const matches = nodes.filter(
    (n) =>
      !q ||
      n.title.toLowerCase().includes(q) ||
      (n.meta ?? "").toLowerCase().includes(q) ||
      (n.path ?? "").toLowerCase().includes(q) ||
      t(`kind.${n.kind}`).toLowerCase().includes(q),
  );
  /* connections index — canvas-hidden kinds stay listed (view state belongs
   *  to the canvas), but only when a query needs them for matching */
  const edgeMatches = edges.filter(
    (e) =>
      !q ||
      nodeTitle(e.from).toLowerCase().includes(q) ||
      nodeTitle(e.to).toLowerCase().includes(q) ||
      e.kind.toLowerCase().includes(q) ||
      (e.note ?? "").toLowerCase().includes(q),
  );
  const hiddenEdgeCount = edges.filter((e) => hiddenKindSet.has(e.kind)).length;

  const groups: [NodeKind, CanvasNode[]][] = KIND_ORDER.map(
    (kind): [NodeKind, CanvasNode[]] => [
      kind,
      matches.filter((n) => n.kind === kind).sort((a, b) => a.title.localeCompare(b.title)),
    ],
  ).filter(([, items]) => items.length > 0);

  const onRowClick = (e: ReactMouseEvent, node: CanvasNode) => {
    /* a finished marquee must not collapse into a single-row click */
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }
    if (e.shiftKey) {
      /* shift-click — additive toggle, no re-center (matches canvas) */
      useKontur.getState().selectNode(node.id, true);
      return;
    }
    useKontur.getState().selectNode(node.id);
    dispatchFocusNode(node.id);
  };

  /* ---------- list marquee ---------- */

  const onListPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    const rowEl = (e.target as HTMLElement).closest?.("[data-row-node-id],[data-row-edge-id]");
    if (!rowEl) return;
    const rowType: "node" | "edge" = rowEl.hasAttribute("data-row-edge-id") ? "edge" : "node";
    const attr = rowType === "edge" ? "data-row-edge-id" : "data-row-node-id";
    const anchorId = rowEl.getAttribute(attr);
    if (!anchorId) return;
    /* clear any stale suppress from a marquee that ended off-row */
    suppressClickRef.current = false;
    marqueeRef.current = {
      rowType,
      anchorId,
      startY: e.clientY,
      moved: false,
      additive: e.shiftKey || e.ctrlKey || e.metaKey,
    };
    /* window-level release — the marquee must end even if the pointer
     *  leaves the list (no pointer capture: it would retarget the click
     *  away from the row button and break plain single-click select). */
    const onUp = () => {
      onListPointerUp();
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onUp);
    };
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
  };

  const onListPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    const mq = marqueeRef.current;
    const el = listRef.current;
    if (!mq || !el) return;
    if (!mq.moved) {
      if (Math.abs(e.clientY - mq.startY) < 3) return;
      mq.moved = true;
    }
    /* rows of the SAME type crossed between the anchor row's midpoint and
     *  the pointer — node rows select nodes, edge rows select connections */
    const attr = mq.rowType === "edge" ? "data-row-edge-id" : "data-row-node-id";
    const rows = [...el.querySelectorAll<HTMLElement>(`[${attr}]`)];
    const anchorEl = rows.find((r) => r.getAttribute(attr) === mq.anchorId);
    if (!anchorEl) return;
    const anchorRect = anchorEl.getBoundingClientRect();
    const anchorMid = anchorRect.top + anchorRect.height / 2;
    const lo = Math.min(anchorMid, e.clientY);
    const hi = Math.max(anchorMid, e.clientY);
    const hit: string[] = [];
    for (const r of rows) {
      const rect = r.getBoundingClientRect();
      const mid = rect.top + rect.height / 2;
      if (mid >= lo && mid <= hi) hit.push(r.getAttribute(attr)!);
    }
    if (hit.length === 0) return;
    const { canvas, setCanvas } = useKontur.getState();
    if (mq.rowType === "edge") {
      const base = mq.additive ? canvas.selectedEdgeIds : [];
      const next = Array.from(new Set([...base, ...hit]));
      setCanvas({
        selectedIds: [],
        primaryId: null,
        selectedEdgeId: next[0] ?? null,
        selectedEdgeIds: next,
      });
    } else {
      const next = mq.additive ? Array.from(new Set([...canvas.selectedIds, ...hit])) : hit;
      setCanvas({ selectedIds: next, primaryId: mq.anchorId, selectedEdgeId: null });
    }
  };

  const onListPointerUp = () => {
    const mq = marqueeRef.current;
    if (mq?.moved) suppressClickRef.current = true;
    marqueeRef.current = null;
  };

  const jumpToCanvas = (e: ReactMouseEvent, node: CanvasNode) => {
    e.stopPropagation();
    useKontur.getState().selectNode(node.id);
    useKontur.getState().setSurface("canvas");
    requestAnimationFrame(() => dispatchFocusNode(node.id));
  };

  const onEdgeRowClick = (e: ReactMouseEvent, edge: CanvasEdge) => {
    /* a finished marquee must not collapse into a single-row click */
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }
    if (e.shiftKey) {
      /* shift-click — additive toggle, stays in the outline */
      const { canvas, setCanvas } = useKontur.getState();
      const has = canvas.selectedEdgeIds.includes(edge.id);
      const next = has
        ? canvas.selectedEdgeIds.filter((x) => x !== edge.id)
        : [...canvas.selectedEdgeIds, edge.id];
      setCanvas({
        selectedIds: [],
        primaryId: null,
        selectedEdgeId: next[0] ?? null,
        selectedEdgeIds: next,
      });
      return;
    }
    useKontur.getState().setCanvas({
      selectedIds: [],
      primaryId: null,
      selectedEdgeId: edge.id,
      selectedEdgeIds: [edge.id],
    });
    useKontur.getState().setSurface("canvas");
  };

  /* ---------- bulk operations on the edge selection ---------- */

  const bulkEdges = edges.filter((e) => selectedEdgeIds.includes(e.id));

  const bulkRetype = (kind: (typeof ALL_EDGE_KINDS)[number]) => {
    const { canvas, pushUndoSnapshot, updateEdgeKind } = useKontur.getState();
    if (canvas.selectedEdgeIds.length < 2) return;
    pushUndoSnapshot();
    for (const id of canvas.selectedEdgeIds) updateEdgeKind(id, kind);
  };

  const bulkDelete = () => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    if (canvas.selectedEdgeIds.length === 0) return;
    const removed = new Set(canvas.selectedEdgeIds);
    pushUndoSnapshot();
    setCanvas({
      edges: canvas.edges.filter((x) => !removed.has(x.id)),
      selectedEdgeId: null,
      selectedEdgeIds: [],
    });
  };

  return (
    <div className="flex h-full w-full flex-col bg-surface-1">
      {/* header */}
      <div className="flex items-center justify-between gap-2 px-3 pb-2 pt-3">
        <Overline>{t("outline.title")}</Overline>
        {query && (
          <KcToolButton
            size={20}
            onClick={() => setQuery("")}
            aria-label={t("outline.clearFilter")}
            title={t("outline.clearFilter")}
          >
            <X size={12} />
          </KcToolButton>
        )}
      </div>

      {/* filter */}
      <div className="px-3 pb-2">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder={t("outline.filter")}
          aria-label={t("outline.filter")}
          spellCheck={false}
          className="kc-focus-ring w-full rounded-sm border border-line bg-sunken px-2.5 py-1.5 text-[12.5px] text-fg-1 transition-colors duration-100 placeholder:text-fg-3 focus:border-line-strong focus:outline-none"
        />
      </div>

      {/* list */}
      {groups.length === 0 && edgeMatches.length === 0 ? (
        <div className="flex flex-1 items-center justify-center px-6">
          <p className="text-center text-[12.5px] text-fg-3">{t("outline.empty")}</p>
        </div>
      ) : (
        <div
          ref={listRef}
          className="min-h-0 flex-1 select-none overflow-y-auto px-1.5 pb-4"
          onPointerDown={onListPointerDown}
          onPointerMove={onListPointerMove}
          onPointerUp={onListPointerUp}
          onPointerCancel={onListPointerUp}
        >
          {groups.map(([kind, items]) => {
            const Icon = KIND_ICONS[kind];
            const color = kindColorVar(kind);
            return (
              <section key={kind} aria-label={t(`kind.${kind}`)} className="mb-1.5">
                <div className="sticky top-0 z-10 flex items-center gap-1.5 bg-surface-1 px-2 py-1.5">
                  <Icon size={12} strokeWidth={2} className="shrink-0" style={{ color }} aria-hidden />
                  <span className="kc-overline">{t(`kind.${kind}`)}</span>
                  <span className="ml-auto font-mono text-[10px] leading-none text-fg-3">
                    {items.length}
                  </span>
                </div>
                {items.map((n) => {
                  const isPrimary = primaryId === n.id;
                  const isSelected = !isPrimary && selectedIds.includes(n.id);
                  const meta = n.path ?? n.meta;
                  return (
                    <div
                      key={n.id}
                      data-row-node-id={n.id}
                      className={cn(
                        "group/row relative rounded-sm transition-colors duration-100",
                        isPrimary
                          ? "bg-accent-soft"
                          : isSelected
                            ? "bg-accent-soft/45"
                            : "hover:bg-surface-hover",
                      )}
                    >
                      {(isPrimary || isSelected) && (
                        <span
                          className={cn(
                            "absolute inset-y-1 left-0 w-[2px] rounded-full bg-accent",
                            !isPrimary && "opacity-60",
                          )}
                          aria-hidden
                        />
                      )}
                      <button
                        type="button"
                        onClick={(e) => onRowClick(e, n)}
                        aria-current={isPrimary || undefined}
                        className="kc-focus-ring block w-full rounded-sm px-2 py-1.5 pl-3 text-left"
                      >
                        <span
                          className={cn(
                            "block truncate text-[13px] leading-snug",
                            isPrimary ? "text-accent" : "text-fg-1",
                          )}
                        >
                          {n.title}
                        </span>
                        {meta && (
                          <span className="block truncate font-mono text-[10.5px] leading-snug text-fg-3">
                            {meta}
                          </span>
                        )}
                      </button>
                      <span className="absolute right-1.5 top-1/2 flex -translate-y-1/2 items-center gap-1">
                        {n.note && (
                          <span
                            className="flex h-4 w-4 items-center justify-center rounded-xs text-warning"
                            title={n.note}
                            aria-label={t("canvas.note.label")}
                          >
                            <StickyNote size={10} aria-hidden />
                          </span>
                        )}
                        <button
                          type="button"
                          onClick={(e) => jumpToCanvas(e, n)}
                          aria-label={t("outline.jump")}
                          title={t("outline.jump")}
                          className="kc-focus-ring flex h-5 w-5 items-center justify-center rounded-xs text-fg-3 opacity-0 transition-all duration-100 hover:bg-surface-2 hover:text-accent group-hover/row:opacity-100 focus-visible:opacity-100"
                        >
                          <Crosshair size={11} aria-hidden />
                        </button>
                      </span>
                    </div>
                  );
                })}
              </section>
            );
          })}

          {/* connections index — every edge, clickable, query-filtered.
              Shift-click / drag across rows → bulk edge selection (shared
              with the canvas); a bulk action bar appears at the bottom. */}
          {showEdges && edgeMatches.length > 0 && (
            <section className="mt-2 mb-1.5" aria-label={t("outline.edges")}>
              <button
                type="button"
                onClick={() => setShowEdges(false)}
                className="kc-focus-ring sticky top-0 z-10 flex w-full items-center gap-1.5 bg-surface-1 px-2 py-1.5 transition-colors duration-100 hover:bg-surface-hover"
                aria-expanded={showEdges}
              >
                <Link2 size={12} strokeWidth={2} className="shrink-0 text-fg-3" aria-hidden />
                <span className="kc-overline">{t("outline.edges")}</span>
                <span className="ml-auto font-mono text-[10px] leading-none text-fg-3">
                  {edgeMatches.length}
                  {hiddenEdgeCount > 0 && (
                    <span
                      className="ml-1 text-fg-3/70"
                      title={t("canvas.legend.hiddenCount", hiddenEdgeKinds.length)}
                    >
                      +{hiddenEdgeCount}
                    </span>
                  )}
                </span>
                <ChevronRight
                  size={11}
                  className="shrink-0 text-fg-3 transition-transform duration-150 rotate-90"
                  aria-hidden
                />
              </button>
              <div className="flex flex-col gap-px">
                {edgeMatches.map((edge) => {
                  const isPrimary = selectedEdgeId === edge.id;
                  const isBulk = !isPrimary && selectedEdgeIds.includes(edge.id);
                  const kindHidden = hiddenKindSet.has(edge.kind);
                  return (
                    <div
                      key={edge.id}
                      data-row-edge-id={edge.id}
                      className={cn(
                        "group/edge relative rounded-sm transition-colors duration-100",
                        isPrimary
                          ? "bg-accent-soft"
                          : isBulk
                            ? "bg-accent-soft/45"
                            : "hover:bg-surface-hover",
                      )}
                    >
                      {(isPrimary || isBulk) && (
                        <span
                          className={cn(
                            "absolute inset-y-1 left-0 w-[2px] rounded-full bg-accent",
                            !isPrimary && "opacity-60",
                          )}
                          aria-hidden
                        />
                      )}
                      <button
                        type="button"
                        onClick={(e) => onEdgeRowClick(e, edge)}
                        aria-current={isPrimary || undefined}
                        aria-pressed={isBulk || undefined}
                        className="kc-focus-ring block w-full rounded-sm px-2 py-1.5 pl-3 text-left"
                      >
                        <span
                          className={cn(
                            "flex items-center gap-1.5 text-[12px] leading-snug",
                            kindHidden ? "text-fg-3" : "text-fg-2",
                          )}
                        >
                          <span className="min-w-0 flex-1 truncate">
                            <span className="text-fg-1">{nodeTitle(edge.from)}</span>
                            <span className="mx-1 text-fg-3">→</span>
                            <span className="text-fg-1">{nodeTitle(edge.to)}</span>
                          </span>
                          {edge.note && (
                            <span
                              className="flex h-4 w-4 shrink-0 items-center justify-center rounded-xs text-warning"
                              title={edge.note}
                            >
                              <StickyNote size={10} aria-hidden />
                              <span className="sr-only">{t("canvas.edgenote.chipLabel")}</span>
                            </span>
                          )}
                        </span>
                      </button>
                      <span className="pointer-events-none absolute right-1.5 top-1/2 flex -translate-y-1/2 items-center gap-1">
                        <span
                          className={cn(
                            "shrink-0 rounded-xs border px-1 py-px font-mono text-[9px] uppercase tracking-wider transition-colors",
                            isPrimary
                              ? "border-accent-border/60 bg-accent-soft text-accent"
                              : isBulk
                                ? "border-accent-border/40 bg-accent-soft/60 text-accent/80"
                                : kindHidden
                                  ? "border-line-faint bg-sunken text-fg-3/60 line-through"
                                  : "border-line-faint bg-sunken text-fg-3",
                          )}
                        >
                          {edge.kind}
                        </span>
                      </span>
                    </div>
                  );
                })}
              </div>
            </section>
          )}
          {!showEdges && edges.length > 0 && (
            <button
              type="button"
              onClick={() => setShowEdges(true)}
              className="kc-focus-ring mx-2 mt-2 mb-1.5 flex items-center gap-1.5 rounded-sm px-2 py-1.5 transition-colors duration-100 hover:bg-surface-hover"
              aria-expanded={showEdges}
            >
              <Link2 size={12} strokeWidth={2} className="shrink-0 text-fg-3" aria-hidden />
              <span className="kc-overline">{t("outline.edges")}</span>
              <span className="ml-auto font-mono text-[10px] leading-none text-fg-3">
                {edges.length}
              </span>
              <ChevronRight size={11} className="shrink-0 text-fg-3" aria-hidden />
            </button>
          )}
        </div>
      )}

      {/* ---- bulk edge action bar — appears when ≥2 connections are selected ---- */}
      {selectedEdgeIds.length >= 2 && (
        <div
          className="shrink-0 border-t border-line bg-surface-2/95 p-2 animate-rise"
          role="toolbar"
          aria-label={t("outline.bulk.title", selectedEdgeIds.length)}
        >
          <div className="mb-1.5 flex items-center gap-1.5 px-0.5">
            <Link2 size={10} className="shrink-0 text-accent" aria-hidden />
            <span className="kc-overline text-accent">
              {t("outline.bulk.title", selectedEdgeIds.length)}
            </span>
            <button
              type="button"
              onClick={() =>
                useKontur.getState().setCanvas({ selectedEdgeId: null, selectedEdgeIds: [] })
              }
              aria-label={t("canvas.toolbar.clearSelection", selectedEdgeIds.length)}
              title={t("canvas.toolbar.clearSelection", selectedEdgeIds.length)}
              className="kc-focus-ring ml-auto flex h-4 w-4 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
            >
              <X size={10} aria-hidden />
            </button>
          </div>
          <div className="grid grid-cols-6 gap-1" role="group" aria-label={t("canvas.edgemenu.kind")}>
            {ALL_EDGE_KINDS.map((kind) => {
              const Icon = EDGE_KIND_ICONS[kind];
              const uniform = bulkEdges.length > 0 && bulkEdges.every((e) => e.kind === kind);
              return (
                <button
                  key={kind}
                  type="button"
                  onClick={() => bulkRetype(kind)}
                  aria-pressed={uniform || undefined}
                  title={t(`edgekind.${kind}`)}
                  aria-label={t(`edgekind.${kind}`)}
                  className={cn(
                    "kc-focus-ring flex h-7 items-center justify-center rounded-sm border bg-surface-1 transition-colors duration-100",
                    uniform
                      ? "border-accent-border bg-accent-soft text-accent"
                      : "border-line-faint text-fg-3 hover:border-accent-border hover:bg-accent-soft hover:text-accent",
                  )}
                >
                  <Icon size={12} aria-hidden />
                </button>
              );
            })}
          </div>
          <div className="mt-1.5 flex items-center gap-1.5">
            <button
              type="button"
              onClick={bulkDelete}
              className="kc-focus-ring flex h-7 flex-1 items-center justify-center gap-1.5 rounded-sm border border-line bg-surface-1 text-[11.5px] font-medium text-fg-2 transition-colors duration-100 hover:border-error/50 hover:bg-error-soft hover:text-error"
            >
              <Trash2 size={11} aria-hidden />
              {t("outline.bulk.delete", selectedEdgeIds.length)}
            </button>
          </div>
          <p className="mt-1 px-0.5 text-[9.5px] leading-snug text-fg-3">
            {t("outline.bulk.hint")}
          </p>
        </div>
      )}
    </div>
  );
}
