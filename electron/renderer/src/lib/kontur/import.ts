"use client";

/* ============================================================
   Session bundle IMPORT — the read side of export.ts.

   Dependency-free ZIP reader (EOCD → central directory → local
   headers; store + deflate-raw via DecompressionStream), bundle
   validation against the manifest, and a restore pipeline that
   prefers the full-fidelity state.json and falls back to the
   lossy per-part JSONs for older bundles.
   ============================================================ */

import { newId } from "./store";
import { useKontur } from "./store";
import type {
  CanvasEdge,
  CanvasNode,
  EdgeKind,
  Goal,
  GoalStatus,
  NodeKind,
  Session,
  TrajectoryKind,
} from "./types";

/* ---------- ZIP reading ---------- */

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = c & 1 ? (0xedb88320 ^ (c >>> 1)) : c >>> 1;
    table[i] = c >>> 0;
  }
  return table;
})();

function crc32(bytes: Uint8Array): number {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

async function inflateRaw(bytes: Uint8Array): Promise<Uint8Array> {
  const ds = new DecompressionStream("deflate-raw");
  const stream = new Blob([bytes as unknown as BlobPart]).stream().pipeThrough(ds);
  const buf = await new Response(stream).arrayBuffer();
  return new Uint8Array(buf);
}

export class BundleError extends Error {
  code: "not-zip" | "corrupt" | "not-bundle" | "empty" | "busy";
  constructor(code: BundleError["code"], message: string) {
    super(message);
    this.code = code;
  }
}

interface ZipRecord {
  name: string;
  method: number;
  crc: number;
  compressedSize: number;
  uncompressedSize: number;
  localOffset: number;
}

function findEocd(view: DataView, len: number): number {
  /* scan backwards for the EOCD signature 0x06054b50 */
  const start = Math.max(0, len - 22 - 65535);
  for (let i = len - 22; i >= start; i--) {
    if (view.getUint32(i, true) === 0x06054b50) return i;
  }
  return -1;
}

async function readZip(buf: ArrayBuffer): Promise<Map<string, Uint8Array>> {
  const view = new DataView(buf);
  const len = buf.byteLength;
  const eocd = findEocd(view, len);
  if (eocd < 0) throw new BundleError("corrupt", "no EOCD record");

  const entryCount = view.getUint16(eocd + 10, true);
  const cdOffset = view.getUint32(eocd + 16, true);
  const cdSize = view.getUint32(eocd + 12, true);
  if (cdOffset === 0xffffffff || cdSize === 0xffffffff) {
    throw new BundleError("corrupt", "zip64 archives are not supported");
  }

  const records: ZipRecord[] = [];
  const bytes = new Uint8Array(buf);
  const dec = new TextDecoder();

  let p = cdOffset;
  for (let i = 0; i < entryCount; i++) {
    if (p + 46 > len || view.getUint32(p, true) !== 0x02014b50) {
      throw new BundleError("corrupt", `bad central record #${i}`);
    }
    const method = view.getUint16(p + 10, true);
    const crc = view.getUint32(p + 16, true);
    const comp = view.getUint32(p + 20, true);
    const uncomp = view.getUint32(p + 24, true);
    const nameLen = view.getUint16(p + 28, true);
    const extraLen = view.getUint16(p + 30, true);
    const commentLen = view.getUint16(p + 32, true);
    const localOffset = view.getUint32(p + 42, true);
    const name = dec.decode(bytes.subarray(p + 46, p + 46 + nameLen));
    records.push({ name, method, crc, compressedSize: comp, uncompressedSize: uncomp, localOffset });
    p += 46 + nameLen + extraLen + commentLen;
  }

  const out = new Map<string, Uint8Array>();
  for (const rec of records) {
    if (rec.name.endsWith("/")) continue; /* directory entry */
    const lo = rec.localOffset;
    if (lo + 30 > len || view.getUint32(lo, true) !== 0x04034b50) {
      throw new BundleError("corrupt", `bad local header for ${rec.name}`);
    }
    const lNameLen = view.getUint16(lo + 26, true);
    const lExtraLen = view.getUint16(lo + 28, true);
    const dataStart = lo + 30 + lNameLen + lExtraLen;
    let data: Uint8Array<ArrayBufferLike> = bytes.subarray(dataStart, dataStart + rec.compressedSize);

    if (rec.method === 0) {
      /* stored — as-is */
    } else if (rec.method === 8) {
      data = await inflateRaw(data);
    } else {
      throw new BundleError("corrupt", `unsupported compression method ${rec.method} for ${rec.name}`);
    }

    if (data.length !== rec.uncompressedSize) {
      throw new BundleError("corrupt", `size mismatch for ${rec.name}`);
    }
    if (crc32(data) !== rec.crc) {
      throw new BundleError("corrupt", `CRC mismatch for ${rec.name}`);
    }
    out.set(rec.name, data);
  }
  return out;
}

/* ---------- bundle parsing ---------- */

const NODE_KINDS: NodeKind[] = [
  "file", "folder", "module", "service", "interface", "data", "view", "test",
  "plan", "task", "agent", "model", "external", "note",
];
const EDGE_KINDS: EdgeKind[] = ["contains", "depends", "calls", "implements", "relates", "plans"];
const GOAL_STATUSES: GoalStatus[] = ["planning", "running", "waiting", "verifying", "completed", "failed"];
const TRAJ_KINDS: TrajectoryKind[] = [
  "request", "plan", "context", "tool", "subagent", "build", "test", "error",
  "fix", "review", "checkpoint", "approval", "complete", "compact", "fork", "skills",
];

export interface ImportedBundle {
  fullFidelity: boolean;
  session: Session;
  goals: Goal[];
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  files: { path: string; content: string; modified: boolean }[];
}

function json(entries: Map<string, Uint8Array>, name: string): unknown {
  const data = entries.get(name);
  if (!data) return null;
  try {
    return JSON.parse(new TextDecoder().decode(data));
  } catch {
    return null;
  }
}

function asRecord(v: unknown): Record<string, unknown> | null {
  return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
}

function str(v: unknown, fallback = ""): string {
  return typeof v === "string" ? v : fallback;
}
function num(v: unknown, fallback = 0): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}

/* lossy fallback: session.json (kontur-code/session/v2) → Session */
function sessionFromV2(raw: unknown): Session | null {
  const root = asRecord(raw);
  const s = asRecord(root?.session);
  if (!s || !Array.isArray(s.messages)) return null;
  const messages = (s.messages as unknown[]).map((m0) => {
    const m = asRecord(m0) ?? {};
    const toolCalls = Array.isArray(m.toolCalls)
      ? (m.toolCalls as unknown[]).map((t0) => {
          const t = asRecord(t0) ?? {};
          return {
            id: newId("tc"),
            tool: str(t.tool, "tool"),
            risk: (str(t.risk, "low") as never),
            state: (str(t.state, "succeeded") as never),
            headline: str(t.headline),
            durationMs: num(t.durationMs),
          };
        })
      : [];
    return {
      id: newId("m"),
      role: str(m.role, "user") === "assistant" ? ("assistant" as const) : ("user" as const),
      content: str(m.content),
      createdAt: Date.parse(str(m.createdAt)) || Date.now(),
      modelId: str(m.modelId) || undefined,
      toolCalls,
      usage: asRecord(m.usage)
        ? {
            inputTokens: num(asRecord(m.usage)?.inputTokens),
            outputTokens: num(asRecord(m.usage)?.outputTokens),
            ms: num(asRecord(m.usage)?.ms),
          }
        : undefined,
    };
  });
  const events = Array.isArray(s.events)
    ? (s.events as unknown[]).map((e0) => {
        const e = asRecord(e0) ?? {};
        return {
          id: newId("ev"),
          ts: Date.parse(str(e.ts)) || Date.now(),
          kind: (TRAJ_KINDS.includes(str(e.kind) as TrajectoryKind) ? str(e.kind) : "request") as TrajectoryKind,
          title: str(e.title),
          detail: str(e.detail) || undefined,
          status: (str(e.status) || undefined) as "running" | "done" | "failed" | "warning" | undefined,
        };
      })
    : [];
  return {
    id: newId("s"),
    title: str(s.title, "Imported session"),
    createdAt: Date.parse(str(s.createdAt)) || Date.now(),
    updatedAt: Date.now(),
    preview: messages[0]?.content?.slice(0, 90) ?? "",
    messages,
    events,
    goalIds: [],
    modelId: str(s.model) || useKontur.getState().ui.selectedModelId,
  };
}

function goalsFromRaw(raw: unknown, sessionId: string): Goal[] {
  const root = asRecord(raw);
  if (!root || !Array.isArray(root.goals)) return [];
  return (root.goals as unknown[]).map((g0) => {
    const g = asRecord(g0) ?? {};
    return {
      id: newId("g"),
      title: str(g.title, "Goal"),
      createdAt: Date.parse(str(g.createdAt)) || Date.now(),
      status: (GOAL_STATUSES.includes(str(g.status) as GoalStatus) ? str(g.status) : "planning") as GoalStatus,
      criteria: Array.isArray(g.criteria)
        ? (g.criteria as unknown[]).map((c0) => {
            const c = asRecord(c0) ?? {};
            return { id: newId("crit"), text: str(c.text), done: !!c.done };
          })
        : [],
      sessionId,
    };
  });
}

function canvasFromRaw(raw: unknown): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
  const root = asRecord(raw);
  if (!root) return { nodes: [], edges: [] };
  const nodes = Array.isArray(root.nodes)
    ? (root.nodes as unknown[]).flatMap((n0) => {
        const n = asRecord(n0);
        if (!n) return [];
        return [{
          id: str(n.id) || newId("n"),
          kind: (NODE_KINDS.includes(str(n.kind) as NodeKind) ? str(n.kind) : "note") as NodeKind,
          title: str(n.title, "Node"),
          meta: str(n.meta) || undefined,
          x: num(n.x),
          y: num(n.y),
          path: str(n.path) || undefined,
          note: str(n.note) || undefined,
        }];
      })
    : [];
  const ids = new Set(nodes.map((n) => n.id));
  const edges = Array.isArray(root.edges)
    ? (root.edges as unknown[]).flatMap((e0) => {
        const e = asRecord(e0);
        if (!e) return [];
        const from = str(e.from);
        const to = str(e.to);
        if (!ids.has(from) || !ids.has(to)) return []; /* drop dangling */
        return [{
          id: str(e.id) || newId("e"),
          from,
          to,
          kind: (EDGE_KINDS.includes(str(e.kind) as EdgeKind) ? str(e.kind) : "relates") as EdgeKind,
          note: str(e.note) || undefined,
        }];
      })
    : [];
  return { nodes, edges };
}

function filesFromRaw(raw: unknown): { path: string; content: string; modified: boolean }[] {
  const root = asRecord(raw);
  if (!root || !Array.isArray(root.files)) return [];
  return (root.files as unknown[]).flatMap((f0) => {
    const f = asRecord(f0);
    if (!f) return [];
    const path = str(f.path);
    if (!path) return [];
    return [{ path, content: str(f.content), modified: !!f.modified }];
  });
}

export function parseBundle(entries: Map<string, Uint8Array>): ImportedBundle {
  const manifest = asRecord(json(entries, "manifest.json"));
  if (!manifest || str(manifest.format) !== "kontur-code/bundle/v1") {
    throw new BundleError("not-bundle", "manifest.json missing or wrong format");
  }

  /* 1 — full-fidelity state.json */
  const state = asRecord(json(entries, "state.json"));
  if (state && str(state.format) === "kontur-code/state/v1") {
    const session = asRecord(state.session) as unknown as Session | null;
    if (session && Array.isArray(session.messages) && Array.isArray(session.events)) {
      const goalsRaw = Array.isArray(state.goals) ? (state.goals as unknown[]) : [];
      const goals = goalsRaw.map((g0) => {
        const g = { ...(asRecord(g0) as unknown as Goal) };
        g.id = g.id || newId("g");
        g.criteria = Array.isArray(g.criteria) ? g.criteria : [];
        return g;
      });
      const canvasRaw = asRecord(state.canvas);
      const nodes = (Array.isArray(canvasRaw?.nodes) ? (canvasRaw?.nodes as unknown[]) : []).map(
        (n0) => ({ ...(asRecord(n0) as unknown as CanvasNode) }),
      );
      const nodeIds = new Set(nodes.map((n) => n.id));
      const edges = (Array.isArray(canvasRaw?.edges) ? (canvasRaw?.edges as unknown[]) : [])
        .map((e0) => ({ ...(asRecord(e0) as unknown as CanvasEdge) }))
        .filter((e) => nodeIds.has(e.from) && nodeIds.has(e.to));
      const files = (Array.isArray(state.files) ? (state.files as unknown[]) : []).flatMap((f0) => {
        const f = asRecord(f0);
        const path = f ? str(f.path) : "";
        return path ? [{ path, content: str(f?.content), modified: !!f?.modified }] : [];
      });
      const clean: Session = { ...session, goalIds: goals.map((g) => g.id) };
      return { fullFidelity: true, session: clean, goals, nodes, edges, files };
    }
  }

  /* 2 — lossy reconstruction from the v1 parts */
  const session = sessionFromV2(json(entries, "session.json"));
  if (!session) throw new BundleError("empty", "no readable session in bundle");
  const goals = goalsFromRaw(json(entries, "goals.json"), session.id);
  const sessionGoals: Session = { ...session, goalIds: goals.map((g) => g.id) };
  const { nodes, edges } = canvasFromRaw(json(entries, "canvas.json"));
  return { fullFidelity: false, session: sessionGoals, goals, nodes, edges, files: filesFromRaw(json(entries, "files.json")) };
}

/* ---------- restore into the live app ---------- */

export interface ImportResult {
  ok: boolean;
  fullFidelity: boolean;
  counts: { messages: number; nodes: number; edges: number; files: number; goals: number; skippedFiles: number };
}

export async function importBundleFile(file: File): Promise<ImportResult> {
  if (!file.name.toLowerCase().endsWith(".zip") && file.type !== "application/zip") {
    throw new BundleError("not-zip", "not a .zip archive");
  }
  const buf = await file.arrayBuffer();
  if (buf.byteLength < 100) throw new BundleError("not-zip", "archive too small");
  const entries = await readZip(buf);
  if (!entries.size) throw new BundleError("empty", "archive has no entries");
  const bundle = parseBundle(entries);

  const store = useKontur.getState();
  if (store.runner.status === "running" || store.runner.status === "awaiting-approval") {
    throw new BundleError("busy", "agent is running");
  }

  /* restore workspace files first (paths outside the demo workspace are skipped) */
  let skippedFiles = 0;
  let restoredFiles = 0;
  const known = new Set(store.files.map((f) => f.path));
  for (const f of bundle.files) {
    if (known.has(f.path)) {
      store.setFileContent(f.path, f.content);
      store.setFileModified(f.path, f.modified);
      restoredFiles++;
    } else {
      skippedFiles++;
    }
  }

  /* restore the session (remaps ids on collision) */
  store.importSession(bundle.session, bundle.goals);

  /* restore the canvas graph */
  if (bundle.nodes.length) {
    useKontur.getState().pushUndoSnapshot();
    useKontur.getState().setCanvas({
      nodes: bundle.nodes,
      edges: bundle.edges,
      selectedIds: [],
      primaryId: null,
      selectedEdgeId: null,
    });
  }

  return {
    ok: true,
    fullFidelity: bundle.fullFidelity,
    counts: {
      messages: bundle.session.messages.length,
      nodes: bundle.nodes.length,
      edges: bundle.edges.length,
      files: restoredFiles,
      goals: bundle.goals.length,
      skippedFiles,
    },
  };
}
