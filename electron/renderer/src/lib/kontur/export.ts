"use client";

import type { CanvasEdge, CanvasNode, DemoFile, Goal, Session, ToolCall } from "./types";

function download(name: string, content: string, mime: string) {
  const blob = new Blob([content], { type: `${mime};charset=utf-8` });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 400);
}

function toolLine(tc: ToolCall): string {
  const status =
    tc.state === "succeeded" ? "✓" : tc.state === "failed" ? "✗" : tc.state === "running" ? "…" : "–";
  return `- [${status}] \`${tc.tool}\` — ${tc.headline}`;
}

function slugify(title: string, fallback = "session"): string {
  return title.replace(/[^\w\s-]/g, "").trim().replace(/\s+/g, "-").toLowerCase() || fallback;
}

function buildSessionMarkdown(session: Session): string {
  const lines: string[] = [];
  lines.push(`# ${session.title}`);
  lines.push("");
  lines.push(`> Exported ${new Date().toLocaleString()} · Kontur Code`);
  lines.push("");
  for (const m of session.messages) {
    if (m.folded) continue;
    lines.push(m.role === "user" ? "## 🧑 User" : "## ✦ Assistant");
    lines.push("");
    lines.push(m.content);
    if (m.toolCalls.length) {
      lines.push("");
      lines.push("**Tools**");
      m.toolCalls.forEach((tc) => lines.push(toolLine(tc)));
    }
    if (m.usage) {
      lines.push("");
      lines.push(
        `_${m.usage.inputTokens.toLocaleString("en-US")} in · ${m.usage.outputTokens.toLocaleString("en-US")} out · ${(m.usage.ms / 1000).toFixed(0)}s_`,
      );
    }
    lines.push("");
  }
  if (session.events.length) {
    lines.push("---");
    lines.push("");
    lines.push("## Trajectory");
    session.events.forEach((ev) => {
      const time = new Date(ev.ts).toLocaleTimeString();
      lines.push(`- \`${time}\` **${ev.kind}** — ${ev.title}${ev.detail ? ` · ${ev.detail}` : ""}`);
    });
  }
  return lines.join("\n");
}

export function exportSessionMarkdown(session: Session) {
  download(`${slugify(session.title)}.md`, buildSessionMarkdown(session), "text/markdown");
}

function buildSessionJson(session: Session): string {
  const payload = {
    exportedAt: new Date().toISOString(),
    format: "kontur-code/session/v2",
    session: {
      id: session.id,
      title: session.title,
      createdAt: new Date(session.createdAt).toISOString(),
      updatedAt: new Date(session.updatedAt).toISOString(),
      model: session.modelId,
      forkedFrom: session.forkedFrom ?? null,
      messages: session.messages.map((m) => ({
        role: m.role,
        content: m.content,
        createdAt: new Date(m.createdAt).toISOString(),
        modelId: m.modelId,
        toolCalls: m.toolCalls.map((tc) => ({
          tool: tc.tool,
          risk: tc.risk,
          state: tc.state,
          headline: tc.headline,
          durationMs: tc.durationMs,
        })),
        usage: m.usage ?? null,
      })),
      events: session.events.map((ev) => ({
        ts: new Date(ev.ts).toISOString(),
        kind: ev.kind,
        title: ev.title,
        status: ev.status ?? null,
      })),
    },
  };
  return JSON.stringify(payload, null, 2);
}

export function exportSessionJson(session: Session) {
  download(`${slugify(session.title)}.json`, buildSessionJson(session), "application/json");
}

export function exportSessionText(session: Session) {
  const lines: string[] = [];
  lines.push(`Kontur Code — ${session.title}`);
  lines.push(`Exported ${new Date().toLocaleString()}`);
  lines.push("=".repeat(64));
  lines.push("");
  for (const m of session.messages) {
    if (m.folded) continue;
    lines.push(m.role === "user" ? "[USER]" : "[ASSISTANT]");
    lines.push(m.content);
    m.toolCalls.forEach((tc) => lines.push(`    > ${tc.tool} — ${tc.headline} (${tc.state})`));
    lines.push("");
  }
  download(`${slugify(session.title)}.txt`, lines.join("\n"), "text/plain");
}

/* ============================================================
   Trajectory export — the agent-run timeline as a standalone
   report (Markdown audit trail / machine-readable JSON).
   ============================================================ */

const KIND_STATUS: Record<string, string> = {
  done: "✓",
  running: "…",
  failed: "✗",
  warning: "⚠",
};

function slug(title: string): string {
  return title.replace(/[^\w\s-]/g, "").trim().replace(/\s+/g, "-").toLowerCase() || "trajectory";
}

export function exportTrajectoryMarkdown(session: Session) {
  download(
    `${slug(session.title)}-trajectory.md`,
    buildTrajectoryMarkdown(session),
    "text/markdown",
  );
}

function buildTrajectoryMarkdown(session: Session): string {
  const events = session.events;
  const lines: string[] = [];

  lines.push(`# Trajectory — ${session.title}`);
  lines.push("");
  lines.push(`> ${events.length} events · exported ${new Date().toLocaleString()} · Kontur Code`);
  lines.push("");

  /* summary table by kind */
  const byKind = new Map<string, number>();
  events.forEach((ev) => byKind.set(ev.kind, (byKind.get(ev.kind) ?? 0) + 1));
  lines.push("| Kind | Events |");
  lines.push("| --- | --- |");
  [...byKind.entries()]
    .sort((a, b) => b[1] - a[1])
    .forEach(([kind, n]) => lines.push(`| ${kind} | ${n} |`));
  lines.push("");

  /* tool duration roll-up */
  const toolEvents = events.filter((ev) => ev.kind === "tool" && ev.durationMs != null);
  if (toolEvents.length) {
    const totalMs = toolEvents.reduce((n, ev) => n + (ev.durationMs ?? 0), 0);
    lines.push(`**Tool time** — ${toolEvents.length} calls · ${(totalMs / 1000).toFixed(1)}s total`);
    lines.push("");
  }

  lines.push("## Timeline");
  lines.push("");
  events.forEach((ev) => {
    const time = new Date(ev.ts).toLocaleTimeString();
    const mark = KIND_STATUS[ev.status ?? ""] ?? "·";
    const dur = ev.durationMs != null ? ` \`${(ev.durationMs / 1000).toFixed(1)}s\`` : "";
    const agent = ev.agent ? ` **[${ev.agent}]**` : "";
    lines.push(`- \`${time}\` ${mark} **${ev.kind}**${agent} — ${ev.title}${ev.detail ? ` · ${ev.detail}` : ""}${dur}`);
  });
  lines.push("");
  return lines.join("\n");
}

export function exportTrajectoryJson(session: Session) {
  download(
    `${slug(session.title)}-trajectory.json`,
    buildTrajectoryJson(session),
    "application/json",
  );
}

function buildTrajectoryJson(session: Session): string {
  const payload = {
    exportedAt: new Date().toISOString(),
    format: "kontur-code/trajectory/v1",
    session: {
      id: session.id,
      title: session.title,
      createdAt: new Date(session.createdAt).toISOString(),
    },
    summary: {
      events: session.events.length,
      byKind: session.events.reduce<Record<string, number>>((acc, ev) => {
        acc[ev.kind] = (acc[ev.kind] ?? 0) + 1;
        return acc;
      }, {}),
      toolMs: session.events
        .filter((ev) => ev.kind === "tool" && ev.durationMs != null)
        .reduce((n, ev) => n + (ev.durationMs ?? 0), 0),
    },
    events: session.events.map((ev) => ({
      ts: new Date(ev.ts).toISOString(),
      kind: ev.kind,
      title: ev.title,
      detail: ev.detail ?? null,
      status: ev.status ?? null,
      agent: ev.agent ?? null,
      durationMs: ev.durationMs ?? null,
    })),
  };
  return JSON.stringify(payload, null, 2);
}

/* ============================================================
   Session export BUNDLE — one .zip with the full hand-off:
   chat markdown + machine-readable session, trajectory report
   (md + json), canvas graph, workspace file snapshot, goals
   and a manifest. Minimal store-method ZIP writer — no deps.
   ============================================================ */

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

interface ZipEntry {
  name: string;
  data: Uint8Array;
}

/* TS5's Uint8Array<ArrayBufferLike> is not assignable to BlobPart — bridge it */
const asBlobPart = (u: Uint8Array): BlobPart => u as unknown as BlobPart;

function dosStamp(): { time: number; date: number } {
  const d = new Date();
  return {
    time: ((d.getHours() & 0x1f) << 11) | ((d.getMinutes() & 0x3f) << 5) | ((d.getSeconds() / 2) & 0x1f),
    date: (((d.getFullYear() - 1980) & 0x7f) << 9) | (((d.getMonth() + 1) & 0xf) << 5) | (d.getDate() & 0x1f),
  };
}

function buildZipBlob(entries: ZipEntry[]): Blob {
  const enc = new TextEncoder();
  const stamp = dosStamp();
  const parts: BlobPart[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;

  for (const entry of entries) {
    const nameBytes = enc.encode(entry.name);
    const crc = crc32(entry.data);

    const lh = new DataView(new ArrayBuffer(30));
    lh.setUint32(0, 0x04034b50, true);
    lh.setUint16(4, 20, true); /* version needed */
    lh.setUint16(6, 0x0800, true); /* UTF-8 file names */
    lh.setUint16(8, 0, true); /* method: store */
    lh.setUint16(10, stamp.time, true);
    lh.setUint16(12, stamp.date, true);
    lh.setUint32(14, crc, true);
    lh.setUint32(18, entry.data.length, true);
    lh.setUint32(22, entry.data.length, true);
    lh.setUint16(26, nameBytes.length, true);
    parts.push(asBlobPart(new Uint8Array(lh.buffer)), asBlobPart(nameBytes), asBlobPart(entry.data));

    const cd = new DataView(new ArrayBuffer(46));
    cd.setUint32(0, 0x02014b50, true);
    cd.setUint16(4, 20, true);
    cd.setUint16(6, 20, true);
    cd.setUint16(8, 0x0800, true);
    cd.setUint16(10, 0, true);
    cd.setUint16(12, stamp.time, true);
    cd.setUint16(14, stamp.date, true);
    cd.setUint32(16, crc, true);
    cd.setUint32(20, entry.data.length, true);
    cd.setUint32(24, entry.data.length, true);
    cd.setUint16(28, nameBytes.length, true);
    cd.setUint32(42, offset, true); /* local header offset */
    central.push(new Uint8Array(cd.buffer), nameBytes);
    offset += 30 + nameBytes.length + entry.data.length;
  }

  let centralSize = 0;
  for (const c of central) centralSize += c.length;
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, entries.length, true);
  end.setUint16(10, entries.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);

  return new Blob(
    [...parts, ...central.map(asBlobPart), asBlobPart(new Uint8Array(end.buffer))],
    { type: "application/zip" },
  );
}

function downloadBlob(name: string, blob: Blob) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 400);
}

export interface BundleInput {
  session: Session;
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  files: DemoFile[];
  goals: Goal[];
}

export function exportSessionBundle({ session, nodes, edges, files, goals }: BundleInput) {
  const enc = new TextEncoder();
  const modified = files.filter((f) => f.modified);
  const manifest = {
    exportedAt: new Date().toISOString(),
    format: "kontur-code/bundle/v1",
    app: "Kontur Code — redesign prototype v0.9",
    session: {
      id: session.id,
      title: session.title,
      createdAt: new Date(session.createdAt).toISOString(),
      updatedAt: new Date(session.updatedAt).toISOString(),
      model: session.modelId,
      messages: session.messages.length,
      events: session.events.length,
    },
    contents: [
      "state.json — full-fidelity snapshot for lossless import (kontur-code/state/v1)",
      "chat.md — readable conversation incl. tool calls and usage",
      "session.json — machine-readable session (kontur-code/session/v2)",
      "trajectory.md — agent-run audit trail",
      "trajectory.json — machine-readable timeline (kontur-code/trajectory/v1)",
      "canvas.json — graph snapshot (kontur-code/canvas/v1)",
      "files.json — workspace snapshot with modified flags",
      "goals.json — goals & completion contracts",
    ],
    counts: {
      canvasNodes: nodes.length,
      canvasEdges: edges.length,
      files: files.length,
      filesModified: modified.length,
      goals: goals.length,
    },
  };

  const canvasPayload = {
    exportedAt: new Date().toISOString(),
    format: "kontur-code/canvas/v1",
    nodes: nodes.map((n) => ({
      id: n.id,
      kind: n.kind,
      title: n.title,
      meta: n.meta ?? null,
      x: Math.round(n.x),
      y: Math.round(n.y),
      path: n.path ?? null,
      note: n.note ?? null,
    })),
    edges: edges.map((e) => ({ id: e.id, from: e.from, to: e.to, kind: e.kind, note: e.note ?? null })),
  };

  const filesPayload = {
    exportedAt: new Date().toISOString(),
    workspace: "AuthFlow",
    files: files.map((f) => ({
      path: f.path,
      language: f.language,
      modified: !!f.modified,
      bytes: f.content.length,
      content: f.content,
    })),
  };

  const goalsPayload = {
    exportedAt: new Date().toISOString(),
    goals: goals.map((g) => ({
      id: g.id,
      title: g.title,
      status: g.status,
      criteria: g.criteria.map((c) => ({ text: c.text, done: c.done })),
      createdAt: new Date(g.createdAt).toISOString(),
    })),
  };

  const statePayload = {
    exportedAt: new Date().toISOString(),
    format: "kontur-code/state/v1",
    session,
    goals,
    canvas: { nodes, edges },
    files,
  };

  const entries: ZipEntry[] = [
    { name: "manifest.json", data: enc.encode(JSON.stringify(manifest, null, 2)) },
    { name: "state.json", data: enc.encode(JSON.stringify(statePayload, null, 2)) },
    { name: "chat.md", data: enc.encode(buildSessionMarkdown(session)) },
    { name: "session.json", data: enc.encode(buildSessionJson(session)) },
    { name: "trajectory.md", data: enc.encode(buildTrajectoryMarkdown(session)) },
    { name: "trajectory.json", data: enc.encode(buildTrajectoryJson(session)) },
    { name: "canvas.json", data: enc.encode(JSON.stringify(canvasPayload, null, 2)) },
    { name: "files.json", data: enc.encode(JSON.stringify(filesPayload, null, 2)) },
    { name: "goals.json", data: enc.encode(JSON.stringify(goalsPayload, null, 2)) },
  ];

  downloadBlob(`${slugify(session.title, "session")}-bundle.zip`, buildZipBlob(entries));
}
