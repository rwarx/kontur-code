import type { DiffLine } from "./types";

/* ============================================================
   Unified line diff (LCS) with context collapsing + hunks.
   Used by the checkpoint review view in the Code surface:
   compares a checkpoint's file snapshot against the current
   content and renders through the chat DiffView component.
   ============================================================ */

/** Split text into lines, dropping a single trailing newline. */
function toLines(text: string): string[] {
  const body = text.endsWith("\n") ? text.slice(0, -1) : text;
  return body.length ? body.split("\n") : [];
}

/**
 * Longest-common-subsequence opcodes: for each old line either
 * "keep", "del" or (for new lines) "add". Classic DP — workspace
 * files are small (< 100 lines), O(n·m) is plenty.
 */
function lcsOps(a: string[], b: string[]): { op: "keep" | "del" | "add"; line: string }[] {
  const n = a.length;
  const m = b.length;
  /* dp[i][j] = LCS length of a[i..] and b[j..] */
  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }
  const ops: { op: "keep" | "del" | "add"; line: string }[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      ops.push({ op: "keep", line: a[i] });
      i++;
      j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      ops.push({ op: "del", line: a[i] });
      i++;
    } else {
      ops.push({ op: "add", line: b[j] });
      j++;
    }
  }
  while (i < n) ops.push({ op: "del", line: a[i++] });
  while (j < m) ops.push({ op: "add", line: b[j++] });
  return ops;
}

const CONTEXT = 3; /* context lines around each change */

/**
 * Computes a unified diff (old → new) as DiffLine[] with
 * `header` hunk markers in git style. Empty when identical.
 */
export function computeUnifiedDiff(oldText: string, newText: string): DiffLine[] {
  const a = toLines(oldText);
  const b = toLines(newText);
  if (a.join("\n") === b.join("\n")) return [];

  const ops = lcsOps(a, b);
  const changed = ops.map((o) => o.op !== "keep");

  /* mark windows of CONTEXT around every change */
  const visible = new Array<boolean>(ops.length).fill(false);
  for (let k = 0; k < ops.length; k++) {
    if (!changed[k]) continue;
    for (let w = Math.max(0, k - CONTEXT); w <= Math.min(ops.length - 1, k + CONTEXT); w++) {
      visible[w] = true;
    }
  }

  /* walk old/new line numbers for hunk headers */
  let oldNo = 0;
  let newNo = 0;
  const numbered = ops.map((o) => {
    if (o.op === "keep") {
      oldNo++;
      newNo++;
      return { ...o, oldNo, newNo };
    }
    if (o.op === "del") {
      oldNo++;
      return { ...o, oldNo, newNo };
    }
    newNo++;
    return { ...o, oldNo, newNo };
  });

  const out: DiffLine[] = [];
  let inHunk = false;
  let skippedSinceHunk = false;
  let gap = 0;
  for (let k = 0; k < numbered.length; k++) {
    if (visible[k]) {
      if (!inHunk) {
        if (skippedSinceHunk) out.push({ kind: "context", text: "⋯" });
        const first = numbered[k];
        out.push({
          kind: "header",
          text: `@@ ${first.oldNo},${countHunk(numbered, visible, k, "old")} → ${first.newNo},${countHunk(numbered, visible, k, "new")} @@`,
        });
        inHunk = true;
      }
      const o = numbered[k];
      out.push({ kind: o.op === "keep" ? "context" : o.op === "del" ? "del" : "add", text: o.line });
      gap = 0;
    } else {
      if (inHunk) {
        inHunk = false;
        skippedSinceHunk = true;
      }
      gap++;
    }
  }
  return out;
}

/** number of old/new lines the hunk starting at `start` will emit */
function countHunk(
  numbered: { op: "keep" | "del" | "add" }[],
  visible: boolean[],
  start: number,
  which: "old" | "new",
): number {
  let count = 0;
  for (let k = start; k < numbered.length && visible[k]; k++) {
    const op = numbered[k].op;
    if (op === "keep" || (which === "old" ? op === "del" : op === "add")) count++;
  }
  return count;
}

/** +/− stats as {add, del} over a DiffLine[] */
export function diffStats(lines: DiffLine[]): { add: number; del: number } {
  let add = 0;
  let del = 0;
  for (const l of lines) {
    if (l.kind === "add") add++;
    else if (l.kind === "del") del++;
  }
  return { add, del };
}

/* ============================================================
   Per-hunk selection (Tier 1 #5). A "hunk" is a maximal run of
   changed lines (del/add) between runs of unchanged context.
   Accepting a hunk keeps the NEW lines; rejecting keeps the OLD
   lines. This lets the review screen apply changes piecemeal,
   reconstructing a file that mixes accepted + reverted regions.
   ============================================================ */

export interface Hunk {
  /** stable index within the file's ordered hunk list */
  index: number;
  /** 1-based line where the change begins in the OLD text (0 → pure insert at top) */
  oldStart: number;
  /** 1-based line where the change begins in the NEW text */
  newStart: number;
  oldLines: string[];
  newLines: string[];
}

/** Ordered hunks between the two texts. Empty when identical. */
export function computeHunks(oldText: string, newText: string): Hunk[] {
  const ops = lcsOps(toLines(oldText), toLines(newText));
  const hunks: Hunk[] = [];
  let oldNo = 0;
  let newNo = 0;
  let cur: { oldStart: number; newStart: number; oldLines: string[]; newLines: string[] } | null = null;
  const flush = () => {
    if (cur) {
      hunks.push({ index: hunks.length, ...cur });
      cur = null;
    }
  };
  for (const o of ops) {
    if (o.op === "keep") {
      flush();
      oldNo++;
      newNo++;
    } else {
      if (!cur) cur = { oldStart: oldNo + 1, newStart: newNo + 1, oldLines: [], newLines: [] };
      if (o.op === "del") {
        cur.oldLines.push(o.line);
        oldNo++;
      } else {
        cur.newLines.push(o.line);
        newNo++;
      }
    }
  }
  flush();
  return hunks;
}

/**
 * Reconstruct file content from `oldText`→`newText` keeping only the hunks
 * whose index is in `accepted` (rejected hunks fall back to the old lines).
 * With all hunks accepted the result equals `newText`; with none, `oldText`.
 */
export function applyHunkSelection(oldText: string, newText: string, accepted: Set<number>): string {
  const ops = lcsOps(toLines(oldText), toLines(newText));
  const out: string[] = [];
  let hunkIdx = -1;
  let i = 0;
  while (i < ops.length) {
    if (ops[i].op === "keep") {
      out.push(ops[i].line);
      i++;
      continue;
    }
    hunkIdx++;
    const dels: string[] = [];
    const adds: string[] = [];
    while (i < ops.length && ops[i].op !== "keep") {
      if (ops[i].op === "del") dels.push(ops[i].line);
      else adds.push(ops[i].line);
      i++;
    }
    if (accepted.has(hunkIdx)) out.push(...adds);
    else out.push(...dels);
  }
  const keepNewline = accepted.size > 0 ? newText.endsWith("\n") : oldText.endsWith("\n");
  return out.join("\n") + (out.length && keepNewline ? "\n" : "");
}

/** DiffLine[] for a single hunk (with its own header), for per-hunk rendering. */
export function hunkDiffLines(h: Hunk): DiffLine[] {
  const lines: DiffLine[] = [
    { kind: "header", text: `@@ -${h.oldStart},${h.oldLines.length} +${h.newStart},${h.newLines.length} @@` },
  ];
  for (const l of h.oldLines) lines.push({ kind: "del", text: l });
  for (const l of h.newLines) lines.push({ kind: "add", text: l });
  return lines;
}
