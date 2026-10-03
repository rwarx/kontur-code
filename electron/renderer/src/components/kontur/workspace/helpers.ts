import type { DemoFile, RunnerState } from "@/lib/kontur/types";

/* ============================================================
   Workspace surfaces — shared pure helpers.
   (Trajectory / Tasks / Files / Code views)
   ============================================================ */

export type TFn = (key: string, ...args: (string | number)[]) => string;

/** "just now" / "4m ago" / "3h ago" / "yesterday" / locale date */
export function relativeTime(ts: number, t: TFn): string {
  const diff = Date.now() - ts;
  const minutes = Math.floor(diff / 60000);
  if (minutes < 1) return t("time.justNow");
  if (minutes < 60) return t("time.minutesAgo", minutes);
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return t("time.hoursAgo", hours);
  if (hours < 48) return t("time.yesterday");
  return new Date(ts).toLocaleDateString();
}

/** HH:mm:ss for the timeline rail */
export function formatClock(ts: number): string {
  const d = new Date(ts);
  return [d.getHours(), d.getMinutes(), d.getSeconds()]
    .map((n) => String(n).padStart(2, "0"))
    .join(":");
}

/** 640ms / 3.2s */
export function formatDuration(ms?: number): string {
  if (ms == null) return "";
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
}

/** content length as "1.8 KB" */
export function formatSize(content: string): string {
  const kb = content.length / 1024;
  return `${kb < 10 ? kb.toFixed(1) : Math.round(kb)} KB`;
}

/** Human label for the runner state — falls back to a mapped status word. */
export function runnerStatusText(t: TFn, status: RunnerState["status"], label?: string): string {
  if (label) return label;
  switch (status) {
    case "running":
      return t("tasks.status.running");
    case "awaiting-approval":
      return t("tasks.waitingForYou");
    case "done":
      return t("tasks.status.done");
    case "stopped":
      return t("tasks.status.stopped");
    case "paused":
      return t("tasks.status.paused");
    default:
      return t("tasks.status.idle");
  }
}

/* ---------- file tree (Files view) ---------- */

export interface FileTreeNode {
  name: string;
  path: string;
  dir: boolean;
  children: FileTreeNode[];
  file?: DemoFile;
}

function sortNodes(nodes: FileTreeNode[]): FileTreeNode[] {
  nodes.sort((a, b) => (a.dir === b.dir ? a.name.localeCompare(b.name) : a.dir ? -1 : 1));
  nodes.forEach((n) => sortNodes(n.children));
  return nodes;
}

/** Builds a folder/file tree from flat workspace paths. Folders first, then alphabetical. */
export function buildFileTree(files: DemoFile[]): FileTreeNode[] {
  const root: FileTreeNode = { name: "", path: "", dir: true, children: [] };
  for (const file of files) {
    const parts = file.path.split("/");
    let node = root;
    parts.forEach((part, i) => {
      const isFile = i === parts.length - 1;
      const path = parts.slice(0, i + 1).join("/");
      let child = node.children.find((c) => c.name === part && c.dir === !isFile);
      if (!child) {
        child = { name: part, path, dir: !isFile, children: [], file: isFile ? file : undefined };
        node.children.push(child);
      }
      node = child;
    });
  }
  return sortNodes(root.children);
}
