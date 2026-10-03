"use client";

/* ============================================================
   KONTUR CODE — Canvas surface (infinite graph canvas).
   Pan / zoom-to-cursor / node drag (multi, undoable, Esc-cancel),
   marquee select, clickable edges, floating toolbar, minimap,
   stats chip, keyboard shortcuts, empty state.
   ============================================================ */

import { useEffect, useMemo, useRef, useState } from "react";
import type {
  CSSProperties,
  KeyboardEvent as ReactKeyboardEvent,
  MouseEvent as ReactMouseEvent,
  PointerEvent as ReactPointerEvent,
} from "react";
import {
  AlignCenterHorizontal,
  AlignCenterVertical,
  AlignEndHorizontal,
  AlignEndVertical,
  AlignHorizontalDistributeCenter,
  AlignStartHorizontal,
  AlignStartVertical,
  AlignVerticalDistributeCenter,
  ArrowLeftRight,
  BoxSelect,
  Copy,
  Eye,
  EyeOff,
  FileText,
  Focus,
  Grid3x3,
  Percent,
  Hand,
  Info,
  LayoutGrid,
  Link2,
  ListFilter,
  Maximize2,
  MousePointer2,
  PenLine,
  Search,
  Sparkles,
  StickyNote,
  Trash2,
  Workflow,
  X,
  ZoomIn,
  ZoomOut,
} from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { workspace } from "@/lib/kontur/backend";
import { isServerMode, reindexCanvas, syncWorkspaceFiles, CANVAS_FIT_EVENT } from "@/lib/kontur/sync";
import type { CanvasEdge, CanvasNode, EdgeKind } from "@/lib/kontur/types";
import { KcGhostButton, KcPrimaryButton, KcToolButton } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";
import CanvasNodeCard from "./CanvasNodeCard";
import { MiniMap } from "./MiniMap";
import {
  ALL_EDGE_KINDS,
  DRAWABLE_EDGE_KINDS,
  EDIT_EDGE_NOTE_EVENT,
  EDIT_NOTE_EVENT,
  EDGE_KIND_ICONS,
  FOCUS_NODE_EVENT,
  NODE_W,
  computeAutoLayout,
  computeFit,
  edgeDash,
  edgeEndpoints,
  edgeGeometry,
  edgeMidScreen,
  edgeWidth,
  nodeH,
  nodeW,
  screenToWorld,
  suggestEdgeKind,
  zoomAtScreen,
} from "./canvas-utils";
import type { Rect } from "./canvas-utils";

type Gesture = "none" | "pan" | "drag" | "marquee" | "connect" | "reconnect";

interface DragState {
  pointerId: number;
  startScreen: { x: number; y: number };
  startWorld: { x: number; y: number };
  snapshot: Map<string, { x: number; y: number }>;
  /* the node under the pointer — drives alignment snapping for the whole delta */
  grabbedId: string;
  moved: boolean;
}

interface MarqueeState {
  pointerId: number;
  sx: number;
  sy: number;
  baseX: number;
  baseY: number;
  additive: boolean;
  moved: boolean;
}

interface ConnectState {
  pointerId: number;
  fromId: string;
  /** all source nodes (multi-select bulk connect via Shift); fromId is the anchor */
  sources: string[];
  /* container-space cursor */
  cx: number;
  cy: number;
  hoverId: string | null;
}

/** Reconnect drag: re-pointing one endpoint of an existing edge. */
interface ReconnectState {
  pointerId: number;
  edgeId: string;
  end: "from" | "to";
  cx: number;
  cy: number;
  hoverId: string | null;
}

/** Post-drop kind picker: the edge(s) exist (with the suggested kind) and
 *  can be retyped from a popover anchored at the drop point. */
interface KindPickerState {
  edgeIds: string[];
  x: number;
  y: number;
  /** kind inferred from the endpoint node kinds — row is marked "Suggested" */
  suggested?: EdgeKind;
}

/** Node right-click context menu (container-space anchor). */
interface NodeMenuState {
  id: string;
  x: number;
  y: number;
}

/** Background right-click context menu (container-space anchor). */
interface BgMenuState {
  x: number;
  y: number;
}

/** Edge right-click context menu (container-space anchor). */
interface EdgeMenuState {
  id: string;
  x: number;
  y: number;
}

/** Alignment snap guides while dragging (world-space coordinates). */
interface SnapGuides {
  x: number[];
  y: number[];
  /** drafting-style dimension line: the gap in the OTHER axis between the
   *  dragged node and the node it snapped to (world space, world px) */
  badge?: SnapBadge;
}

interface SnapBadge {
  /** orientation of the dimension line — "v" measures a vertical gap,
   *  "h" a horizontal one */
  axis: "v" | "h";
  /** world coordinate the line sits on (x for v, y for h) */
  at: number;
  /** world-space range the line spans (y1..y2 for v, x1..x2 for h) */
  from: number;
  to: number;
  /** measured gap in world px */
  gap: number;
  /** proximity hint (no snap) — muted styling, no correction applied */
  proximity?: boolean;
}

/** Alignment / distribute modes for the multi-select node-menu section. */
type AlignMode = "left" | "hcenter" | "right" | "top" | "vcenter" | "bottom";

/* Once the user has moved the viewport, keep it across surface switches. */
let canvasViewTouched = false;
function markInteracted(): void {
  canvasViewTouched = true;
}

/* ---------- on-canvas search helpers (module level, pure) ---------- */

function nodeMatchesQuery(n: CanvasNode, q: string): boolean {
  return (
    n.title.toLowerCase().includes(q) ||
    (n.meta ?? "").toLowerCase().includes(q) ||
    (n.path ?? "").toLowerCase().includes(q) ||
    n.kind.toLowerCase().includes(q) ||
    (n.note ?? "").toLowerCase().includes(q)
  );
}

function edgeMatchesQuery(e: CanvasEdge, nodeById: Map<string, CanvasNode>, q: string): boolean {
  if (e.kind.toLowerCase().includes(q)) return true;
  if ((e.note ?? "").toLowerCase().includes(q)) return true;
  const a = nodeById.get(e.from);
  const b = nodeById.get(e.to);
  if (!a || !b) return false;
  /* both endpoint titles matching reads as "the path between them" */
  return a.title.toLowerCase().includes(q) && b.title.toLowerCase().includes(q);
}

type MatchRef = { kind: "node" | "edge"; id: string };

function buildMatches(nodes: CanvasNode[], edges: CanvasEdge[], q: string): MatchRef[] {
  if (!q) return [];
  const map = new Map(nodes.map((n) => [n.id, n]));
  return [
    ...nodes.filter((n) => nodeMatchesQuery(n, q)).map((n) => ({ kind: "node", id: n.id }) as MatchRef),
    ...edges.filter((e) => edgeMatchesQuery(e, map, q)).map((e) => ({ kind: "edge", id: e.id }) as MatchRef),
  ];
}

/** Center the viewport on a search match and select it (node or edge). */
function centerOnMatch(m: MatchRef, el: HTMLElement): void {
  const { canvas, setViewport, setCanvas } = useKontur.getState();
  const r = el.getBoundingClientRect();
  if (m.kind === "node") {
    const n = canvas.nodes.find((x) => x.id === m.id);
    if (!n) return;
    setViewport(
      canvas.zoom,
      r.width / 2 - (n.x + nodeW(n) / 2) * canvas.zoom,
      r.height / 2 - (n.y + nodeH(n) / 2) * canvas.zoom,
    );
    setCanvas({ selectedIds: [n.id], primaryId: n.id, selectedEdgeId: null });
    return;
  }
  const e = canvas.edges.find((x) => x.id === m.id);
  const a = e ? canvas.nodes.find((x) => x.id === e.from) : null;
  const b = e ? canvas.nodes.find((x) => x.id === e.to) : null;
  if (!e || !a || !b) return;
  setViewport(
    canvas.zoom,
    r.width / 2 - ((a.x + nodeW(a) / 2 + b.x + nodeW(b) / 2) / 2) * canvas.zoom,
    r.height / 2 - ((a.y + nodeH(a) / 2 + b.y + nodeH(b) / 2) / 2) * canvas.zoom,
  );
  setCanvas({ selectedIds: [], primaryId: null, selectedEdgeId: e.id });
}

export default function CanvasView() {
  const t = useT();

  const nodes = useKontur((s) => s.canvas.nodes);
  const edges = useKontur((s) => s.canvas.edges);
  const selectedIds = useKontur((s) => s.canvas.selectedIds);
  const primaryId = useKontur((s) => s.canvas.primaryId);
  const selectedEdgeId = useKontur((s) => s.canvas.selectedEdgeId);
  const selectedEdgeIds = useKontur((s) => s.canvas.selectedEdgeIds);
  const hiddenEdgeKinds = useKontur((s) => s.canvas.hiddenEdgeKinds);
  const zoom = useKontur((s) => s.canvas.zoom);
  const panX = useKontur((s) => s.canvas.panX);
  const panY = useKontur((s) => s.canvas.panY);
  const showGrid = useKontur((s) => s.canvas.showGrid);
  const tool = useKontur((s) => s.canvas.tool);

  const containerRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  const fittedRef = useRef(false);
  const [gesture, setGesture] = useState<Gesture>("none");
  const [marquee, setMarquee] = useState<Rect | null>(null);
  /* live connect-drag preview (screen space) */
  const [connect, setConnect] = useState<ConnectState | null>(null);
  /* live reconnect-drag preview (screen space) */
  const [reconnect, setReconnect] = useState<ReconnectState | null>(null);
  /* post-drop edge-kind picker (screen space, data-canvas-overlay) */
  const [kindPicker, setKindPicker] = useState<KindPickerState | null>(null);
  /* ref mirror so the always-on Escape listener sees the current picker */
  const kindPickerRef = useRef<KindPickerState | null>(null);
  /* edge under the pointer — affordance + label chip */
  const [hoverEdgeId, setHoverEdgeId] = useState<string | null>(null);

  /* on-canvas search: query text (null = closed) + index of the active match */
  const [searchQuery, setSearchQuery] = useState<string | null>(null);
  const [searchIdx, setSearchIdx] = useState(0);
  /* filter mode: hide everything that doesn't match (sticky toggle) */
  const [searchFilter, setSearchFilter] = useState(false);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const searchOpenRef = useRef(false);

  /* stats-chip legend popover (hover / focus) */
  const [legendOpen, setLegendOpen] = useState(false);
  const legendTimer = useRef<number | null>(null);

  /* note editor */
  const [noteEditId, setNoteEditId] = useState<string | null>(null);
  const [noteDraft, setNoteDraft] = useState("");
  const noteTaRef = useRef<HTMLTextAreaElement>(null);

  /* edge label editor — anchored at the connection midpoint (screen space) */
  const [edgeNoteEditId, setEdgeNoteEditId] = useState<string | null>(null);
  const [edgeNoteDraft, setEdgeNoteDraft] = useState("");
  const edgeNoteTaRef = useRef<HTMLTextAreaElement>(null);

  /* node right-click menu + background menu + edge menu + alignment guides */
  const [nodeMenu, setNodeMenu] = useState<NodeMenuState | null>(null);
  const nodeMenuRef = useRef<NodeMenuState | null>(null);
  const [bgMenu, setBgMenu] = useState<BgMenuState | null>(null);
  const bgMenuRef = useRef<BgMenuState | null>(null);
  const [edgeMenu, setEdgeMenu] = useState<EdgeMenuState | null>(null);
  const edgeMenuRef = useRef<EdgeMenuState | null>(null);
  const [snapGuides, setSnapGuides] = useState<SnapGuides | null>(null);

  const panRef = useRef<{ pointerId: number; x: number; y: number } | null>(null);
  const dragRef = useRef<DragState | null>(null);
  const marqueeRef = useRef<MarqueeState | null>(null);
  const connectRef = useRef<ConnectState | null>(null);
  const reconnectRef = useRef<ReconnectState | null>(null);

  const selectedSet = useMemo(() => new Set(selectedIds), [selectedIds]);
  const selectedEdgeSet = useMemo(() => new Set(selectedEdgeIds), [selectedEdgeIds]);
  const hiddenKindSet = useMemo(() => new Set(hiddenEdgeKinds), [hiddenEdgeKinds]);
  const nodeById = useMemo(() => new Map(nodes.map((n) => [n.id, n])), [nodes]);
  const noteNode = noteEditId ? (nodeById.get(noteEditId) ?? null) : null;
  const edgeNoteEdge = edgeNoteEditId ? (edges.find((e) => e.id === edgeNoteEditId) ?? null) : null;
  const hasSelection = selectedIds.length > 0 || selectedEdgeId !== null;

  /* ---------- search: matches (nodes first, then edges) ---------- */

  const q = searchQuery !== null ? searchQuery.trim().toLowerCase() : "";
  const searchActive = q.length > 0;
  /* legend-hidden kinds are invisible to search too — a match you cannot
   *  see would center the viewport on nothing */
  const visibleEdges = useMemo(
    () => edges.filter((e) => !hiddenKindSet.has(e.kind)),
    [edges, hiddenKindSet],
  );
  const orderedMatches = useMemo(() => buildMatches(nodes, visibleEdges, q), [nodes, visibleEdges, q]);
  const matchCount = orderedMatches.length;
  const nodeMatchIds = useMemo(
    () => new Set(nodes.filter((n) => nodeMatchesQuery(n, q)).map((n) => n.id)),
    [nodes, q],
  );
  const edgeMatchIds = useMemo(
    () => new Set(visibleEdges.filter((e) => edgeMatchesQuery(e, nodeById, q)).map((e) => e.id)),
    [visibleEdges, nodeById, q],
  );
  /* dim only when there is something to find — zero matches must not blank the graph */
  const searchDimming = searchActive && matchCount > 0;
  const searchOpen = searchQuery !== null;
  /* filter mode: non-matching nodes are HIDDEN (endpoints of matching edges
   *  stay visible so the edge can still be drawn). */
  const visibleNodeIds = useMemo(() => {
    if (!(searchFilter && searchDimming)) return null;
    const s = new Set(nodes.filter((n) => nodeMatchIds.has(n.id)).map((n) => n.id));
    for (const e of edges) {
      if (edgeMatchIds.has(e.id)) {
        s.add(e.from);
        s.add(e.to);
      }
    }
    return s;
  }, [searchFilter, searchDimming, nodes, edges, nodeMatchIds, edgeMatchIds]);

  /* ---------- viewport helpers ---------- */

  const fitView = () => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const { canvas, setViewport } = useKontur.getState();
    const vp = computeFit(canvas.nodes, r.width, r.height);
    markInteracted();
    setViewport(vp.zoom, vp.panX, vp.panY);
  };

  const zoomCentered = (factor: number) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    zoomAtScreen(factor, r.width / 2, r.height / 2);
  };

  const resetZoom = () => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const { canvas, setViewport } = useKontur.getState();
    const wx = (r.width / 2 - canvas.panX) / canvas.zoom;
    const wy = (r.height / 2 - canvas.panY) / canvas.zoom;
    setViewport(1, r.width / 2 - wx, r.height / 2 - wy);
  };

  const focusSelection = () => {
    const el = containerRef.current;
    if (!el) return;
    const { canvas, setViewport } = useKontur.getState();
    const sel = canvas.nodes.filter((n) => canvas.selectedIds.includes(n.id));
    if (sel.length === 0) return;
    const r = el.getBoundingClientRect();
    const vp = computeFit(sel, r.width, r.height, 120, 1.6);
    setViewport(vp.zoom, vp.panX, vp.panY);
  };

  const autoLayout = () => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    if (canvas.nodes.length === 0) return;
    pushUndoSnapshot();
    setCanvas({ nodes: computeAutoLayout(canvas.nodes) });
    void import("@/lib/kontur/sync").then((m) => m.pushCanvasMoves());
    fitView();
  };

  const centerWorld = (wx: number, wy: number) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const { canvas, setViewport } = useKontur.getState();
    setViewport(canvas.zoom, r.width / 2 - wx * canvas.zoom, r.height / 2 - wy * canvas.zoom);
  };

  const deleteSelection = () => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    if (canvas.selectedEdgeId && canvas.selectedIds.length === 0) {
      const bulk = new Set(canvas.selectedEdgeIds);
      const removedEdges = canvas.edges.filter((x) => (bulk.size > 1 ? bulk.has(x.id) : x.id === canvas.selectedEdgeId)).map((x) => x.id);
      pushUndoSnapshot();
      setCanvas({
        edges: canvas.edges.filter((x) => (bulk.size > 1 ? !bulk.has(x.id) : x.id !== canvas.selectedEdgeId)),
        selectedEdgeId: null,
        selectedEdgeIds: [],
      });
      void import("@/lib/kontur/sync").then((m) => m.pushCanvasDeletion([], removedEdges));
      return;
    }
    if (canvas.selectedIds.length === 0) return;
    pushUndoSnapshot();
    const removed = new Set(canvas.selectedIds);
    const removedEdges = canvas.edges.filter((x) => removed.has(x.from) || removed.has(x.to)).map((x) => x.id);
    setCanvas({
      nodes: canvas.nodes.filter((n) => !removed.has(n.id)),
      edges: canvas.edges.filter((x) => !removed.has(x.from) && !removed.has(x.to)),
      selectedIds: [],
      primaryId: null,
    });
    void import("@/lib/kontur/sync").then((m) => m.pushCanvasDeletion([...removed], removedEdges));
  };

  /* ---------- gestures ---------- */

  /* port screen position: right-center of the node card */
  const portScreen = (n: CanvasNode): { x: number; y: number } => ({
    x: (n.x + nodeW(n)) * zoom + panX,
    y: (n.y + nodeH(n) / 2) * zoom + panY,
  });

  const clearConnect = () => {
    connectRef.current = null;
    setConnect(null);
    setGesture("none");
  };

  /* ---------- reconnect gesture ---------- */

  const clearReconnect = () => {
    reconnectRef.current = null;
    setReconnect(null);
    setGesture("none");
  };

  const onReconnectStart = (
    e: ReactPointerEvent<SVGElement>,
    edge: CanvasEdge,
    end: "from" | "to",
  ) => {
    const el = containerRef.current;
    if (!el) return;
    e.stopPropagation();
    const r = el.getBoundingClientRect();
    const state: ReconnectState = {
      pointerId: e.pointerId,
      edgeId: edge.id,
      end,
      cx: e.clientX - r.left,
      cy: e.clientY - r.top,
      hoverId: null,
    };
    reconnectRef.current = state;
    setReconnect({ ...state });
    setGesture("reconnect");
    markInteracted();
    try {
      containerRef.current?.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort */
    }
  };

  const moveReconnect = (e: ReactPointerEvent<HTMLDivElement>, rc: ReconnectState) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    rc.cx = e.clientX - r.left;
    rc.cy = e.clientY - r.top;
    const wx = (rc.cx - panX) / zoom;
    const wy = (rc.cy - panY) / zoom;
    const hit =
      nodes.find(
        (n) => wx > n.x && wx < n.x + nodeW(n) && wy > n.y && wy < n.y + nodeH(n),
      )?.id ?? null;
    if (hit !== rc.hoverId) rc.hoverId = hit;
    setReconnect({ ...rc });
  };

  const finishReconnect = (rc: ReconnectState) => {
    const { canvas, pushUndoSnapshot, updateEdgeEndpoints, pushToast } = useKontur.getState();
    const edge = canvas.edges.find((x) => x.id === rc.edgeId);
    const target = rc.hoverId;
    clearReconnect();
    if (!edge || !target) return;
    const newFrom = rc.end === "from" ? target : edge.from;
    const newTo = rc.end === "to" ? target : edge.to;
    if (newFrom === newTo) return; /* self-loops are not drawable */
    if (newFrom === edge.from && newTo === edge.to) return; /* dropped back — no-op */
    if (canvas.edges.some((x) => x.id !== edge.id && x.from === newFrom && x.to === newTo)) {
      pushToast(t("canvas.connect.exists"), undefined);
      return;
    }
    pushUndoSnapshot();
    updateEdgeEndpoints(edge.id, newFrom, newTo);
  };

  /* ---------- on-canvas search ---------- */

  const openSearch = () => setSearchQuery("");

  const closeSearch = () => {
    setSearchQuery(null);
    setSearchIdx(0);
    containerRef.current?.focus({ preventScroll: true });
  };

  const onSearchInput = (value: string) => {
    setSearchQuery(value);
    setSearchIdx(0);
  };

  const stepSearch = (dir: 1 | -1) => {
    if (orderedMatches.length === 0) return;
    const base = Math.min(searchIdx, orderedMatches.length - 1);
    const next = (((base + dir) % orderedMatches.length) + orderedMatches.length) % orderedMatches.length;
    setSearchIdx(next);
    const el = containerRef.current;
    if (el) centerOnMatch(orderedMatches[next], el);
  };

  const onStartConnect = (e: ReactPointerEvent<HTMLElement>, node: CanvasNode) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    /* Shift from the handle → bulk connect every selected node (anchor first) */
    const { canvas } = useKontur.getState();
    const bulk = e.shiftKey && canvas.selectedIds.length > 1 && canvas.selectedIds.includes(node.id);
    const sources = bulk
      ? [node.id, ...canvas.selectedIds.filter((id) => id !== node.id)]
      : [node.id];
    const state: ConnectState = {
      pointerId: e.pointerId,
      fromId: node.id,
      sources,
      cx: e.clientX - r.left,
      cy: e.clientY - r.top,
      hoverId: null,
    };
    connectRef.current = state;
    setConnect({ ...state });
    setGesture("connect");
    markInteracted();
    try {
      containerRef.current?.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort */
    }
  };

  const moveConnect = (e: ReactPointerEvent<HTMLDivElement>, c: ConnectState) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    c.cx = e.clientX - r.left;
    c.cy = e.clientY - r.top;
    /* world-space hit test for the drop target */
    const wx = (c.cx - panX) / zoom;
    const wy = (c.cy - panY) / zoom;
    const hit =
      nodes.find(
        (n) => n.id !== c.fromId && wx > n.x && wx < n.x + nodeW(n) && wy > n.y && wy < n.y + nodeH(n),
      )?.id ?? null;
    if (hit !== c.hoverId) c.hoverId = hit;
    setConnect({ ...c });
  };

  const finishConnect = (c: ConnectState) => {
    const { canvas, pushUndoSnapshot, addEdges, pushToast } = useKontur.getState();
    const target = c.hoverId && c.hoverId !== c.fromId ? c.hoverId : null;
    const bulk = c.sources.length > 1;
    if (target) {
      const targetNode = canvas.nodes.find((n) => n.id === target);
      const existing = new Set(canvas.edges.map((x) => `${x.from}→${x.to}`));
      const stamp = Date.now().toString(36);
      const created: CanvasEdge[] = [];
      let suggested: EdgeKind | undefined;
      for (const from of c.sources) {
        if (from === target || existing.has(`${from}→${target}`)) continue;
        const fromNode = canvas.nodes.find((n) => n.id === from);
        /* smart default: infer the kind from the endpoint node kinds */
        const kind: EdgeKind =
          fromNode && targetNode ? suggestEdgeKind(fromNode.kind, targetNode.kind) : "relates";
        if (suggested === undefined) suggested = kind;
        const edge: CanvasEdge = {
          id: `edge-${stamp}-${created.length}-${Math.random().toString(36).slice(2, 6)}`,
          from,
          to: target,
          kind,
        };
        created.push(edge);
      }
      if (created.length === 0) {
        pushToast(t(bulk ? "canvas.connect.bulkExists" : "canvas.connect.exists"), undefined);
      } else {
        pushUndoSnapshot();
        addEdges(created);
        if (bulk && created.length > 1) {
          pushToast(t("canvas.connect.bulkCreated", created.length), undefined);
        }
        /* open the kind picker anchored at the drop point */
        setKindPicker({ edgeIds: created.map((x) => x.id), x: c.cx, y: c.cy, suggested });
      }
    }
    clearConnect();
  };

  const startPan = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button === 1) e.preventDefault(); /* block middle-click autoscroll */
    markInteracted();
    panRef.current = { pointerId: e.pointerId, x: e.clientX, y: e.clientY };
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort (synthetic / already-released pointers) */
    }
    setGesture("pan");
  };

  const startMarquee = (e: ReactPointerEvent<HTMLDivElement>) => {
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    marqueeRef.current = {
      pointerId: e.pointerId,
      sx: e.clientX,
      sy: e.clientY,
      baseX: r.left,
      baseY: r.top,
      additive: e.shiftKey || e.ctrlKey || e.metaKey,
      moved: false,
    };
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort (synthetic / already-released pointers) */
    }
  };

  const cancelDrag = () => {
    const drag = dragRef.current;
    if (!drag) return;
    dragRef.current = null;
    setGesture("none");
    setSnapGuides(null);
    const { canvas, setCanvas } = useKontur.getState();
    setCanvas({
      nodes: canvas.nodes.map((n) => {
        const s = drag.snapshot.get(n.id);
        return s ? { ...n, x: s.x, y: s.y } : n;
      }),
    });
  };

  const moveDrag = (e: ReactPointerEvent<HTMLDivElement>, drag: DragState) => {
    const el = containerRef.current;
    if (!el) return;
    if (!drag.moved) {
      if (Math.abs(e.clientX - drag.startScreen.x) + Math.abs(e.clientY - drag.startScreen.y) < 3) return;
      drag.moved = true;
      setGesture("drag");
      markInteracted();
      /* snapshot of pre-drag positions BEFORE the first mutation */
      useKontur.getState().pushUndoSnapshot();
    }
    const { canvas, moveNode } = useKontur.getState();
    const r = el.getBoundingClientRect();
    const wx = (e.clientX - r.left - canvas.panX) / canvas.zoom;
    const wy = (e.clientY - r.top - canvas.panY) / canvas.zoom;
    let dx = wx - drag.startWorld.x;
    let dy = wy - drag.startWorld.y;

    /* ---- alignment snap guides (Alt disables) ----
     * The grabbed node's left/center/right (and top/middle/bottom) edges snap
     * to any other node's edges within a screen-consistent threshold. The
     * correction applies to the whole drag delta, so multi-select drags stay
     * rigid. Guides render at the snapped target coordinate (world space). */
    let guides: SnapGuides | null = null;
    if (!e.altKey) {
      const grabbed = canvas.nodes.find((n) => n.id === drag.grabbedId);
      const base = drag.snapshot.get(drag.grabbedId);
      if (grabbed && base) {
        const threshold = 6 / canvas.zoom;
        const others = canvas.nodes.filter((n) => !drag.snapshot.has(n.id));
        const candX = [base.x + dx, base.x + dx + nodeW(grabbed) / 2, base.x + dx + nodeW(grabbed)];
        const candY = [base.y + dy, base.y + dy + nodeH(grabbed) / 2, base.y + dy + nodeH(grabbed)];
        let bestX: { delta: number; at: number; ref: CanvasNode } | null = null;
        let bestY: { delta: number; at: number; ref: CanvasNode } | null = null;
        for (const o of others) {
          for (const target of [o.x, o.x + nodeW(o) / 2, o.x + nodeW(o)]) {
            for (const cx of candX) {
              const delta = target - cx;
              if (Math.abs(delta) <= threshold && (!bestX || Math.abs(delta) < Math.abs(bestX.delta))) {
                bestX = { delta, at: target, ref: o };
              }
            }
          }
          for (const target of [o.y, o.y + nodeH(o) / 2, o.y + nodeH(o)]) {
            for (const cy of candY) {
              const delta = target - cy;
              if (Math.abs(delta) <= threshold && (!bestY || Math.abs(delta) < Math.abs(bestY.delta))) {
                bestY = { delta, at: target, ref: o };
              }
            }
          }
        }
        if (bestX) dx += bestX.delta;
        if (bestY) dy += bestY.delta;
        if (bestX || bestY) {
          guides = { x: bestX ? [bestX.at] : [], y: bestY ? [bestY.at] : [] };
          /* distance badge — the gap in the OTHER axis between the dragged
           *  node and the node it snapped to (Figma-style measurement). */
          if (bestX && !bestY) {
            const gTop = base.y + dy;
            const gBottom = gTop + nodeH(grabbed);
            const o = bestX.ref;
            if (gTop >= o.y + nodeH(o)) {
              const gap = gTop - (o.y + nodeH(o));
              if (gap > 0.5) {
                guides = { ...guides, badge: { axis: "v", at: bestX.at, from: o.y + nodeH(o), to: gTop, gap } };
              }
            } else if (o.y >= gBottom) {
              const gap = o.y - gBottom;
              if (gap > 0.5) {
                guides = { ...guides, badge: { axis: "v", at: bestX.at, from: gBottom, to: o.y, gap } };
              }
            }
          } else if (bestY && !bestX) {
            const gLeft = base.x + dx;
            const gRight = gLeft + nodeW(grabbed);
            const o = bestY.ref;
            if (gLeft >= o.x + nodeW(o)) {
              const gap = gLeft - (o.x + nodeW(o));
              if (gap > 0.5) {
                guides = { ...guides, badge: { axis: "h", at: bestY.at, from: o.x + nodeW(o), to: gLeft, gap } };
              }
            } else if (o.x >= gRight) {
              const gap = o.x - gRight;
              if (gap > 0.5) {
                guides = { ...guides, badge: { axis: "h", at: bestY.at, from: gRight, to: o.x, gap } };
              }
            }
          }
        } else {
          /* proximity gap badge — nothing snapped, but the dragged node hovers
           * near alignment on ONE axis within a wider threshold: show the gap
           * in the other axis (Figma-style, muted — no correction applied). */
          const nearThreshold = 24 / canvas.zoom;
          let nearX: { delta: number; edge: number; ref: CanvasNode } | null = null;
          let nearY: { delta: number; edge: number; ref: CanvasNode } | null = null;
          for (const o of others) {
            for (const target of [o.x, o.x + nodeW(o) / 2, o.x + nodeW(o)]) {
              for (const cx of candX) {
                const delta = target - cx;
                if (Math.abs(delta) <= nearThreshold && (!nearX || Math.abs(delta) < Math.abs(nearX.delta))) {
                  nearX = { delta, edge: cx, ref: o };
                }
              }
            }
            for (const target of [o.y, o.y + nodeH(o) / 2, o.y + nodeH(o)]) {
              for (const cy of candY) {
                const delta = target - cy;
                if (Math.abs(delta) <= nearThreshold && (!nearY || Math.abs(delta) < Math.abs(nearY.delta))) {
                  nearY = { delta, edge: cy, ref: o };
                }
              }
            }
          }
          const near =
            nearX && nearY
              ? Math.abs(nearX.delta) <= Math.abs(nearY.delta)
                ? { ax: "x" as const, d: nearX }
                : { ax: "y" as const, d: nearY }
              : nearX
                ? { ax: "x" as const, d: nearX }
                : nearY
                  ? { ax: "y" as const, d: nearY }
                  : null;
          if (near) {
            const gTop = base.y + dy;
            const gBottom = gTop + nodeH(grabbed);
            const gLeft = base.x + dx;
            const gRight = gLeft + nodeW(grabbed);
            const o = near.d.ref;
            if (near.ax === "x") {
              if (gTop >= o.y + nodeH(o)) {
                const gap = gTop - (o.y + nodeH(o));
                if (gap > 0.5) {
                  guides = {
                    x: [],
                    y: [],
                    badge: { axis: "v", at: near.d.edge, from: o.y + nodeH(o), to: gTop, gap, proximity: true },
                  };
                }
              } else if (o.y >= gBottom) {
                const gap = o.y - gBottom;
                if (gap > 0.5) {
                  guides = {
                    x: [],
                    y: [],
                    badge: { axis: "v", at: near.d.edge, from: gBottom, to: o.y, gap, proximity: true },
                  };
                }
              }
            } else {
              if (gLeft >= o.x + nodeW(o)) {
                const gap = gLeft - (o.x + nodeW(o));
                if (gap > 0.5) {
                  guides = {
                    x: [],
                    y: [],
                    badge: { axis: "h", at: near.d.edge, from: o.x + nodeW(o), to: gLeft, gap, proximity: true },
                  };
                }
              } else if (o.x >= gRight) {
                const gap = o.x - gRight;
                if (gap > 0.5) {
                  guides = {
                    x: [],
                    y: [],
                    badge: { axis: "h", at: near.d.edge, from: gRight, to: o.x, gap, proximity: true },
                  };
                }
              }
            }
          }
        }
      }
    }
    setSnapGuides(guides);

    drag.snapshot.forEach((pos, id) => {
      moveNode(id, pos.x + dx, pos.y + dy);
    });
  };

  const finishMarquee = (e: ReactPointerEvent<HTMLDivElement>, mq: MarqueeState) => {
    const { canvas, setCanvas } = useKontur.getState();
    const x1 = (Math.min(mq.sx, e.clientX) - mq.baseX - canvas.panX) / canvas.zoom;
    const y1 = (Math.min(mq.sy, e.clientY) - mq.baseY - canvas.panY) / canvas.zoom;
    const x2 = (Math.max(mq.sx, e.clientX) - mq.baseX - canvas.panX) / canvas.zoom;
    const y2 = (Math.max(mq.sy, e.clientY) - mq.baseY - canvas.panY) / canvas.zoom;
    const hit = canvas.nodes
      .filter((n) => n.x < x2 && n.x + nodeW(n) > x1 && n.y < y2 && n.y + nodeH(n) > y1)
      .map((n) => n.id);
    const next = mq.additive ? Array.from(new Set([...canvas.selectedIds, ...hit])) : hit;
    setCanvas({ selectedIds: next, primaryId: next[0] ?? null, selectedEdgeId: null });
  };

  const onContainerPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button === 2) return;
    const target = e.target as HTMLElement;
    if (target.closest("[data-canvas-overlay]")) return;
    /* any plain canvas press dismisses the post-drop kind picker + open search */
    if (kindPicker) setKindPicker(null);
    if (nodeMenu) setNodeMenu(null);
    if (bgMenu) setBgMenu(null);
    if (edgeMenu) setEdgeMenu(null);
    if (searchQuery !== null) closeSearch();
    /* middle mouse always pans; hand tool pans anywhere */
    if (e.button === 1 || (tool === "pan" && e.button === 0)) {
      startPan(e);
      return;
    }
    if (e.button === 0 && tool === "select" && target === e.currentTarget) {
      startMarquee(e);
    }
  };

  const onContainerPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    const rcn = reconnectRef.current;
    if (rcn && rcn.pointerId === e.pointerId) {
      moveReconnect(e, rcn);
      return;
    }
    const conn = connectRef.current;
    if (conn && conn.pointerId === e.pointerId) {
      moveConnect(e, conn);
      return;
    }
    const pan = panRef.current;
    if (pan && pan.pointerId === e.pointerId) {
      useKontur.getState().panBy(e.clientX - pan.x, e.clientY - pan.y);
      pan.x = e.clientX;
      pan.y = e.clientY;
      return;
    }
    const drag = dragRef.current;
    if (drag && drag.pointerId === e.pointerId) {
      moveDrag(e, drag);
      return;
    }
    const mq = marqueeRef.current;
    if (mq && mq.pointerId === e.pointerId) {
      if (!mq.moved) {
        if (Math.abs(e.clientX - mq.sx) + Math.abs(e.clientY - mq.sy) < 3) return;
        mq.moved = true;
        setGesture("marquee");
        markInteracted();
      }
      setMarquee({
        x: Math.min(mq.sx, e.clientX) - mq.baseX,
        y: Math.min(mq.sy, e.clientY) - mq.baseY,
        w: Math.abs(e.clientX - mq.sx),
        h: Math.abs(e.clientY - mq.sy),
      });
    }
  };

  const onContainerPointerUp = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (reconnectRef.current?.pointerId === e.pointerId) {
      finishReconnect(reconnectRef.current);
      return;
    }
    const conn = connectRef.current;
    if (conn && conn.pointerId === e.pointerId) {
      finishConnect(conn);
      return;
    }
    if (panRef.current?.pointerId === e.pointerId) {
      panRef.current = null;
      setGesture("none");
      setSnapGuides(null);
      return;
    }
    if (dragRef.current?.pointerId === e.pointerId) {
      dragRef.current = null;
      setGesture("none");
      setSnapGuides(null);
      return;
    }
    const mq = marqueeRef.current;
    if (mq?.pointerId === e.pointerId) {
      marqueeRef.current = null;
      setMarquee(null);
      setGesture("none");
      if (mq.moved) {
        finishMarquee(e, mq);
      } else {
        /* plain click on the background */
        useKontur.getState().clearSelection();
      }
    }
  };

  const onContainerPointerCancel = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (reconnectRef.current?.pointerId === e.pointerId) {
      clearReconnect();
      return;
    }
    if (connectRef.current?.pointerId === e.pointerId) {
      clearConnect();
      return;
    }
    if (panRef.current?.pointerId === e.pointerId) {
      panRef.current = null;
      setGesture("none");
      return;
    }
    if (dragRef.current?.pointerId === e.pointerId) {
      cancelDrag();
      return;
    }
    if (marqueeRef.current?.pointerId === e.pointerId) {
      marqueeRef.current = null;
      setMarquee(null);
      setGesture("none");
    }
  };

  const onNodePointerDown = (e: ReactPointerEvent<HTMLDivElement>, node: CanvasNode) => {
    /* select tool + left button drags; middle/right bubble up (middle → pan) */
    if (tool !== "select" || e.button !== 0) return;
    e.stopPropagation();
    const { canvas, selectNode, setCanvas } = useKontur.getState();
    const additive = e.shiftKey || e.ctrlKey || e.metaKey;
    if (!canvas.selectedIds.includes(node.id)) {
      selectNode(node.id, additive);
    } else if (!additive && canvas.primaryId !== node.id) {
      setCanvas({ primaryId: node.id, selectedEdgeId: null });
    }
    const ids =
      canvas.selectedIds.includes(node.id) && !additive ? canvas.selectedIds : [node.id];
    const snapshot = new Map<string, { x: number; y: number }>();
    for (const id of ids) {
      const n = canvas.nodes.find((nn) => nn.id === id);
      if (n) snapshot.set(id, { x: n.x, y: n.y });
    }
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    dragRef.current = {
      pointerId: e.pointerId,
      startScreen: { x: e.clientX, y: e.clientY },
      startWorld: screenToWorld(
        e.clientX - r.left,
        e.clientY - r.top,
        canvas.zoom,
        canvas.panX,
        canvas.panY,
      ),
      snapshot,
      grabbedId: node.id,
      moved: false,
    };
    try {
      e.currentTarget.setPointerCapture(e.pointerId);
    } catch {
      /* capture is best-effort (synthetic / already-released pointers) */
    }
  };

  /* right-click on a node → context menu. A node that is already part of the
   *  selection keeps that selection (so the align section / bulk actions work);
   *  an unselected node becomes the sole selection. */
  const onNodeContextMenu = (e: ReactMouseEvent<HTMLDivElement>, node: CanvasNode) => {
    e.preventDefault();
    e.stopPropagation();
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const { canvas, selectNode, setCanvas } = useKontur.getState();
    if (!canvas.selectedIds.includes(node.id)) {
      selectNode(node.id, false);
    } else if (canvas.primaryId !== node.id) {
      setCanvas({ primaryId: node.id, selectedEdgeId: null });
    }
    setBgMenu(null);
    setEdgeMenu(null);
    setNodeMenu({ id: node.id, x: e.clientX - r.left, y: e.clientY - r.top });
    markInteracted();
  };

  /* right-click on an edge → edge action menu. The edge becomes the sole
   *  selection (same as left-click), so the endpoint handles appear for
   *  retargeting while the menu is open. */
  const onEdgeContextMenu = (e: ReactMouseEvent<SVGPathElement>, edge: CanvasEdge) => {
    e.preventDefault();
    e.stopPropagation();
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    useKontur.getState().setCanvas({ selectedIds: [], primaryId: null, selectedEdgeId: edge.id });
    setNodeMenu(null);
    setBgMenu(null);
    setEdgeMenu({ id: edge.id, x: e.clientX - r.left, y: e.clientY - r.top });
    markInteracted();
  };

  /* right-click on the plain background → canvas action menu (nodes and
   *  edges run their own handlers and stop propagation; the remaining svg
   *  surface just swallows the native menu) */
  const onContainerContextMenu = (e: ReactMouseEvent<HTMLDivElement>) => {
    if (e.target !== e.currentTarget) {
      if ((e.target as Element).closest?.("svg[data-canvas-edges]")) e.preventDefault();
      return;
    }
    e.preventDefault();
    const el = containerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    setNodeMenu(null);
    setEdgeMenu(null);
    setBgMenu({ x: e.clientX - r.left, y: e.clientY - r.top });
    markInteracted();
  };

  const deleteNodeById = (id: string) => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    pushUndoSnapshot();
    setCanvas({
      nodes: canvas.nodes.filter((n) => n.id !== id),
      edges: canvas.edges.filter((x) => x.from !== id && x.to !== id),
      selectedIds: [],
      primaryId: null,
    });
  };

  /* ---------- background context-menu actions ---------- */

  const addNoteAt = (sx: number, sy: number) => {
    const { canvas, addNodes, selectNode, pushUndoSnapshot } = useKontur.getState();
    pushUndoSnapshot();
    const wx = (sx - canvas.panX) / canvas.zoom;
    const wy = (sy - canvas.panY) / canvas.zoom;
    const count = canvas.nodes.filter((n) => n.kind === "note").length;
    const node: CanvasNode = {
      id: `note-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`,
      kind: "note",
      title: t("canvas.note.nodeTitle", count + 1),
      meta: new Date().toLocaleDateString(),
      x: Math.round(wx - NODE_W / 2),
      y: Math.round(wy - 15),
    };
    addNodes([node]);
    selectNode(node.id);
    markInteracted();
    setNoteEditId(node.id);
    setNoteDraft("");
  };

  const selectAllNodes = () => {
    const { canvas, setCanvas } = useKontur.getState();
    if (canvas.nodes.length === 0) return;
    setCanvas({
      selectedIds: canvas.nodes.map((n) => n.id),
      primaryId: canvas.nodes[0].id,
      selectedEdgeId: null,
    });
  };

  /* ---------- multi-select alignment (node-menu section) ---------- */

  const alignSelection = (mode: AlignMode) => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    const sel = canvas.nodes.filter((n) => canvas.selectedIds.includes(n.id));
    if (sel.length < 2) return;
    const minX = Math.min(...sel.map((n) => n.x));
    const maxX = Math.max(...sel.map((n) => n.x + nodeW(n)));
    const cx = (minX + maxX) / 2;
    const minY = Math.min(...sel.map((n) => n.y));
    const maxY = Math.max(...sel.map((n) => n.y + nodeH(n)));
    const cy = (minY + maxY) / 2;
    pushUndoSnapshot();
    setCanvas({
      nodes: canvas.nodes.map((n) => {
        if (!canvas.selectedIds.includes(n.id)) return n;
        switch (mode) {
          case "left":
            return { ...n, x: Math.round(minX) };
          case "right":
            return { ...n, x: Math.round(maxX - nodeW(n)) };
          case "hcenter":
            return { ...n, x: Math.round(cx - nodeW(n) / 2) };
          case "top":
            return { ...n, y: Math.round(minY) };
          case "bottom":
            return { ...n, y: Math.round(maxY - nodeH(n)) };
          case "vcenter":
            return { ...n, y: Math.round(cy - nodeH(n) / 2) };
        }
      }),
    });
  };

  const distributeSelection = (axis: "x" | "y") => {
    const { canvas, pushUndoSnapshot, setCanvas } = useKontur.getState();
    const sel = canvas.nodes.filter((n) => canvas.selectedIds.includes(n.id));
    if (sel.length < 3) return;
    const sorted = [...sel].sort((a, b) => (axis === "x" ? a.x - b.x : a.y - b.y));
    const size = (n: CanvasNode) => (axis === "x" ? nodeW(n) : nodeH(n));
    const pos = (n: CanvasNode) => (axis === "x" ? n.x : n.y);
    const first = sorted[0];
    const last = sorted[sorted.length - 1];
    const start = pos(first);
    const end = pos(last) + size(last);
    const totalSize = sorted.reduce((acc, n) => acc + size(n), 0);
    const span = end - start - totalSize;
    if (span < 0) return; /* overlapping — nothing meaningful to distribute */
    const gap = span / (sorted.length - 1);
    let cursor = start;
    const next = new Map<string, number>();
    for (const n of sorted) {
      next.set(n.id, Math.round(cursor));
      cursor += size(n) + gap;
    }
    pushUndoSnapshot();
    setCanvas({
      nodes: canvas.nodes.map((n) => {
        const p = next.get(n.id);
        if (p === undefined) return n;
        return axis === "x" ? { ...n, x: p } : { ...n, y: p };
      }),
    });
  };

  const onNodeDoubleClick = (node: CanvasNode) => {
    if (node.path) {
      const { openCodeTab, setSurface } = useKontur.getState();
      openCodeTab(node.path);
      setSurface("code");
      return;
    }
    /* path-less nodes (incl. standalone notes) → annotate */
    openNoteEditor(node);
  };

  /* ---------- note editor ---------- */

  const openNoteEditor = (node: CanvasNode) => {
    setNoteEditId(node.id);
    setNoteDraft(node.note ?? "");
  };

  const autosizeNote = () => {
    const ta = noteTaRef.current;
    if (!ta) return;
    ta.style.height = "auto";
    ta.style.height = `${Math.min(Math.max(ta.scrollHeight, 58), 220)}px`;
  };

  const saveNoteEditor = () => {
    const id = noteEditId;
    if (!id) return;
    useKontur.getState().setNodeNote(id, noteDraft);
    setNoteEditId(null);
    setNoteDraft("");
  };

  const removeNote = () => {
    if (!noteNode) return;
    useKontur.getState().setNodeNote(noteNode.id, undefined);
    setNoteEditId(null);
    setNoteDraft("");
  };

  /* ---------- edge label editor ---------- */

  const openEdgeNoteEditor = (edge: CanvasEdge) => {
    setNodeMenu(null);
    setBgMenu(null);
    setEdgeMenu(null);
    setKindPicker(null);
    setEdgeNoteEditId(edge.id);
    setEdgeNoteDraft(edge.note ?? "");
  };

  const autosizeEdgeNote = () => {
    const ta = edgeNoteTaRef.current;
    if (!ta) return;
    ta.style.height = "auto";
    ta.style.height = `${Math.min(Math.max(ta.scrollHeight, 44), 160)}px`;
  };

  const saveEdgeNoteEditor = () => {
    const id = edgeNoteEditId;
    if (!id) return;
    useKontur.getState().setEdgeNote(id, edgeNoteDraft);
    setEdgeNoteEditId(null);
    setEdgeNoteDraft("");
  };

  const removeEdgeNote = () => {
    if (!edgeNoteEdge) return;
    useKontur.getState().setEdgeNote(edgeNoteEdge.id, undefined);
    setEdgeNoteEditId(null);
    setEdgeNoteDraft("");
  };

  const addNoteNode = () => {
    const { canvas, addNodes, selectNode, pushUndoSnapshot } = useKontur.getState();
    pushUndoSnapshot();
    const el = containerRef.current;
    const r = el ? el.getBoundingClientRect() : { width: 900, height: 640 };
    const count = canvas.nodes.filter((n) => n.kind === "note").length;
    const wx = (r.width / 2 - canvas.panX) / canvas.zoom - NODE_W / 2;
    const wy = (r.height / 2 - canvas.panY) / canvas.zoom - 31 + count * 28;
    const node: CanvasNode = {
      id: `note-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`,
      kind: "note",
      title: t("canvas.note.nodeTitle", count + 1),
      meta: new Date().toLocaleDateString(),
      x: Math.round(wx),
      y: Math.round(wy),
    };
    addNodes([node]);
    selectNode(node.id);
    markInteracted();
    setNoteEditId(node.id);
    setNoteDraft("");
  };

  const onEdgePointerDown = (e: ReactPointerEvent<SVGPathElement>, edge: CanvasEdge) => {
    if (tool !== "select" || e.button !== 0) return;
    e.stopPropagation();
    useKontur.getState().setCanvas({
      selectedIds: [],
      primaryId: null,
      selectedEdgeId: edge.id,
    });
  };

  const onContainerDoubleClick = (e: ReactMouseEvent<HTMLDivElement>) => {
    if (e.target !== e.currentTarget) return;
    fitView();
  };

  const onContainerKeyDown = (e: ReactKeyboardEvent<HTMLDivElement>) => {
    /* typing inside a note editor must not trigger canvas shortcuts */
    if ((e.target as HTMLElement).closest?.("[data-note-editor]")) return;
    if ((e.target as HTMLElement).closest?.("[data-edge-note-editor]")) return;
    const mod = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();
    if (mod && key === "z") {
      e.preventDefault();
      if (e.shiftKey) useKontur.getState().redoCanvas();
      else useKontur.getState().undoCanvas();
      return;
    }
    if (mod && key === "y") {
      e.preventDefault();
      useKontur.getState().redoCanvas();
      return;
    }
    if (mod && key === "f") {
      /* canvas find — jumps straight into the search overlay */
      e.preventDefault();
      if (searchQuery === null) openSearch();
      else {
        searchInputRef.current?.focus();
        searchInputRef.current?.select();
      }
      return;
    }
    if (e.key === "Delete" || e.key === "Backspace") {
      e.preventDefault();
      deleteSelection();
      return;
    }
    if (e.key === "F2") {
      /* annotate the selection — node note or connection label */
      e.preventDefault();
      if (selectedEdgeId) {
        const edge = edges.find((x) => x.id === selectedEdgeId);
        if (edge) openEdgeNoteEditor(edge);
      } else if (primaryId) {
        const node = nodeById.get(primaryId);
        if (node) openNoteEditor(node);
      }
      return;
    }
    if (e.key === "Escape") {
      if (searchQuery !== null) {
        closeSearch();
        return;
      }
      useKontur.getState().clearSelection();
      return;
    }
    if (key === "+" || key === "=") {
      e.preventDefault();
      zoomCentered(1.25);
      return;
    }
    if (key === "-" || key === "_") {
      e.preventDefault();
      zoomCentered(0.8);
      return;
    }
    if (key === "0") {
      e.preventDefault();
      resetZoom();
    }
  };

  /* ---------- toolbar actions ---------- */

  const setTool = (next: "select" | "pan") => useKontur.getState().setCanvas({ tool: next });
  const toggleGrid = () => useKontur.getState().setCanvas({ showGrid: !showGrid });
  const askAi = () => {
    // Route the current selection into the chat composer so the real agent
    // can act on it; nothing selected just brings the chat forward.
    const { canvas: c, setSurface, setDraft, pushToast } = useKontur.getState();
    const sel = c.nodes.filter((n) => c.selectedIds.includes(n.id));
    if (sel.length > 0) {
      const refs = sel.map((n) => n.path ?? n.title).join(", ");
      setDraft(t("canvas.askAi.prompt", refs));
    } else {
      pushToast(t("canvas.askAi.toastTitle"), t("canvas.askAi.toastBody"));
    }
    setSurface("chat");
  };
  const onOpenWorkspace = () => {
    // Real folder-pick → open → resync, mirroring CommandPalette / SettingsView.
    // Only the browser prototype (no Electron bridge) falls back to the toast.
    if (!isServerMode() || !window.kontur) {
      useKontur.getState().pushToast(t("canvas.open"), t("canvas.open.toast"));
      return;
    }
    void (async () => {
      const dir = await window.kontur!.pickFolder();
      if (!dir) return;
      const { pushToast } = useKontur.getState();
      try {
        const res = await workspace.open(dir);
        await syncWorkspaceFiles().catch(() => undefined);
        // Rebuild the project graph so the canvas actually fills in — a fresh
        // open has an empty server graph until the folder is walked.
        await reindexCanvas().catch(() => undefined);
        pushToast(t("settings.workspace.opened", res.root) ?? res.root);
      } catch (err) {
        pushToast(t("canvas.open"), err instanceof Error ? err.message : undefined, "destructive");
      }
    })();
  };

  /* ---------- effects ---------- */

  /* measure the container */
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const measure = () => {
      const r = el.getBoundingClientRect();
      setSize((prev) =>
        Math.abs(prev.w - r.width) < 1 && Math.abs(prev.h - r.height) < 1
          ? prev
          : { w: r.width, h: r.height },
      );
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  /* fit the initial graph once, after first measurement */
  useEffect(() => {
    if (size.w < 40 || size.h < 40 || fittedRef.current) return;
    fittedRef.current = true;
    if (!canvasViewTouched) {
      const { canvas, setViewport } = useKontur.getState();
      const vp = computeFit(canvas.nodes, size.w, size.h);
      setViewport(vp.zoom, vp.panX, vp.panY);
    }
    containerRef.current?.focus({ preventScroll: true });
  }, [size]);

  /* Re-fit when the graph goes from empty to populated — i.e. right after a
     workspace is opened/reindexed. Server nodes carry their own coordinates,
     which can land well outside the current viewport; without this the canvas
     stays blank even though the nodes loaded. Fires for every open entry point
     (canvas button, command palette, settings) since it keys off the store. */
  const prevNodeCountRef = useRef(nodes.length);
  useEffect(() => {
    if (size.w < 40 || size.h < 40) return;
    const prev = prevNodeCountRef.current;
    prevNodeCountRef.current = nodes.length;
    if (prev === 0 && nodes.length > 0) {
      const { setViewport } = useKontur.getState();
      const vp = computeFit(nodes, size.w, size.h);
      setViewport(vp.zoom, vp.panX, vp.panY);
    }
  }, [nodes, size]);

  /* Open Workspace / reindex asks the canvas to fit the freshly-loaded graph.
     Covers the workspace-switch case too (node set changes N→M without ever
     hitting 0), which the empty→populated guard above would miss. Reads the
     store directly so it doesn't depend on React having re-rendered yet. */
  useEffect(() => {
    const onFit = () => {
      const el = containerRef.current;
      if (!el) return;
      const r = el.getBoundingClientRect();
      if (r.width < 40 || r.height < 40) return;
      const { canvas, setViewport } = useKontur.getState();
      if (canvas.nodes.length === 0) return;
      const vp = computeFit(canvas.nodes, r.width, r.height);
      markInteracted();
      setViewport(vp.zoom, vp.panX, vp.panY);
    };
    window.addEventListener(CANVAS_FIT_EVENT, onFit);
    return () => window.removeEventListener(CANVAS_FIT_EVENT, onFit);
  }, []);

  /* wheel: zoom-to-cursor (plain + ctrl/pinch), shift → horizontal pan */
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      /* let the note editors scroll their own text */
      if ((e.target as HTMLElement).closest?.("[data-note-editor]")) return;
      if ((e.target as HTMLElement).closest?.("[data-edge-note-editor]")) return;
      e.preventDefault();
      markInteracted();
      if (e.shiftKey && !e.ctrlKey && !e.metaKey) {
        useKontur.getState().panBy(-(e.deltaX || e.deltaY), 0);
        return;
      }
      const pinch = e.ctrlKey || e.metaKey;
      const factor = e.deltaY < 0 ? (pinch ? 1.16 : 1.12) : (pinch ? 0.86 : 0.89);
      const r = el.getBoundingClientRect();
      zoomAtScreen(factor, e.clientX - r.left, e.clientY - r.top);
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, []);

  /* graph outline asks the canvas to center on a node */
  useEffect(() => {
    const onFocusNode = (ev: Event) => {
      const detail = (ev as CustomEvent<{ id: string }>).detail;
      if (!detail?.id) return;
      const { canvas, setViewport } = useKontur.getState();
      const n = canvas.nodes.find((nn) => nn.id === detail.id);
      const el = containerRef.current;
      if (!n || !el) return;
      const r = el.getBoundingClientRect();
      setViewport(
        canvas.zoom,
        r.width / 2 - (n.x + nodeW(n) / 2) * canvas.zoom,
        r.height / 2 - (n.y + nodeH(n) / 2) * canvas.zoom,
      );
    };
    window.addEventListener(FOCUS_NODE_EVENT, onFocusNode);
    return () => window.removeEventListener(FOCUS_NODE_EVENT, onFocusNode);
  }, []);

  /* inspector asks the canvas to open the note editor */
  useEffect(() => {
    const onEditNote = (ev: Event) => {
      const detail = (ev as CustomEvent<{ id: string }>).detail;
      if (!detail?.id) return;
      const n = useKontur.getState().canvas.nodes.find((nn) => nn.id === detail.id);
      if (!n) return;
      setNoteEditId(n.id);
      setNoteDraft(n.note ?? "");
    };
    window.addEventListener(EDIT_NOTE_EVENT, onEditNote);
    return () => window.removeEventListener(EDIT_NOTE_EVENT, onEditNote);
  }, []);

  /* inspector asks the canvas to open the edge label editor */
  useEffect(() => {
    const onEditEdgeNote = (ev: Event) => {
      const detail = (ev as CustomEvent<{ id: string }>).detail;
      if (!detail?.id) return;
      const e = useKontur.getState().canvas.edges.find((ee) => ee.id === detail.id);
      if (!e) return;
      openEdgeNoteEditor(e);
    };
    window.addEventListener(EDIT_EDGE_NOTE_EVENT, onEditEdgeNote);
    return () => window.removeEventListener(EDIT_EDGE_NOTE_EVENT, onEditEdgeNote);
  }, []);

  /* focus + autosize the note editor when it opens */
  useEffect(() => {
    if (!noteEditId) return;
    const ta = noteTaRef.current;
    if (!ta) return;
    ta.focus();
    ta.setSelectionRange(ta.value.length, ta.value.length);
    ta.style.height = "auto";
    ta.style.height = `${Math.min(Math.max(ta.scrollHeight, 58), 220)}px`;
  }, [noteEditId]);

  /* focus + autosize the edge label editor when it opens */
  useEffect(() => {
    if (!edgeNoteEditId) return;
    const ta = edgeNoteTaRef.current;
    if (!ta) return;
    ta.focus();
    ta.setSelectionRange(ta.value.length, ta.value.length);
    ta.style.height = "auto";
    ta.style.height = `${Math.min(Math.max(ta.scrollHeight, 44), 160)}px`;
  }, [edgeNoteEditId]);

  /* Escape cancels an active gesture even without canvas focus */
  useEffect(() => {
    kindPickerRef.current = kindPicker;
  }, [kindPicker]);

  useEffect(() => {
    nodeMenuRef.current = nodeMenu;
  }, [nodeMenu]);

  useEffect(() => {
    bgMenuRef.current = bgMenu;
  }, [bgMenu]);

  useEffect(() => {
    edgeMenuRef.current = edgeMenu;
  }, [edgeMenu]);

  /* mirror open state for the always-on Escape listener */
  useEffect(() => {
    searchOpenRef.current = searchQuery !== null;
  }, [searchQuery]);

  /* focus the search input when the overlay opens */
  useEffect(() => {
    if (searchOpen) searchInputRef.current?.focus();
  }, [searchOpen]);

  /* live-jump to the first match while the query is typed */
  useEffect(() => {
    if (searchQuery === null || q === "") return;
    const el = containerRef.current;
    if (!el) return;
    const matches = buildMatches(nodes, visibleEdges, q);
    if (matches.length > 0) centerOnMatch(matches[0], el);
  }, [q, searchQuery, nodes, visibleEdges]);

  /* tell the global Escape chain the canvas owns the key while any local
   *  overlay or gesture is open (picker / search / connect / reconnect) */
  const overlayActive =
    Boolean(kindPicker) || Boolean(nodeMenu) || Boolean(bgMenu) || Boolean(edgeMenu) || searchQuery !== null || gesture !== "none";
  useEffect(() => {
    useKontur.getState().setCanvasOverlayActive(overlayActive);
    return () => {
      useKontur.getState().setCanvasOverlayActive(false);
    };
  }, [overlayActive]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (kindPickerRef.current) {
        /* picker first: keeps the just-created edge with the suggested kind */
        setKindPicker(null);
        return;
      }
      if (nodeMenuRef.current) {
        setNodeMenu(null);
        return;
      }
      if (edgeMenuRef.current) {
        setEdgeMenu(null);
        return;
      }
      if (bgMenuRef.current) {
        setBgMenu(null);
        return;
      }
      if (searchOpenRef.current) {
        setSearchQuery(null);
        setSearchIdx(0);
        return;
      }
      if (reconnectRef.current) {
        clearReconnect();
      } else if (connectRef.current) {
        clearConnect();
      } else if (dragRef.current) {
        cancelDrag();
      } else if (marqueeRef.current) {
        marqueeRef.current = null;
        setMarquee(null);
        setGesture("none");
      } else if (panRef.current) {
        panRef.current = null;
        setGesture("none");
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  /* ---------- render ---------- */

  const gridStyle: CSSProperties = showGrid
    ? {
        backgroundImage: "radial-gradient(circle, var(--kc-canvas-grid) 1px, transparent 1px)",
        backgroundSize: `${Math.max(24 * zoom, 6)}px ${Math.max(24 * zoom, 6)}px`,
        backgroundPosition: `${panX}px ${panY}px`,
      }
    : {};

  return (
    <div
      ref={containerRef}
      tabIndex={0}
      role="application"
      aria-label={t("canvas.title")}
      className={cn(
        "relative h-full w-full touch-none select-none overflow-hidden bg-canvas outline-none focus-visible:ring-1 focus-visible:ring-accent/25",
        tool === "pan" ? "cursor-grab" : "cursor-default",
        gesture === "pan" && "cursor-grabbing",
        (gesture === "marquee" || gesture === "connect" || gesture === "reconnect") && "cursor-crosshair",
        gesture !== "none" && "kc-noselect",
      )}
      style={gridStyle}
      onPointerDown={onContainerPointerDown}
      onPointerMove={onContainerPointerMove}
      onPointerUp={onContainerPointerUp}
      onPointerCancel={onContainerPointerCancel}
      onDoubleClick={onContainerDoubleClick}
      onContextMenu={onContainerContextMenu}
      onKeyDown={onContainerKeyDown}
    >
      {/* ---- edges (screen space, under the nodes) ---- */}
      <svg data-canvas-edges className="pointer-events-none absolute inset-0 z-0 h-full w-full overflow-visible" aria-hidden>
        <defs>
          <marker
            id="kc-canvas-arrow"
            viewBox="0 0 8 8"
            markerWidth={8}
            markerHeight={8}
            refX={7}
            refY={4}
            orient="auto"
            markerUnits="userSpaceOnUse"
          >
            <path d="M0 0 L8 4 L0 8 Z" fill="var(--kc-fg-3)" />
          </marker>
          <marker
            id="kc-canvas-arrow-hi"
            viewBox="0 0 8 8"
            markerWidth={8}
            markerHeight={8}
            refX={7}
            refY={4}
            orient="auto"
            markerUnits="userSpaceOnUse"
          >
            <path d="M0 0 L8 4 L0 8 Z" fill="var(--kc-fg-1)" />
          </marker>
          <marker
            id="kc-canvas-arrow-connect"
            viewBox="0 0 8 8"
            markerWidth={8}
            markerHeight={8}
            refX={7}
            refY={4}
            orient="auto"
            markerUnits="userSpaceOnUse"
          >
            <path d="M0 0 L8 4 L0 8 Z" fill="var(--kc-accent)" />
          </marker>
        </defs>
        {edges.map((edge) => {
          const a = nodeById.get(edge.from);
          const b = nodeById.get(edge.to);
          if (!a || !b) return null;
          /* filter mode: edges need both endpoints visible */
          if (visibleNodeIds && !(visibleNodeIds.has(edge.from) && visibleNodeIds.has(edge.to))) {
            return null;
          }
          /* legend visibility toggles — hidden kinds are not drawn at all */
          if (hiddenKindSet.has(edge.kind)) return null;
          const d = edgeGeometry(a, b, zoom, panX, panY);
          if (!d) return null;
          const isSel = selectedEdgeId === edge.id;
          const isHover = hoverEdgeId === edge.id;
          const searchHit = searchActive && edgeMatchIds.has(edge.id);
          const highlighted =
            isSel || isHover || selectedEdgeSet.has(edge.id) || selectedSet.has(edge.from) || selectedSet.has(edge.to) || searchHit;
          const dimmed = (hasSelection || searchDimming) && !highlighted;
          /* endpoint handles — retarget the edge by dragging (drawable kinds only) */
          const endpoints =
            isSel && DRAWABLE_EDGE_KINDS.includes(edge.kind)
              ? edgeEndpoints(a, b, zoom, panX, panY)
              : null;
          return (
            <g key={edge.id} opacity={dimmed ? 0.28 : highlighted ? 1 : 0.62}>
              <path
                d={d}
                fill="none"
                stroke={highlighted ? "var(--kc-fg-1)" : "var(--kc-fg-3)"}
                strokeWidth={highlighted ? Math.max(edgeWidth(edge.kind), 1.8) : edgeWidth(edge.kind)}
                strokeDasharray={edgeDash(edge.kind)}
                markerEnd={`url(#${highlighted ? "kc-canvas-arrow-hi" : "kc-canvas-arrow"})`}
                style={{ transition: "stroke-width 80ms ease, opacity 80ms ease" }}
              />
              <path
                d={d}
                fill="none"
                stroke="transparent"
                strokeWidth={12}
                style={{ pointerEvents: tool === "select" ? "stroke" : "none", cursor: "pointer" }}
                onPointerDown={(e) => onEdgePointerDown(e, edge)}
                onDoubleClick={() => openEdgeNoteEditor(edge)}
                onContextMenu={(e) => onEdgeContextMenu(e, edge)}
                onPointerEnter={() => setHoverEdgeId(edge.id)}
                onPointerLeave={() => setHoverEdgeId((cur) => (cur === edge.id ? null : cur))}
              />
              {endpoints && (() => {
                /* push each handle outward along the edge so it clears the node card */
                const ux = (endpoints.tx - endpoints.sx) / (Math.hypot(endpoints.tx - endpoints.sx, endpoints.ty - endpoints.sy) || 1);
                const uy = (endpoints.ty - endpoints.sy) / (Math.hypot(endpoints.tx - endpoints.sx, endpoints.ty - endpoints.sy) || 1);
                const handle = (x: number, y: number, end: "from" | "to") => (
                  <g
                    key={end}
                    style={{ cursor: "grab" }}
                    onPointerDown={(e) => onReconnectStart(e, edge, end)}
                  >
                    <title>{t("canvas.reconnect.title")}</title>
                    <circle
                      cx={x}
                      cy={y}
                      r={11}
                      fill="transparent"
                      style={{ pointerEvents: "auto" }}
                    />
                    <circle
                      cx={x}
                      cy={y}
                      r={5.5}
                      fill="var(--kc-surface-1)"
                      stroke="var(--kc-accent)"
                      strokeWidth={1.5}
                      pointerEvents="none"
                    />
                    <circle cx={x} cy={y} r={2} fill="var(--kc-accent)" pointerEvents="none" />
                  </g>
                );
                return (
                  <>
                    {handle(endpoints.sx + ux * 9, endpoints.sy + uy * 9, "from")}
                    {handle(endpoints.tx - ux * 9, endpoints.ty - uy * 9, "to")}
                  </>
                );
              })()}
            </g>
          );
        })}

        {/* live connect-drag preview: every source port → cursor (bulk = fan) */}
        {connect && (() => {
          const sources = connect.sources
            .map((id) => nodeById.get(id))
            .filter((n): n is CanvasNode => Boolean(n));
          if (sources.length === 0) return null;
          const valid = connect.hoverId && connect.hoverId !== connect.fromId;
          const curve = (n: CanvasNode, primary: boolean) => {
            const p = portScreen(n);
            const dx = connect.cx - p.x;
            const dy = connect.cy - p.y;
            const dist = Math.max(Math.hypot(dx, dy), 1);
            const ax = (dx / dist) * Math.min(dist * 0.35, 70);
            const ay = (dy / dist) * Math.min(dist * 0.35, 70);
            return (
              <path
                key={n.id}
                d={`M ${p.x} ${p.y} C ${p.x + ax} ${p.y + ay}, ${connect.cx - ax} ${connect.cy - ay}, ${connect.cx} ${connect.cy}`}
                fill="none"
                stroke="var(--kc-accent)"
                strokeWidth={primary ? 1.6 : 1.2}
                strokeDasharray="5 4"
                opacity={primary ? 1 : 0.45}
                markerEnd="url(#kc-canvas-arrow-connect)"
              />
            );
          };
          return (
            <g>
              {sources.map((n) => curve(n, n.id === connect.fromId))}
              {valid && (() => {
                const target = nodeById.get(connect.hoverId!);
                if (!target) return null;
                const tx = target.x * zoom + panX;
                const ty = target.y * zoom + panY;
                const tw = nodeW(target) * zoom;
                const th = nodeH(target) * zoom;
                return (
                  <rect
                    x={tx - 4}
                    y={ty - 4}
                    width={tw + 8}
                    height={th + 8}
                    rx={8}
                    fill="var(--kc-accent-soft)"
                    stroke="var(--kc-accent)"
                    strokeWidth={1.2}
                  />
                );
              })()}
            </g>
          );
        })()}

        {/* live reconnect preview: fixed endpoint node center → cursor */}
        {reconnect && (() => {
          const edge = edges.find((x) => x.id === reconnect.edgeId);
          if (!edge) return null;
          const fixedId = reconnect.end === "from" ? edge.to : edge.from;
          const fixed = nodeById.get(fixedId);
          if (!fixed) return null;
          const cx0 = (fixed.x + nodeW(fixed) / 2) * zoom + panX;
          const cy0 = (fixed.y + nodeH(fixed) / 2) * zoom + panY;
          const dx = reconnect.cx - cx0;
          const dy = reconnect.cy - cy0;
          const dist = Math.max(Math.hypot(dx, dy), 1);
          const ax = (dx / dist) * Math.min(dist * 0.35, 70);
          const ay = (dy / dist) * Math.min(dist * 0.35, 70);
          const target = reconnect.hoverId ? nodeById.get(reconnect.hoverId) : null;
          return (
            <g>
              <path
                d={`M ${cx0} ${cy0} C ${cx0 + ax} ${cy0 + ay}, ${reconnect.cx - ax} ${reconnect.cy - ay}, ${reconnect.cx} ${reconnect.cy}`}
                fill="none"
                stroke="var(--kc-accent)"
                strokeWidth={1.6}
                strokeDasharray="5 4"
                markerEnd="url(#kc-canvas-arrow-connect)"
              />
              {/* the traveling handle rides the cursor */}
              <circle
                cx={reconnect.cx}
                cy={reconnect.cy}
                r={6}
                fill="var(--kc-accent)"
                stroke="var(--kc-surface-1)"
                strokeWidth={2}
              />
              {target && target.id !== fixedId && (() => {
                const tx = target.x * zoom + panX;
                const ty = target.y * zoom + panY;
                const tw = nodeW(target) * zoom;
                const th = nodeH(target) * zoom;
                return (
                  <rect
                    x={tx - 4}
                    y={ty - 4}
                    width={tw + 8}
                    height={th + 8}
                    rx={8}
                    fill="var(--kc-accent-soft)"
                    stroke="var(--kc-accent)"
                    strokeWidth={1.2}
                  />
                );
              })()}
            </g>
          );
        })()}

        {/* alignment snap guides — full-length dashed accent lines */}
        {snapGuides && (() => {
          const xs = snapGuides.x.map((w) => w * zoom + panX);
          const ys = snapGuides.y.map((w) => w * zoom + panY);
          return (
            <g pointerEvents="none">
              {xs.map((sx) => (
                <line
                  key={`gx${sx.toFixed(1)}`}
                  x1={sx}
                  y1={0}
                  x2={sx}
                  y2={size.h}
                  stroke="var(--kc-accent)"
                  strokeWidth={1}
                  strokeDasharray="4 4"
                  opacity={0.9}
                />
              ))}
              {ys.map((sy) => (
                <line
                  key={`gy${sy.toFixed(1)}`}
                  x1={0}
                  y1={sy}
                  x2={size.w}
                  y2={sy}
                  stroke="var(--kc-accent)"
                  strokeWidth={1}
                  strokeDasharray="4 4"
                  opacity={0.9}
                />
              ))}
            </g>
          );
        })()}

        {/* snap distance badge — drafting-style dimension line with end caps.
            Proximity variant: muted stroke (no snap happened, informational) */}
        {snapGuides?.badge && (() => {
          const b = snapGuides.badge;
          const fixed = Math.round(b.at * zoom + (b.axis === "v" ? panX : panY));
          const from = Math.round(b.from * zoom + (b.axis === "v" ? panY : panX));
          const to = Math.round(b.to * zoom + (b.axis === "v" ? panY : panX));
          const cap = 4;
          const isV = b.axis === "v";
          const stroke = b.proximity ? "var(--kc-fg-3)" : "var(--kc-accent)";
          const opacity = b.proximity ? 0.55 : 0.85;
          return (
            <g pointerEvents="none">
              <line
                x1={isV ? fixed : from}
                y1={isV ? from : fixed}
                x2={isV ? fixed : to}
                y2={isV ? to : fixed}
                stroke={stroke}
                strokeWidth={1}
                opacity={opacity}
              />
              <line
                x1={isV ? fixed - cap : from}
                y1={isV ? from : fixed - cap}
                x2={isV ? fixed + cap : from}
                y2={isV ? from : fixed + cap}
                stroke={stroke}
                strokeWidth={1}
                opacity={opacity}
              />
              <line
                x1={isV ? fixed - cap : to}
                y1={isV ? to : fixed - cap}
                x2={isV ? fixed + cap : to}
                y2={isV ? to : fixed + cap}
                stroke={stroke}
                strokeWidth={1}
                opacity={opacity}
              />
            </g>
          );
        })()}
      </svg>

      {/* snap distance badge chip — the measured gap in world px.
          Proximity variant: muted border/text (informational, not snapped) */}
      {snapGuides?.badge && (() => {
        const b = snapGuides.badge;
        const isV = b.axis === "v";
        const fixed = b.at * zoom + (isV ? panX : panY);
        const mid = ((b.from + b.to) / 2) * zoom + (isV ? panY : panX);
        return (
          <div className="pointer-events-none absolute inset-0 z-20" aria-hidden>
            <div
              style={{ left: Math.round(isV ? fixed : mid), top: Math.round(isV ? mid : fixed) }}
              className={cn(
                "absolute flex h-[18px] -translate-x-1/2 -translate-y-1/2 items-center rounded-xs border bg-surface-2/95 px-1.5 shadow-subtle backdrop-blur",
                b.proximity ? "border-line" : "border-accent-border",
              )}
            >
              <span
                className={cn(
                  "font-mono text-[10px] font-medium leading-none tabular-nums",
                  b.proximity ? "text-fg-2" : "text-accent",
                )}
              >
                {Math.round(b.gap)}
              </span>
            </div>
          </div>
        );
      })()}

      {/* ---- connection labels (persistent, clickable) ----
           Edges carrying a note show a sticky chip at their midpoint —
           click to edit, always visible (unlike the transient kind chips). */}
      {(() => {
        const notes = edges.flatMap((edge) => {
          if (!edge.note) return [];
          const a = nodeById.get(edge.from);
          const b = nodeById.get(edge.to);
          if (!a || !b) return [];
          if (visibleNodeIds && !(visibleNodeIds.has(edge.from) && visibleNodeIds.has(edge.to))) return [];
          if (hiddenKindSet.has(edge.kind)) return [];
          const d = edgeGeometry(a, b, zoom, panX, panY);
          if (!d) return [];
          const m = edgeMidScreen(a, b, zoom, panX, panY);
          const isSel = selectedEdgeId === edge.id;
          const isHover = hoverEdgeId === edge.id;
          const highlighted =
            isSel || isHover || selectedEdgeSet.has(edge.id) || selectedSet.has(edge.from) || selectedSet.has(edge.to);
          const dimmed = (hasSelection || searchDimming) && !highlighted;
          return [{ edge, m, dimmed }];
        });
        if (notes.length === 0) return null;
        /* far zoom: labels collapse to a sticky marker (text would smear
         *  into noise once the edge itself is a few px long) */
        const compact = zoom < 0.45;
        return (
          <div className="pointer-events-none absolute inset-0 z-20">
            {notes.map(({ edge, m, dimmed }) => (
              <button
                key={edge.id}
                type="button"
                data-canvas-overlay
                onPointerDown={(e) => e.stopPropagation()}
                onClick={() => openEdgeNoteEditor(edge)}
                aria-label={`${t("canvas.edgenote.chipLabel")} ${edge.note}`}
                title={edge.note}
                style={{ left: Math.round(m.x), top: Math.round(m.y) }}
                className={cn(
                  "kc-focus-ring group/n pointer-events-auto absolute flex -translate-x-1/2 -translate-y-1/2 items-center gap-1 rounded-xs border bg-surface-2/95 shadow-subtle backdrop-blur transition-[opacity,border-color,max-width] duration-100",
                  compact ? "h-[16px] w-[16px] justify-center px-0" : "h-[18px] max-w-[168px] pl-1 pr-1.5",
                  dimmed ? "opacity-30" : "opacity-100",
                  edgeNoteEditId === edge.id
                    ? "border-warning/60"
                    : "border-warning/35 hover:border-warning/70",
                )}
              >
                <StickyNote size={compact ? 8 : 9} className="shrink-0 text-warning" aria-hidden />
                {!compact && (
                  <span className="truncate text-[10px] font-medium leading-none text-fg-2">
                    {edge.note}
                  </span>
                )}
                {!compact && (
                  <PenLine
                    size={9}
                    className="shrink-0 text-fg-3/0 transition-colors duration-100 group-hover/n:text-warning"
                    aria-hidden
                  />
                )}
              </button>
            ))}
          </div>
        );
      })()}

      {/* ---- edge kind chips (hovered / selected / search-hit) ----
           HTML overlay so they can carry a Lucide icon beside the label.
           A label chip already occupying the midpoint pushes the kind chip
           onto the line above it. */}
      {(() => {
        const chips = edges.flatMap((edge) => {
          const a = nodeById.get(edge.from);
          const b = nodeById.get(edge.to);
          if (!a || !b) return [];
          if (visibleNodeIds && !(visibleNodeIds.has(edge.from) && visibleNodeIds.has(edge.to))) return [];
          if (hiddenKindSet.has(edge.kind)) return [];
          const isSel = selectedEdgeId === edge.id;
          const isHover = hoverEdgeId === edge.id;
          const searchHit = searchActive && edgeMatchIds.has(edge.id);
          if (!(isSel || isHover || searchHit)) return [];
          const m = edgeMidScreen(a, b, zoom, panX, panY);
          const Icon = EDGE_KIND_ICONS[edge.kind];
          return [{ id: edge.id, kind: edge.kind, m, isSel, Icon, hasNote: Boolean(edge.note) }];
        });
        if (chips.length === 0) return null;
        return (
          <div className="pointer-events-none absolute inset-0 z-20" aria-hidden>
            {chips.map((c) => (
              <div
                key={c.id}
                style={{ left: Math.round(c.m.x), top: Math.round(c.m.y - (c.hasNote ? 11 : 0)) }}
                className={cn(
                  "absolute flex h-[17px] -translate-x-1/2 -translate-y-1/2 items-center gap-1 rounded-xs border px-1.5 shadow-subtle backdrop-blur",
                  c.isSel
                    ? "border-accent-border bg-surface-2/95"
                    : "border-line bg-surface-2/95",
                )}
              >
                <c.Icon size={9} className={c.isSel ? "text-accent" : "text-fg-2"} aria-hidden />
                <span
                  className={cn(
                    "font-mono text-[9px] uppercase leading-none tracking-[0.08em]",
                    c.isSel ? "text-accent" : "text-fg-2",
                  )}
                >
                  {c.kind}
                </span>
              </div>
            ))}
          </div>
        );
      })()}

      {/* ---- nodes (world space, scaled container) ---- */}
      <div
        className="absolute left-0 top-0 z-10"
        style={{
          transform: `translate(${panX}px, ${panY}px) scale(${zoom})`,
          transformOrigin: "0 0",
        }}
      >
        {(visibleNodeIds ? nodes.filter((n) => visibleNodeIds.has(n.id)) : nodes).map((n) => (
          <CanvasNodeCard
            key={n.id}
            node={n}
            selected={selectedSet.has(n.id)}
            primary={primaryId === n.id}
            tool={tool}
            editingNote={noteEditId === n.id}
            connectingFrom={connect?.sources.includes(n.id)}
            connectTarget={connect?.hoverId === n.id}
            searchHit={searchActive && nodeMatchIds.has(n.id)}
            searchDim={searchDimming && !nodeMatchIds.has(n.id)}
            onPointerDown={onNodePointerDown}
            onStartConnect={onStartConnect}
            onDoubleClick={onNodeDoubleClick}
            onContextMenu={onNodeContextMenu}
            onEditNote={openNoteEditor}
            noteLabel={t("canvas.note.label")}
            noteEditLabel={t("canvas.note.edit")}
            connectLabel={t("canvas.connect.start")}
          />
        ))}

        {/* ---- note editor (popover below the node, world space) ---- */}
        {noteNode && (
          <div
            data-canvas-overlay
            data-note-editor
            className="absolute z-30"
            style={{
              left: noteNode.x,
              top: noteNode.y + nodeH(noteNode) + 8,
              width: nodeW(noteNode),
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onMouseDown={(e) => e.preventDefault()}
          >
            <div className="rounded-md border border-accent-border bg-surface-3 p-2 shadow-overlay animate-rise">
              <div className="mb-1.5 flex items-center gap-1.5">
                <StickyNote size={10} className="shrink-0 text-accent" aria-hidden />
                <span className="kc-overline text-accent">{t("canvas.note.editorTitle")}</span>
                <span className="ml-auto font-mono text-[9px] text-fg-3">esc</span>
              </div>
              <textarea
                ref={noteTaRef}
                value={noteDraft}
                onChange={(e) => {
                  setNoteDraft(e.target.value);
                  requestAnimationFrame(autosizeNote);
                }}
                onBlur={saveNoteEditor}
                onKeyDown={(e) => {
                  if (e.key === "Escape") {
                    e.preventDefault();
                    e.stopPropagation();
                    saveNoteEditor();
                    containerRef.current?.focus({ preventScroll: true });
                  }
                }}
                placeholder={t("canvas.note.placeholder")}
                rows={3}
                aria-label={t("canvas.note.editorTitle")}
                className="kc-focus-ring block w-full resize-none rounded-sm border border-line-faint bg-surface-1 px-2 py-1.5 font-mono text-[10.5px] leading-relaxed text-fg-1 outline-none placeholder:text-fg-3/70 focus:border-accent-border"
              />
              <div className="mt-1.5 flex items-center justify-between gap-2">
                <span className="text-[9.5px] leading-none text-fg-3">{t("canvas.note.hint")}</span>
                {noteNode.note && (
                  <button
                    type="button"
                    onClick={removeNote}
                    className="kc-focus-ring flex h-[18px] items-center gap-1 rounded-xs px-1.5 text-[9.5px] font-medium leading-none text-fg-3 transition-colors hover:bg-error-soft hover:text-error"
                  >
                    <Trash2 size={9} aria-hidden />
                    {t("canvas.note.remove")}
                  </button>
                )}
              </div>
            </div>
          </div>
        )}

        {/* ---- edge label editor (popover at the edge midpoint, screen space) ---- */}
        {edgeNoteEdge && (() => {
          const a = nodeById.get(edgeNoteEdge.from);
          const b = nodeById.get(edgeNoteEdge.to);
          if (!a || !b) return null;
          const m = edgeMidScreen(a, b, zoom, panX, panY);
          const editorW = 208;
          const x = Math.max(8, Math.min(Math.round(m.x - editorW / 2), Math.max(size.w - editorW - 8, 8)));
          const y = Math.max(8, Math.min(Math.round(m.y + 14), Math.max(size.h - 190, 8)));
          return (
            <div
              data-canvas-overlay
              data-edge-note-editor
              className="absolute z-30"
              style={{ left: x, top: y, width: editorW }}
              onPointerDown={(e) => e.stopPropagation()}
              onMouseDown={(e) => e.preventDefault()}
            >
              <div className="rounded-md border border-warning/60 bg-surface-3 p-2 shadow-overlay animate-rise">
                <div className="mb-1.5 flex items-center gap-1.5">
                  <StickyNote size={10} className="shrink-0 text-warning" aria-hidden />
                  <span className="kc-overline text-warning">{t("canvas.edgenote.editorTitle")}</span>
                  <span className="ml-auto font-mono text-[9px] text-fg-3">enter ⏎ · esc</span>
                </div>
                <textarea
                  ref={edgeNoteTaRef}
                  value={edgeNoteDraft}
                  onChange={(e) => {
                    setEdgeNoteDraft(e.target.value);
                    requestAnimationFrame(autosizeEdgeNote);
                  }}
                  onBlur={saveEdgeNoteEditor}
                  onKeyDown={(e) => {
                    if (e.key === "Escape" || (e.key === "Enter" && !e.shiftKey)) {
                      e.preventDefault();
                      e.stopPropagation();
                      saveEdgeNoteEditor();
                      containerRef.current?.focus({ preventScroll: true });
                    }
                  }}
                  placeholder={t("canvas.edgenote.placeholder")}
                  rows={2}
                  aria-label={t("canvas.edgenote.editorTitle")}
                  aria-keyshortcuts="Enter Escape"
                  className="kc-focus-ring block w-full resize-none rounded-sm border border-line-faint bg-surface-1 px-2 py-1.5 font-mono text-[10.5px] leading-relaxed text-fg-1 outline-none placeholder:text-fg-3/70 focus:border-warning/60"
                />
                <div className="mt-1.5 flex items-center justify-between gap-2">
                  <span className="truncate font-mono text-[9px] leading-none text-fg-3" title={`${a.title} → ${b.title}`}>
                    {a.title} → {b.title}
                  </span>
                  {edgeNoteEdge.note && (
                    <button
                      type="button"
                      onClick={removeEdgeNote}
                      className="kc-focus-ring flex h-[18px] shrink-0 items-center gap-1 rounded-xs px-1.5 text-[9.5px] font-medium leading-none text-fg-3 transition-colors hover:bg-error-soft hover:text-error"
                    >
                      <Trash2 size={9} aria-hidden />
                      {t("canvas.edgenote.remove")}
                    </button>
                  )}
                </div>
              </div>
            </div>
          );
        })()}
      </div>

      {/* ---- marquee ---- */}
      {marquee && (
        <div
          aria-hidden
          className="pointer-events-none absolute z-20 border border-dashed border-line-strong bg-surface-hover/20"
          style={{ left: marquee.x, top: marquee.y, width: marquee.w, height: marquee.h }}
        />
      )}

      {/* ---- stats chip (click = fit) + hover/focus line legend ---- */}
      <div
        data-canvas-overlay
        className="absolute bottom-3 left-3 z-30 max-sm:bottom-14"
        onPointerEnter={() => {
          if (legendTimer.current) {
            clearTimeout(legendTimer.current);
            legendTimer.current = null;
          }
          setLegendOpen(true);
        }}
        onPointerLeave={() => {
          if (legendTimer.current) clearTimeout(legendTimer.current);
          legendTimer.current = window.setTimeout(() => setLegendOpen(false), 160);
        }}
        onFocus={() => {
          if (legendTimer.current) {
            clearTimeout(legendTimer.current);
            legendTimer.current = null;
          }
          setLegendOpen(true);
        }}
        onBlur={() => {
          if (legendTimer.current) clearTimeout(legendTimer.current);
          legendTimer.current = window.setTimeout(() => setLegendOpen(false), 160);
        }}
      >
        <button
          type="button"
          onClick={fitView}
          title={t("canvas.fit")}
          aria-label={t("canvas.fit")}
          className="kc-focus-ring flex items-center gap-1 rounded-md border border-line bg-surface-1/90 px-2.5 py-1 font-mono text-[11px] leading-none text-fg-3 transition-colors duration-100 hover:border-line-strong hover:text-fg-2"
        >
          <span>
            {t("status.counts", nodes.length, edges.length - edges.filter((e) => hiddenKindSet.has(e.kind)).length)}
            {selectedIds.length > 0 && ` · ${t("canvas.selectedCount", selectedIds.length)}`}
            {selectedEdgeIds.length > 1 && ` · ${t("canvas.selectedEdgeCount", selectedEdgeIds.length)}`}
            {hiddenEdgeKinds.length > 0 &&
              ` · ${t("canvas.hiddenEdgeCount", edges.filter((e) => hiddenKindSet.has(e.kind)).length)}`}
            {` · ${t("status.zoom", Math.round(zoom * 100))}`}
          </span>
          <Info size={10} className="shrink-0 text-fg-3/70 transition-colors group-hover:text-fg-2" aria-hidden />
        </button>
        {legendOpen && (
          <div
            className="absolute bottom-full left-0 mb-2 w-[264px] animate-rise rounded-md border border-line bg-surface-2/95 p-2 shadow-overlay backdrop-blur"
            aria-label={t("canvas.legend.title")}
          >
            <div className="flex items-center gap-1.5 px-1 pb-1.5 pt-0.5">
              <Link2 size={10} className="shrink-0 text-accent" aria-hidden />
              <span className="kc-overline text-accent">{t("canvas.legend.title")}</span>
              {hiddenEdgeKinds.length > 0 && (
                <span className="ml-auto rounded-xs border border-line px-1 py-px font-mono text-[8.5px] uppercase leading-[1.4] text-fg-3">
                  {t("canvas.legend.hiddenCount", hiddenEdgeKinds.length)}
                </span>
              )}
            </div>
            {ALL_EDGE_KINDS.map((kind) => {
              const hidden = hiddenKindSet.has(kind);
              const count = edges.filter((e) => e.kind === kind).length;
              return (
                <div key={kind}>
                  {kind === "contains" && (
                    <div className="mx-1 mb-1 mt-1.5 flex items-center gap-1.5 border-t border-line-faint px-0.5 pt-1.5">
                      <span className="kc-overline text-fg-3">{t("canvas.legend.structural")}</span>
                    </div>
                  )}
                  <button
                    type="button"
                    role="switch"
                    aria-checked={!hidden}
                    disabled={count === 0 && !hidden}
                    title={hidden ? t("canvas.legend.show", t(`edgekind.${kind}`)) : t("canvas.legend.hide", t(`edgekind.${kind}`))}
                    onClick={() =>
                      useKontur.getState().setCanvas({
                        hiddenEdgeKinds: hidden
                          ? hiddenEdgeKinds.filter((k) => k !== kind)
                          : [...hiddenEdgeKinds, kind],
                      })
                    }
                    className={cn(
                      "kc-focus-ring group/lg flex w-full items-center gap-2.5 rounded-sm px-1.5 py-1.5 text-left transition-colors duration-100",
                      count === 0 && !hidden
                        ? "cursor-default opacity-45 hover:bg-transparent"
                        : "hover:bg-surface-hover",
                      hidden && "opacity-55",
                    )}
                  >
                    <svg width="30" height="8" viewBox="0 0 30 8" className="shrink-0" aria-hidden>
                      <line
                        x1={1}
                        y1={4}
                        x2={29}
                        y2={4}
                        stroke={hidden ? "var(--kc-fg-3)" : "var(--kc-fg-2)"}
                        strokeWidth={kind === "calls" ? 1.6 : 1.2}
                        strokeDasharray={hidden ? "2 3" : edgeDash(kind)}
                        strokeLinecap="round"
                        opacity={hidden ? 0.55 : 1}
                      />
                    </svg>
                    <span className="min-w-0 flex-1">
                      <span
                        className={cn(
                          "block truncate text-[11.5px] font-medium leading-tight",
                          hidden ? "text-fg-3 line-through decoration-fg-3/60" : "text-fg-1",
                        )}
                      >
                        {t(`edgekind.${kind}`)}
                      </span>
                      <span className="block truncate text-[10px] leading-tight text-fg-3">
                        {t(`edgekind.desc.${kind}`)}
                      </span>
                    </span>
                    <span className="flex shrink-0 items-center gap-1">
                      <span
                        className={cn(
                          "rounded-xs border px-1 py-px font-mono text-[9px] leading-[1.4] tabular-nums",
                          hidden ? "border-line-faint text-fg-3" : "border-line-faint bg-sunken text-fg-2",
                        )}
                      >
                        {count}
                      </span>
                      <span
                        className={cn(
                          "flex h-4 w-4 items-center justify-center rounded-xs transition-colors",
                          hidden
                            ? "text-fg-3 group-hover/lg:text-fg-2"
                            : "text-fg-3/0 group-hover/lg:text-fg-3",
                        )}
                        aria-hidden
                      >
                        {hidden ? <EyeOff size={11} /> : <Eye size={11} />}
                      </span>
                    </span>
                  </button>
                </div>
              );
            })}
            {hiddenEdgeKinds.length > 0 && (
              <button
                type="button"
                onClick={() => useKontur.getState().setCanvas({ hiddenEdgeKinds: [] })}
                className="kc-focus-ring mt-1 flex w-full items-center justify-center gap-1.5 rounded-sm border border-line-faint bg-surface-1 px-2 py-1.5 text-[11px] font-medium text-fg-2 transition-colors duration-100 hover:border-line-strong hover:text-fg-1"
              >
                <Eye size={11} aria-hidden />
                {t("canvas.legend.showAll")}
              </button>
            )}
            <div className="mt-1 border-t border-line-faint px-1 pt-1.5">
              <span className="text-[9.5px] leading-snug text-fg-3">{t("canvas.legend.hint")}</span>
            </div>
          </div>
        )}
      </div>

      {/* ---- post-drop connection-type picker ---- */}
      {kindPicker && (
        <div
          data-canvas-overlay
          className="absolute z-40 w-[208px] animate-rise rounded-md border border-accent-border/70 bg-surface-2 p-1.5 shadow-overlay"
          style={{
            left: Math.max(8, Math.min(kindPicker.x - 104, Math.max(size.w - 216, 8))),
            top: Math.max(8, Math.min(kindPicker.y + 14, Math.max(size.h - 190, 8))),
          }}
          role="menu"
          aria-label={t("canvas.connect.kindTitle")}
          onPointerDown={(e) => e.stopPropagation()}
        >
          <div className="flex items-center gap-1.5 px-1 pb-1.5 pt-0.5">
            <Link2 size={10} className="shrink-0 text-accent" aria-hidden />
            <span className="kc-overline text-accent">{t("canvas.connect.kindTitle")}</span>
            {kindPicker.edgeIds.length > 1 && (
              <span className="ml-auto font-mono text-[9px] leading-none text-fg-3">
                ×{kindPicker.edgeIds.length}
              </span>
            )}
          </div>
          {DRAWABLE_EDGE_KINDS.map((kind) => {
            const Icon = EDGE_KIND_ICONS[kind];
            const isSuggested = kind === kindPicker.suggested;
            return (
              <button
                key={kind}
                type="button"
                role="menuitem"
                data-suggested={isSuggested ? "true" : undefined}
                onClick={() => {
                  const { updateEdgeKind } = useKontur.getState();
                  for (const id of kindPicker.edgeIds) updateEdgeKind(id, kind);
                  setKindPicker(null);
                }}
                className={cn(
                  "kc-focus-ring group/k flex w-full items-center gap-2 rounded-sm border px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-accent-border/60 hover:bg-accent-soft",
                  isSuggested ? "border-accent-border/70 bg-accent-soft/30" : "border-transparent",
                )}
              >
                <span
                  className={cn(
                    "flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border bg-surface-1 text-fg-3 transition-colors group-hover/k:border-accent-border group-hover/k:text-accent",
                    isSuggested ? "border-accent-border text-accent" : "border-line-faint",
                  )}
                >
                  <Icon size={11} aria-hidden />
                </span>
                <span className="min-w-0">
                  <span className="flex items-center gap-1.5">
                    <span className="text-[12px] font-medium leading-tight text-fg-1">
                      {t(`edgekind.${kind}`)}
                    </span>
                    {isSuggested && (
                      <span
                        className="rounded-xs border border-accent-border/50 bg-accent-soft px-1 py-px font-mono text-[8.5px] uppercase leading-[1.3] text-accent"
                        title={t("canvas.connect.suggestedHint")}
                      >
                        {t("canvas.connect.suggested")}
                      </span>
                    )}
                  </span>
                  <span className="block truncate text-[10px] leading-tight text-fg-3">
                    {t(`edgekind.desc.${kind}`)}
                  </span>
                </span>
                {/* stroke sample — mirrors the canvas dash language */}
                <svg width="26" height="8" viewBox="0 0 26 8" className="ml-auto shrink-0" aria-hidden>
                  <line
                    x1={1}
                    y1={4}
                    x2={25}
                    y2={4}
                    stroke={isSuggested ? "var(--kc-accent)" : "var(--kc-fg-3)"}
                    strokeWidth={kind === "calls" ? 1.6 : 1.2}
                    strokeDasharray={edgeDash(kind)}
                    strokeLinecap="round"
                  />
                </svg>
              </button>
            );
          })}
          <div className="flex items-center justify-between gap-2 px-1 pb-0.5 pt-1.5">
            <span className="text-[9.5px] leading-none text-fg-3">
              {t(kindPicker.suggested ? "canvas.connect.suggestedHint" : "canvas.connect.kindHint")}
            </span>
            <span className="font-mono text-[9px] leading-none text-fg-3">esc</span>
          </div>
        </div>
      )}

      {/* ---- on-canvas search (Ctrl+F / toolbar) ---- */}
      {searchQuery !== null && (
        <div
          data-canvas-overlay
          role="search"
          aria-label={t("canvas.search.title")}
          className="absolute right-3 top-3 z-40 flex items-center gap-1.5 animate-rise rounded-md border border-line bg-surface-2/95 py-1.5 pl-2.5 pr-1.5 shadow-overlay backdrop-blur max-sm:left-3"
          onPointerDown={(e) => e.stopPropagation()}
        >
          <Search size={12} className="shrink-0 text-fg-3" aria-hidden />
          <input
            ref={searchInputRef}
            value={searchQuery}
            onChange={(e) => onSearchInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                stepSearch(e.shiftKey ? -1 : 1);
              } else if (e.key === "Escape") {
                e.preventDefault();
                e.stopPropagation();
                closeSearch();
              }
            }}
            placeholder={t("canvas.search.placeholder")}
            aria-label={t("canvas.search.title")}
            spellCheck={false}
            className="h-6 w-40 bg-transparent font-mono text-[11.5px] text-fg-1 outline-none placeholder:font-sans placeholder:text-fg-3/70 sm:w-52"
          />
          <span
            aria-live="polite"
            className={cn(
              "shrink-0 font-mono text-[10px] leading-none tabular-nums",
              q && matchCount === 0 ? "text-warning" : "text-fg-3",
            )}
          >
            {q ? `${matchCount > 0 ? Math.min(searchIdx, matchCount - 1) + 1 : 0}/${matchCount}` : ""}
          </span>
          <KcToolButton
            onClick={() => setSearchFilter((v) => !v)}
            active={searchFilter}
            disabled={matchCount === 0}
            aria-label={t("canvas.search.filter")}
            title={t("canvas.search.filterTitle")}
          >
            <ListFilter size={12} />
          </KcToolButton>
          <KcToolButton onClick={closeSearch} aria-label={t("common.close")} title={t("common.close")}>
            <X size={12} />
          </KcToolButton>
        </div>
      )}

      {/* ---- node right-click menu ---- */}
      {nodeMenu && (() => {
        const node = nodeById.get(nodeMenu.id);
        if (!node) return null;
        const close = () => setNodeMenu(null);
        /* alignment section shows when the right-clicked node is part of a
         *  multi-selection (align needs ≥2, distribute needs ≥3) */
        const multiAlign = selectedIds.includes(node.id) && selectedIds.length >= 2;
        const alignModes: { mode: AlignMode; Icon: typeof AlignStartVertical; label: string }[] = [
          { mode: "left", Icon: AlignStartVertical, label: t("canvas.menu.alignLeft") },
          { mode: "hcenter", Icon: AlignCenterVertical, label: t("canvas.menu.alignHCenter") },
          { mode: "right", Icon: AlignEndVertical, label: t("canvas.menu.alignRight") },
          { mode: "top", Icon: AlignStartHorizontal, label: t("canvas.menu.alignTop") },
          { mode: "vcenter", Icon: AlignCenterHorizontal, label: t("canvas.menu.alignVCenter") },
          { mode: "bottom", Icon: AlignEndHorizontal, label: t("canvas.menu.alignBottom") },
        ];
        const copyTitle = async () => {
          close();
          try {
            await navigator.clipboard.writeText(node.title);
            useKontur.getState().pushToast(t("canvas.menu.copied"), undefined);
          } catch {
            useKontur.getState().pushToast(t("canvas.menu.copyFailed"), undefined);
          }
        };
        const menuH =
          168 +
          (node.path ? 34 : 0) +
          (multiAlign ? (selectedIds.length >= 3 ? 126 : 92) : 0);
        return (
          <div
            data-canvas-overlay
            role="menu"
            aria-label={t("canvas.menu.title")}
            className="absolute z-40 w-[196px] animate-rise rounded-md border border-line bg-surface-2/95 p-1.5 shadow-overlay backdrop-blur"
            style={{
              left: Math.max(8, Math.min(nodeMenu.x - 14, Math.max(size.w - 204, 8))),
              top: Math.max(8, Math.min(nodeMenu.y - 12, Math.max(size.h - menuH - 8, 8))),
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onContextMenu={(e) => e.preventDefault()}
          >
            {node.path && (
              <button
                type="button"
                role="menuitem"
                onClick={() => {
                  close();
                  const { openCodeTab, setSurface } = useKontur.getState();
                  openCodeTab(node.path!);
                  setSurface("code");
                }}
                className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover"
              >
                <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent">
                  <FileText size={11} aria-hidden />
                </span>
                <span className="min-w-0">
                  <span className="block text-[12px] font-medium leading-tight text-fg-1">
                    {t("canvas.menu.open")}
                  </span>
                  <span className="block truncate font-mono text-[9.5px] leading-tight text-fg-3">
                    {node.path}
                  </span>
                </span>
              </button>
            )}
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                openNoteEditor(node);
              }}
              className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover"
            >
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent">
                <StickyNote size={11} aria-hidden />
              </span>
              <span className="text-[12px] font-medium leading-tight text-fg-1">
                {node.note ? t("canvas.menu.editNote") : t("canvas.menu.addNote")}
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={copyTitle}
              className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover"
            >
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent">
                <Copy size={11} aria-hidden />
              </span>
              <span className="text-[12px] font-medium leading-tight text-fg-1">
                {t("canvas.menu.copy")}
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                centerWorld(node.x + nodeW(node) / 2, node.y + nodeH(node) / 2);
              }}
              className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover"
            >
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent">
                <Focus size={11} aria-hidden />
              </span>
              <span className="text-[12px] font-medium leading-tight text-fg-1">
                {t("canvas.menu.focus")}
              </span>
            </button>
            {multiAlign && (
              <div className="my-1 rounded-sm border border-line-faint bg-sunken/60 p-1.5">
                <div className="mb-1.5 flex items-center justify-between px-0.5">
                  <span className="font-mono text-[9px] uppercase tracking-[0.12em] text-fg-3">
                    {t("canvas.menu.alignSection")}
                  </span>
                  <span className="font-mono text-[9px] leading-none tabular-nums text-fg-3">
                    {selectedIds.length}
                  </span>
                </div>
                <div className="grid grid-cols-3 gap-1" role="group" aria-label={t("canvas.menu.alignSection")}>
                  {alignModes.map(({ mode, Icon, label }) => (
                    <button
                      key={mode}
                      type="button"
                      title={label}
                      aria-label={label}
                      onClick={() => alignSelection(mode)}
                      className="kc-focus-ring flex h-7 items-center justify-center rounded-sm border border-line-faint bg-surface-1 text-fg-3 transition-colors duration-100 hover:border-accent-border hover:bg-accent-soft hover:text-accent"
                    >
                      <Icon size={12} aria-hidden />
                    </button>
                  ))}
                </div>
                {selectedIds.length >= 3 && (
                  <div className="mt-1 grid grid-cols-2 gap-1">
                    <button
                      type="button"
                      title={t("canvas.menu.distributeH")}
                      aria-label={t("canvas.menu.distributeH")}
                      onClick={() => distributeSelection("x")}
                      className="kc-focus-ring flex h-7 items-center justify-center gap-1 rounded-sm border border-line-faint bg-surface-1 px-1 text-fg-3 transition-colors duration-100 hover:border-accent-border hover:bg-accent-soft hover:text-accent"
                    >
                      <AlignHorizontalDistributeCenter size={11} aria-hidden />
                      <span className="font-mono text-[9px] leading-none">H</span>
                    </button>
                    <button
                      type="button"
                      title={t("canvas.menu.distributeV")}
                      aria-label={t("canvas.menu.distributeV")}
                      onClick={() => distributeSelection("y")}
                      className="kc-focus-ring flex h-7 items-center justify-center gap-1 rounded-sm border border-line-faint bg-surface-1 px-1 text-fg-3 transition-colors duration-100 hover:border-accent-border hover:bg-accent-soft hover:text-accent"
                    >
                      <AlignVerticalDistributeCenter size={11} aria-hidden />
                      <span className="font-mono text-[9px] leading-none">V</span>
                    </button>
                  </div>
                )}
              </div>
            )}
            <div className="mx-1 my-1 border-t border-line-faint" aria-hidden />
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                deleteNodeById(node.id);
              }}
              className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-error/40 hover:bg-error-soft"
            >
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-error/50 group-hover/m:text-error">
                <Trash2 size={11} aria-hidden />
              </span>
              <span className="text-[12px] font-medium leading-tight text-fg-2 group-hover/m:text-error">
                {t("canvas.menu.delete")}
              </span>
            </button>
          </div>
        );
      })()}

      {/* ---- background right-click menu ---- */}
      {bgMenu && (() => {
        const close = () => setBgMenu(null);
        const menuH = 226;
        const item =
          "kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover disabled:pointer-events-none disabled:opacity-40";
        const tile =
          "flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent";
        return (
          <div
            data-canvas-overlay
            role="menu"
            aria-label={t("canvas.bgmenu.title")}
            className="absolute z-40 w-[196px] animate-rise rounded-md border border-line bg-surface-2/95 p-1.5 shadow-overlay backdrop-blur"
            style={{
              left: Math.max(8, Math.min(bgMenu.x - 14, Math.max(size.w - 204, 8))),
              top: Math.max(8, Math.min(bgMenu.y - 12, Math.max(size.h - menuH - 8, 8))),
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onContextMenu={(e) => e.preventDefault()}
          >
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                addNoteAt(bgMenu.x, bgMenu.y);
              }}
              className={item}
            >
              <span className={tile}>
                <StickyNote size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.bgmenu.addNoteHere")}
                </span>
                <span className="block text-[9.5px] leading-tight text-fg-3">
                  {t("canvas.bgmenu.atPointer")}
                </span>
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              disabled={nodes.length === 0}
              onClick={() => {
                close();
                selectAllNodes();
              }}
              className={item}
            >
              <span className={tile}>
                <BoxSelect size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.bgmenu.selectAll")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight tabular-nums text-fg-3">
                  {t("canvas.bgmenu.nodeCount", nodes.length)}
                </span>
              </span>
            </button>
            <div className="mx-1 my-1 border-t border-line-faint" aria-hidden />
            <button
              type="button"
              role="menuitem"
              disabled={nodes.length === 0}
              onClick={() => {
                close();
                fitView();
              }}
              className={item}
            >
              <span className={tile}>
                <Maximize2 size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.bgmenu.fit")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight text-fg-3">
                  {nodes.length} × {edges.length}
                </span>
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                resetZoom();
              }}
              className={item}
            >
              <span className={tile}>
                <Percent size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.bgmenu.resetZoom")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight tabular-nums text-fg-3">
                  {Math.round(zoom * 100)}% → 100%
                </span>
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                toggleGrid();
              }}
              className={item}
            >
              <span className={tile}>
                <Grid3x3 size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {showGrid ? t("canvas.bgmenu.hideGrid") : t("canvas.bgmenu.showGrid")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight text-fg-3">
                  {showGrid ? "24 px" : "—"}
                </span>
              </span>
            </button>
          </div>
        );
      })()}

      {/* ---- edge right-click menu ---- */}
      {edgeMenu && (() => {
        const edge = edges.find((x) => x.id === edgeMenu.id);
        if (!edge) return null;
        const from = nodeById.get(edge.from);
        const to = nodeById.get(edge.to);
        if (!from || !to) return null;
        const close = () => setEdgeMenu(null);
        const KindIcon = EDGE_KIND_ICONS[edge.kind];
        const item =
          "kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-line-strong hover:bg-surface-hover disabled:pointer-events-none disabled:opacity-40";
        const tile =
          "flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-accent-border group-hover/m:text-accent";
        const menuH = 300;
        return (
          <div
            data-canvas-overlay
            role="menu"
            aria-label={t("canvas.edgemenu.title")}
            className="absolute z-40 w-[196px] animate-rise rounded-md border border-line bg-surface-2/95 p-1.5 shadow-overlay backdrop-blur"
            style={{
              left: Math.max(8, Math.min(edgeMenu.x - 14, Math.max(size.w - 204, 8))),
              top: Math.max(8, Math.min(edgeMenu.y - 12, Math.max(size.h - menuH - 8, 8))),
            }}
            onPointerDown={(e) => e.stopPropagation()}
            onContextMenu={(e) => e.preventDefault()}
          >
            {/* identity header — kind + direction (not a menu item) */}
            <div className="flex items-center gap-2 px-1 pb-1.5 pt-0.5" aria-hidden>
              <span className={cn(tile, "group-hover/m:border-line-faint group-hover/m:text-fg-3 text-accent border-accent-border")}>
                <KindIcon size={11} />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t(`edgekind.${edge.kind}`)}
                </span>
                <span className="block truncate font-mono text-[9.5px] leading-tight text-fg-3">
                  {from.title} → {to.title}
                </span>
              </span>
            </div>
            <div className="mx-1 mb-1 border-t border-line-faint" aria-hidden />
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                const { pushUndoSnapshot, updateEdgeEndpoints } = useKontur.getState();
                pushUndoSnapshot();
                updateEdgeEndpoints(edge.id, edge.to, edge.from);
              }}
              className={item}
            >
              <span className={tile}>
                <ArrowLeftRight size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.edgemenu.flip")}
                </span>
                <span className="block truncate font-mono text-[9.5px] leading-tight text-fg-3">
                  {to.title} → {from.title}
                </span>
              </span>
            </button>
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                useKontur.getState().setCanvas({
                  selectedIds: [edge.from, edge.to],
                  primaryId: edge.from,
                  selectedEdgeId: null,
                });
              }}
              className={item}
            >
              <span className={tile}>
                <BoxSelect size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {t("canvas.edgemenu.selectEndpoints")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight text-fg-3">
                  {t("canvas.edgemenu.endpointsSub")}
                </span>
              </span>
            </button>
            {/* label — opens the connection-label editor at the midpoint */}
            <button
              type="button"
              role="menuitem"
              onClick={() => openEdgeNoteEditor(edge)}
              aria-keyshortcuts="F2"
              className={item}
            >
              <span className={cn(tile, edge.note && "border-warning/50 text-warning")}>
                <StickyNote size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-1">
                  {edge.note ? t("canvas.edgemenu.note.edit") : t("canvas.edgemenu.note.add")}
                </span>
                <span className="block truncate font-mono text-[9.5px] leading-tight text-fg-3">
                  {edge.note ?? t("canvas.edgemenu.note.sub")}
                </span>
              </span>
            </button>
            {/* kind section — iterative, menu stays open (like align) */}
            <div className="my-1 rounded-sm border border-line-faint bg-sunken/60 p-1.5">
              <div className="mb-1.5 flex items-center justify-between px-0.5">
                <span className="font-mono text-[9px] uppercase tracking-[0.12em] text-fg-3">
                  {t("canvas.edgemenu.kind")}
                </span>
                <span className="font-mono text-[9px] leading-none text-fg-3">6</span>
              </div>
              <div
                className="grid grid-cols-3 gap-1"
                role="group"
                aria-label={t("canvas.edgemenu.kind")}
              >
                {ALL_EDGE_KINDS.map((kind) => {
                  const Icon = EDGE_KIND_ICONS[kind];
                  const structural = kind === "contains" || kind === "plans";
                  const current = kind === edge.kind;
                  return (
                    <button
                      key={kind}
                      type="button"
                      title={structural ? `${t(`edgekind.${kind}`)} · ${t("canvas.edgemenu.structural")}` : t(`edgekind.${kind}`)}
                      aria-label={t(`edgekind.${kind}`)}
                      aria-pressed={current}
                      onClick={() => {
                        useKontur.getState().updateEdgeKind(edge.id, kind, { undoable: true });
                      }}
                      className={cn(
                        "kc-focus-ring flex h-7 items-center justify-center gap-1 rounded-sm border bg-surface-1 transition-colors duration-100",
                        current
                          ? "border-accent-border bg-accent-soft text-accent"
                          : structural
                            ? "border-dashed border-line-faint text-fg-3 hover:border-accent-border hover:bg-accent-soft hover:text-accent"
                            : "border-line-faint text-fg-3 hover:border-accent-border hover:bg-accent-soft hover:text-accent",
                      )}
                    >
                      <Icon size={12} aria-hidden />
                    </button>
                  );
                })}
              </div>
            </div>
            <div className="mx-1 my-1 border-t border-line-faint" aria-hidden />
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                close();
                const { canvas, pushUndoSnapshot, setCanvas: sc } = useKontur.getState();
                pushUndoSnapshot();
                sc({
                  edges: canvas.edges.filter((x) => x.id !== edge.id),
                  selectedEdgeId: null,
                });
              }}
              className="kc-focus-ring group/m flex w-full items-center gap-2 rounded-sm border border-transparent px-1.5 py-1.5 text-left transition-colors duration-100 hover:border-error/40 hover:bg-error-soft"
            >
              <span className="flex h-5 w-5 shrink-0 items-center justify-center rounded-xs border border-line-faint bg-surface-1 text-fg-3 transition-colors group-hover/m:border-error/50 group-hover/m:text-error">
                <Trash2 size={11} aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block text-[12px] font-medium leading-tight text-fg-2 group-hover/m:text-error">
                  {t("canvas.edgemenu.delete")}
                </span>
                <span className="block font-mono text-[9.5px] leading-tight text-fg-3">
                  {t("canvas.edgemenu.deleteSub")}
                </span>
              </span>
            </button>
          </div>
        );
      })()}

      {/* ---- floating toolbar ---- */}
      <div
        data-canvas-overlay
        className="absolute bottom-3 left-1/2 z-30 -translate-x-1/2 max-sm:left-3 max-sm:right-3 max-sm:translate-x-0"
      >
        <div className="flex items-center gap-0.5 overflow-x-auto rounded-lg border border-line bg-surface-2/95 px-1.5 py-1 shadow-overlay backdrop-blur">
          <KcToolButton
            active={tool === "select"}
            onClick={() => setTool("select")}
            aria-label={t("canvas.select")}
            title={t("canvas.select")}
          >
            <MousePointer2 size={13} />
          </KcToolButton>
          <KcToolButton
            active={tool === "pan"}
            onClick={() => setTool("pan")}
            aria-label={t("canvas.pan")}
            title={t("canvas.pan")}
          >
            <Hand size={13} />
          </KcToolButton>

          <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />

          <KcToolButton
            onClick={() => zoomCentered(0.8)}
            aria-label={t("canvas.zoomOut")}
            title={t("canvas.zoomOut")}
          >
            <ZoomOut size={13} />
          </KcToolButton>
          <button
            type="button"
            onClick={resetZoom}
            aria-label={t("canvas.zoomReset")}
            title={t("canvas.zoomReset")}
            className="kc-focus-ring w-12 shrink-0 rounded-sm py-1.5 text-center font-mono text-[11px] leading-none text-fg-2 transition-colors duration-100 hover:bg-surface-hover hover:text-fg-1"
          >
            {Math.round(zoom * 100)}%
          </button>
          <KcToolButton
            onClick={() => zoomCentered(1.25)}
            aria-label={t("canvas.zoomIn")}
            title={t("canvas.zoomIn")}
          >
            <ZoomIn size={13} />
          </KcToolButton>

          <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />

          <KcToolButton onClick={fitView} aria-label={t("canvas.fit")} title={t("canvas.fit")}>
            <Maximize2 size={13} />
          </KcToolButton>
          <KcToolButton
            onClick={focusSelection}
            disabled={selectedIds.length === 0}
            aria-label={t("canvas.focus")}
            title={t("canvas.focus")}
          >
            <Focus size={13} />
          </KcToolButton>
          <KcToolButton
            onClick={autoLayout}
            disabled={nodes.length === 0}
            aria-label={t("canvas.autolayout")}
            title={t("canvas.autolayout")}
          >
            <LayoutGrid size={13} />
          </KcToolButton>
          <KcToolButton
            active={showGrid}
            onClick={toggleGrid}
            aria-label={t("canvas.grid")}
            title={t("canvas.grid")}
          >
            <Grid3x3 size={13} />
          </KcToolButton>
          <KcToolButton
            onClick={addNoteNode}
            aria-label={t("canvas.note.add")}
            title={t("canvas.note.add")}
          >
            <StickyNote size={13} />
          </KcToolButton>

          <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />

          <KcToolButton
            onClick={() => (searchQuery === null ? openSearch() : closeSearch())}
            active={searchQuery !== null}
            aria-label={t("canvas.search.title")}
            title={t("canvas.search.title")}
          >
            <Search size={13} />
          </KcToolButton>

          <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />

          <KcToolButton
            onClick={askAi}
            aria-label={t("canvas.askAi")}
            title={t("canvas.askAi")}
            className="text-accent hover:bg-accent-soft hover:text-accent"
          >
            <Sparkles size={13} />
          </KcToolButton>

          {/* selection chip — count + clear (multi-selections only) */}
          {selectedIds.length >= 2 && (
            <>
              <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />
              <button
                type="button"
                onClick={() => useKontur.getState().clearSelection()}
                aria-label={t("canvas.toolbar.clearSelection", selectedIds.length)}
                title={t("canvas.toolbar.clearSelection", selectedIds.length)}
                className="kc-focus-ring group/sel flex h-[26px] shrink-0 items-center gap-1 rounded-sm border border-accent-border/60 bg-accent-soft px-1.5 font-mono text-[11px] leading-none text-accent transition-colors duration-100 hover:border-error/40 hover:bg-error-soft hover:text-error"
              >
                <BoxSelect size={11} aria-hidden className="opacity-70" />
                <span className="tabular-nums">{selectedIds.length}</span>
                <X size={11} aria-hidden className="opacity-50 transition-opacity duration-100 group-hover/sel:opacity-100" />
              </button>
            </>
          )}

          {/* edge-selection chip — bulk connections selected (outline marquee) */}
          {selectedEdgeIds.length >= 2 && (
            <>
              <div className="mx-0.5 h-4 w-px shrink-0 bg-line" aria-hidden />
              <button
                type="button"
                onClick={() =>
                  useKontur.getState().setCanvas({ selectedEdgeId: null, selectedEdgeIds: [] })
                }
                aria-label={t("canvas.toolbar.clearEdgeSelection", selectedEdgeIds.length)}
                title={t("canvas.toolbar.clearEdgeSelection", selectedEdgeIds.length)}
                className="kc-focus-ring group/esel flex h-[26px] shrink-0 items-center gap-1 rounded-sm border border-accent-border/60 bg-accent-soft px-1.5 font-mono text-[11px] leading-none text-accent transition-colors duration-100 hover:border-error/40 hover:bg-error-soft hover:text-error"
              >
                <Link2 size={11} aria-hidden className="opacity-70" />
                <span className="tabular-nums">{selectedEdgeIds.length}</span>
                <X size={11} aria-hidden className="opacity-50 transition-opacity duration-100 group-hover/esel:opacity-100" />
              </button>
            </>
          )}
        </div>
      </div>

      {/* ---- minimap ---- */}
      {nodes.length > 0 && (
        <MiniMap
          nodes={nodes}
          selectedIds={selectedIds}
          zoom={zoom}
          panX={panX}
          panY={panY}
          viewW={size.w}
          viewH={size.h}
          ariaLabel={t("canvas.minimap")}
          onCenterWorld={centerWorld}
        />
      )}

      {/* ---- empty state ---- */}
      {nodes.length === 0 && (
        <div className="pointer-events-none absolute inset-0 z-40 flex items-center justify-center p-6">
          <div
            data-canvas-overlay
            className="pointer-events-auto rounded-lg border border-line bg-surface-2 p-6 text-center shadow-overlay"
          >
            <Workflow size={28} strokeWidth={1.5} className="mx-auto mb-3 text-fg-3" aria-hidden />
            <div className="text-[14px] font-semibold text-fg-1">{t("canvas.blank.title")}</div>
            <p className="mx-auto mt-1.5 max-w-[300px] text-[12.5px] leading-relaxed text-fg-3">
              {t("canvas.blank.hint")}
            </p>
            <div className="mt-4 flex items-center justify-center gap-2">
              <KcPrimaryButton onClick={onOpenWorkspace}>{t("canvas.open")}</KcPrimaryButton>
              <KcGhostButton onClick={() => useKontur.getState().setSurface("chat")}>
                {t("canvas.askPlan")}
              </KcGhostButton>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
