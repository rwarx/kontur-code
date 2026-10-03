"use client";

/* ============================================================
   KONTUR CODE — canvas helpers (geometry, layout, icons)
   Shared by CanvasView / CanvasNodeCard / MiniMap /
   GraphOutlineView. Pure functions + one store-backed zoom
   helper (see note on zoomAtScreen).
   ============================================================ */

import type { CanvasNode, EdgeKind, NodeKind } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import {
  ArrowLeftRight,
  Bot,
  CircleDot,
  Cog,
  CornerDownRight,
  Database,
  FileText,
  FlaskConical,
  Folder,
  Globe,
  Hammer,
  Link2,
  ListChecks,
  Monitor,
  Package,
  Sparkles,
  StickyNote,
  Zap,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";

/* ---------- constants ---------- */

export const NODE_W = 208;
export const NODE_H = 62;
/** extra card height when a node carries a note (preview area) */
export const NOTE_H = 40;

export const ZOOM_MIN = 0.06;
export const ZOOM_MAX = 3.5;

/** Canonical kind ordering (outline groups, auto-layout columns). */
export const KIND_ORDER: NodeKind[] = [
  "module",
  "folder",
  "file",
  "service",
  "interface",
  "data",
  "view",
  "test",
  "plan",
  "task",
  "agent",
  "model",
  "external",
  "note",
];

/* ---------- kinds ---------- */

export const KIND_ICONS: Record<NodeKind, LucideIcon> = {
  file: FileText,
  folder: Folder,
  module: Package,
  service: Cog,
  interface: ArrowLeftRight,
  data: Database,
  view: Monitor,
  test: FlaskConical,
  plan: ListChecks,
  task: CircleDot,
  agent: Bot,
  model: Sparkles,
  external: Globe,
  note: StickyNote,
};

/** Icons for the edge kinds (connection picker + inspector). */
export const EDGE_KIND_ICONS: Record<EdgeKind, LucideIcon> = {
  relates: Link2,
  depends: CornerDownRight,
  calls: Zap,
  implements: Hammer,
  contains: Folder,
  plans: ListChecks,
};

/** Node-kind accent color as a CSS var reference (theme-aware). */
export function kindColorVar(kind: NodeKind): string {
  return `var(--nk-${kind})`;
}

export function nodeW(n: CanvasNode): number {
  return n.w ?? NODE_W;
}

export function nodeH(n: CanvasNode): number {
  const base = n.h ?? NODE_H;
  return n.note ? base + NOTE_H : base;
}

/* ---------- geometry ---------- */

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Viewport {
  zoom: number;
  panX: number;
  panY: number;
}

export function screenToWorld(
  sx: number,
  sy: number,
  zoom: number,
  panX: number,
  panY: number,
): { x: number; y: number } {
  return { x: (sx - panX) / zoom, y: (sy - panY) / zoom };
}

/** World-space bounding box of a set of nodes (null when empty). */
export function nodesBounds(nodes: CanvasNode[]): Rect | null {
  if (nodes.length === 0) return null;
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const n of nodes) {
    minX = Math.min(minX, n.x);
    minY = Math.min(minY, n.y);
    maxX = Math.max(maxX, n.x + nodeW(n));
    maxY = Math.max(maxY, n.y + nodeH(n));
  }
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}

/** Viewport that fits the given nodes with padding, centered. */
export function computeFit(
  nodes: CanvasNode[],
  viewW: number,
  viewH: number,
  padding = 80,
  maxZoom = 1.25,
): Viewport {
  const b = nodesBounds(nodes);
  if (!b || viewW <= 0 || viewH <= 0) {
    return { zoom: 1, panX: viewW / 2, panY: viewH / 2 };
  }
  const bw = Math.max(b.w, 1);
  const bh = Math.max(b.h, 1);
  const availW = Math.max(viewW - padding * 2, 1);
  const availH = Math.max(viewH - padding * 2, 1);
  const zoom = Math.max(ZOOM_MIN, Math.min(availW / bw, availH / bh, maxZoom));
  const panX = (viewW - bw * zoom) / 2 - b.x * zoom;
  const panY = (viewH - bh * zoom) / 2 - b.y * zoom;
  return { zoom, panX, panY };
}

/**
 * Zoom keeping the world point under (px, py) pinned to the same
 * screen position (true zoom-to-cursor).
 *
 * NOTE: the store's `zoomAt` anchors the given point to the *center*
 * of the viewport (panX' = viewW/2 - wx*next), which makes content
 * jump when zooming at the cursor. The store cannot be modified, so
 * this local wrapper computes the pan directly through `setViewport`
 * — same clamping (0.06–3.5), correct anchor semantics.
 */
export function zoomAtScreen(factor: number, px: number, py: number): void {
  const { canvas, setViewport } = useKontur.getState();
  const next = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, canvas.zoom * factor));
  const wx = (px - canvas.panX) / canvas.zoom;
  const wy = (py - canvas.panY) / canvas.zoom;
  setViewport(next, px - wx * next, py - wy * next);
}

/* ---------- edges ---------- */

/** Kinds the user can draw/connect with (contains/plans are structural —
 *  produced by the workspace index and the agent, not hand-drawn). */
export const DRAWABLE_EDGE_KINDS: EdgeKind[] = ["relates", "depends", "calls", "implements"];

/** All kinds in legend order (drawable first, structural last). */
export const ALL_EDGE_KINDS: EdgeKind[] = [
  "relates",
  "depends",
  "calls",
  "implements",
  "contains",
  "plans",
];

/** Suggested drawable kind for a NEW from→to connection, inferred from the
 *  endpoint node kinds. Ordered rules — first match wins:
 *    1. code-ish   → interface  : implements  (realizes the contract)
 *    2. code-ish   → data       : depends     (needs the store to function)
 *    3. test       → code-ish   : calls       (tests exercise the target)
 *    4. runtime    → runtime    : calls       (service-to-service flow)
 *    5. otherwise               : relates     (neutral association)
 *  Structural kinds (contains/plans) are never suggested. */
export function suggestEdgeKind(from: NodeKind, to: NodeKind): EdgeKind {
  const codeish: ReadonlySet<NodeKind> = new Set(["file", "service", "view", "module", "test"]);
  if (to === "interface" && codeish.has(from)) return "implements";
  if (to === "data" && codeish.has(from)) return "depends";
  if (from === "test" && codeish.has(to) && to !== "interface") return "calls";
  if (
    (from === "service" || from === "view" || from === "external" || from === "agent") &&
    (to === "service" || to === "external" || to === "view")
  ) {
    return "calls";
  }
  return "relates";
}

/** Dash pattern per edge kind (undefined = solid). Distinct at a glance,
 *  neutral stroke colors keep the graph calm. */
export function edgeDash(kind: EdgeKind): string | undefined {
  switch (kind) {
    case "contains":
      return "3 4";
    case "depends":
      return "7 4";
    case "implements":
      return "2 3";
    case "plans":
      return "1.5 3";
    default:
      return undefined; /* relates · calls — solid */
  }
}

/** Base stroke width per kind (calls reads slightly heavier = runtime flow). */
export function edgeWidth(kind: EdgeKind): number {
  return kind === "calls" ? 1.5 : 1.2;
}

function borderExit(hw: number, hh: number, dx: number, dy: number): number {
  const tx = dx !== 0 ? hw / Math.abs(dx) : Infinity;
  const ty = dy !== 0 ? hh / Math.abs(dy) : Infinity;
  return Math.min(tx, ty);
}

const r1 = (v: number): number => Math.round(v * 10) / 10;

/** Screen-space border intersection points of the center line between two
 *  nodes (after the overlap-stub fix). Shared by edgeGeometry (control
 *  points) and the reconnect handles (endpoint anchors). */
export interface EdgeEndpoints {
  sx: number;
  sy: number;
  tx: number;
  ty: number;
}

export function edgeEndpoints(
  a: CanvasNode,
  b: CanvasNode,
  zoom: number,
  panX: number,
  panY: number,
): EdgeEndpoints | null {
  if (a.id === b.id) return null;

  const aw = nodeW(a) * zoom;
  const ah = nodeH(a) * zoom;
  const bw = nodeW(b) * zoom;
  const bh = nodeH(b) * zoom;

  const c1x = (a.x + nodeW(a) / 2) * zoom + panX;
  const c1y = (a.y + nodeH(a) / 2) * zoom + panY;
  const c2x = (b.x + nodeW(b) / 2) * zoom + panX;
  const c2y = (b.y + nodeH(b) / 2) * zoom + panY;

  let dx = c2x - c1x;
  let dy = c2y - c1y;
  const d = Math.hypot(dx, dy);
  if (d < 2) return null;
  dx /= d;
  dy /= d;

  const startTrim = borderExit(aw / 2, ah / 2, dx, dy) + 2;
  const endTrim = borderExit(bw / 2, bh / 2, dx, dy) + 1.5;

  let sx = c1x + dx * startTrim;
  let sy = c1y + dy * startTrim;
  let tx = c2x - dx * endTrim;
  let ty = c2y - dy * endTrim;

  /* nodes overlap heavily → draw a short stub towards the target */
  if ((tx - sx) * dx + (ty - sy) * dy < 8) {
    tx = sx + dx * 8;
    ty = sy + dy * 8;
  }

  return { sx, sy, tx, ty };
}

/**
 * Cubic bezier between the *borders* of two nodes (screen space).
 * Endpoints are trimmed along the center line so the arrowhead lands
 * on the target border; control points are offset perpendicular
 * (pull ≈ 0.12 of distance) on top of an axial pull (≈ 0.22) so the
 * end tangent keeps pointing into the target node.
 */
export function edgeGeometry(
  a: CanvasNode,
  b: CanvasNode,
  zoom: number,
  panX: number,
  panY: number,
): string | null {
  const ep = edgeEndpoints(a, b, zoom, panX, panY);
  if (!ep) return null;

  const { sx, sy, tx, ty } = ep;
  const dd = Math.max(Math.hypot(tx - sx, ty - sy), 1);
  const ux = (tx - sx) / dd;
  const uy = (ty - sy) / dd;
  const px = -uy;
  const py = ux;
  const axial = dd * 0.22;
  const perp = dd * 0.12;

  const c1cx = sx + ux * axial + px * perp;
  const c1cy = sy + uy * axial + py * perp;
  const c2cx = tx - ux * axial + px * perp;
  const c2cy = ty - uy * axial + py * perp;

  return `M ${r1(sx)} ${r1(sy)} C ${r1(c1cx)} ${r1(c1cy)} ${r1(c2cx)} ${r1(c2cy)} ${r1(tx)} ${r1(ty)}`;
}

/** Screen-space midpoint between two node centers — anchor for the
 *  selected/hovered edge kind label. */
export function edgeMidScreen(
  a: CanvasNode,
  b: CanvasNode,
  zoom: number,
  panX: number,
  panY: number,
): { x: number; y: number } {
  return {
    x: ((a.x + nodeW(a) / 2 + b.x + nodeW(b) / 2) / 2) * zoom + panX,
    y: ((a.y + nodeH(a) / 2 + b.y + nodeH(b) / 2) / 2) * zoom + panY,
  };
}

/* ---------- auto layout ---------- */

/**
 * Tidy layered layout: one column per node kind (canonical order),
 * rows sorted by title, columns vertically centered. Returns new node
 * objects; callers snapshot for undo before applying.
 */
export function computeAutoLayout(nodes: CanvasNode[]): CanvasNode[] {
  if (nodes.length === 0) return nodes;

  const kinds = KIND_ORDER.filter((k) => nodes.some((n) => n.kind === k));
  const columns = kinds.map((k) =>
    nodes.filter((n) => n.kind === k).sort((a, b) => a.title.localeCompare(b.title)),
  );
  const colWidths = columns.map((col) => Math.max(...col.map(nodeW)));
  const colHeights = columns.map((col) =>
    col.reduce((acc, n) => acc + nodeH(n) + 26, -26),
  );
  const totalH = Math.max(...colHeights);

  const placed: CanvasNode[] = [];
  let x = 0;
  columns.forEach((col, i) => {
    let y = Math.max((totalH - colHeights[i]) / 2, 0);
    for (const n of col) {
      placed.push({ ...n, x: Math.round(x), y: Math.round(y) });
      y += nodeH(n) + 26;
    }
    x += colWidths[i] + 96;
  });
  return placed;
}

/* ---------- cross-surface focus event ---------- */

export const FOCUS_NODE_EVENT = "kontur:focus-node";
/** Ask the canvas to open the note editor for a node (used by the inspector). */
export const EDIT_NOTE_EVENT = "kontur:edit-node-note";
/** Ask the canvas to open the label editor for an edge (used by the inspector). */
export const EDIT_EDGE_NOTE_EVENT = "kontur:edit-edge-note";

/** Ask the canvas to center on a node (used by the graph outline). */
export function dispatchFocusNode(id: string): void {
  window.dispatchEvent(new CustomEvent<{ id: string }>(FOCUS_NODE_EVENT, { detail: { id } }));
}

/** Ask the canvas to open the label editor for an edge (used by the inspector). */
export function dispatchEditEdgeNote(id: string): void {
  window.dispatchEvent(new CustomEvent<{ id: string }>(EDIT_EDGE_NOTE_EVENT, { detail: { id } }));
}
