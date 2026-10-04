"use client";

/* ============================================================
   Backend client — talks to the .NET sidecar (AIClient.Server)
   over HTTP + NDJSON streams. No mocks: every call lands on the
   real Application/Infrastructure services.
   ============================================================ */

let cachedBase: string | null = null;
let cachedToken: string | null = null;
let backendAvailable: boolean | null = null;

export async function getBackendUrl(): Promise<string> {
  if (cachedBase) return cachedBase;
  try {
    const bridge = window.kontur;
    if (bridge) {
      cachedBase = await bridge.getBackendUrl();
      return cachedBase;
    }
  } catch {
    /* fall through to the loopback default */
  }
  cachedBase = "http://127.0.0.1:45631";
  return cachedBase;
}

/**
 * The sidecar's per-launch bearer token, fetched over the context bridge.
 *
 * It is not in the bundle, not in localStorage and not in the URL: a token in any of those three is
 * a token that survives a repack, and the sidecar rejects everything without it. Cached in a module
 * variable for the life of the renderer only.
 */
async function getBackendToken(): Promise<string | null> {
  if (cachedToken !== null) return cachedToken;
  try {
    const bridge = window.kontur;
    cachedToken = bridge?.getBackendAuth ? await bridge.getBackendAuth() : null;
  } catch {
    cachedToken = null;
  }
  return cachedToken;
}

/** Headers every authenticated call carries. The sidecar refuses anything else with a 401. */
async function authHeaders(): Promise<Record<string, string>> {
  const token = await getBackendToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

export function isBackendKnownAvailable(): boolean | null {
  return backendAvailable;
}

export async function checkBackend(): Promise<boolean> {
  try {
    const base = await getBackendUrl();
    // /api/health is the one route that is deliberately anonymous, so this probe needs no token and
    // is also the right place to learn whether one can be had at all.
    const res = await fetch(`${base}/api/health`, { signal: AbortSignal.timeout(4000) });
    backendAvailable = res.ok;
    return res.ok;
  } catch {
    backendAvailable = false;
    return false;
  }
}

/** Default ceiling for a single request, so a wedged sidecar surfaces as an error and not a hang. */
const REQUEST_TIMEOUT_MS = 120_000;

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const base = await getBackendUrl();
  const res = await fetch(`${base}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(await authHeaders()),
      ...(init?.headers ?? {}),
    },
    // An explicit signal in `init` wins; otherwise every call gets a ceiling. Without it a request
    // to a stopped sidecar waits on the socket indefinitely and the UI simply stops responding.
    signal: init?.signal ?? AbortSignal.timeout(REQUEST_TIMEOUT_MS),
  });
  if (!res.ok) {
    let detail = "";
    try {
      detail = await res.text();
    } catch {
      /* ignore */
    }
    // The sidecar reports failures as JSON `{ "error": "…" }` (e.g. the
    // /providers/custom, checkpoint and tool routes). Surface that human
    // message directly — it lands in destructive toasts and inline error
    // bubbles — and fall back to a bare status line when there's nothing
    // user-facing to show (empty body or an HTML error page).
    let message = detail.trim();
    if (message.startsWith("{")) {
      try {
        const body = JSON.parse(message) as { error?: string; message?: string; title?: string };
        message = body.error ?? body.message ?? body.title ?? "";
      } catch {
        message = "";
      }
    } else if (message.startsWith("<")) {
      message = "";
    }
    throw new Error(message ? message.slice(0, 300) : `Request failed (${res.status})`);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

/* ---------------- NDJSON streaming ---------------- */

export interface StreamFrame {
  type: string;
  name?: string;
  data?: Record<string, unknown>;
  text?: string;
  message?: string;
  messageId?: string;
  kind?: string;
  details?: string;
  retryable?: boolean;
}

/**
 * Reads an NDJSON stream, handing each complete frame to `onFrame`.
 *
 * `onFrame` is awaited. Frame handlers here do asynchronous work — persisting a message, answering
 * an approval, folding a usage record — and firing those off un-awaited against a tight synchronous
 * read loop lets a delta land after the run has already reported itself complete, or lets a rejection
 * escape as an unhandled promise rejection with nothing to attribute it to. The read loop is already
 * async; awaiting the handler costs nothing at this frame rate.
 */
export async function streamNdjson(
  path: string,
  body: unknown,
  onFrame: (frame: StreamFrame) => void | Promise<void>,
  signal?: AbortSignal,
): Promise<void> {
  const base = await getBackendUrl();
  const res = await fetch(`${base}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...(await authHeaders()) },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok || !res.body) {
    throw new Error(await describeFailure(res, path));
  }
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n");
      buffer = lines.pop() ?? "";
      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        let frame: StreamFrame;
        try {
          frame = JSON.parse(trimmed) as StreamFrame;
        } catch {
          /* partial frame — ignore */
          continue;
        }
        await onFrame(frame);
      }
    }
  } finally {
    // The loop above can end in three ways, and only one of them is the stream finishing normally:
    // an abort, a handler that threw, or the connection dropping. Releasing the lock lets the
    // response be collected instead of pinned until GC, and cancelling tells the sidecar to stop
    // writing into a socket nobody is reading.
    try {
      await reader.cancel();
    } catch {
      /* already finished */
    }
    reader.releaseLock();
  }
}

/**
 * Runs an NDJSON stream, retrying once the transport has failed.
 *
 * A run can be minutes long, so a dropped socket is likely rather than exceptional — a laptop
 * changing networks, a proxy reaping an idle connection. Without this the run is simply lost, along
 * with whatever the agent had already done.
 *
 * Retrying re-POSTs the same request. That is safe for these routes because the run id is the
 * caller's, and the server now refuses an id that is already in flight — so a retry that arrives
 * while the original is still alive is rejected rather than starting a second agent.
 *
 * It cannot resume mid-stream: there is no cursor, so a retry starts the run again. That is honest
 * and visible rather than a silent partial continuation, and the agent's tool calls are per-call
 * approved, so a repeated run is the user's call to confirm.
 *
 * Three attempts, backing off. Beyond that the error propagates and the caller reports it.
 */
const STREAM_MAX_ATTEMPTS = 3;

export async function streamNdjsonWithRetry(
  path: string,
  body: unknown,
  onFrame: (frame: StreamFrame) => void | Promise<void>,
  signal?: AbortSignal,
): Promise<void> {
  let lastError: unknown;

  for (let attempt = 1; attempt <= STREAM_MAX_ATTEMPTS; attempt += 1) {
    try {
      await streamNdjson(path, body, onFrame, signal);
      return;
    } catch (err) {
      // A deliberate stop is not a transport failure, and retrying it would start a run the user
      // just cancelled.
      if (signal?.aborted) throw err;

      lastError = err;

      if (attempt === STREAM_MAX_ATTEMPTS) break;

      await new Promise((resolve) => setTimeout(resolve, 400 * 2 ** (attempt - 1)));
    }
  }

  throw lastError instanceof Error
    ? lastError
    : new Error("The connection to the backend failed repeatedly.");
}

/** One error reader for both request shapes, so a stream failure reads like any other failure. */
async function describeFailure(res: Response, path: string): Promise<string> {
  try {
    const body = (await res.text()).trim();
    if (body.startsWith("{")) {
      const parsed = JSON.parse(body) as { error?: string; message?: string };
      const message = parsed.error ?? parsed.message;
      if (message) return message.slice(0, 300);
    }
    if (body && !body.startsWith("<")) return body.slice(0, 300);
  } catch {
    /* fall through to the status line */
  }
  return `backend ${res.status} ${path}`;
}

/* ---------------- conversations ---------------- */

export interface ServerConversationSummary {
  id: string;
  title: string;
  providerId?: string | null;
  modelId?: string | null;
  createdAt: string;
  updatedAt: string;
  isPinned?: boolean;
  pinned?: boolean;
  messageCount: number;
  projectId?: string | null;
  preview?: string | null;
}

export interface ServerAttachment {
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  isTruncated: boolean;
  textContent?: string | null;
}

export interface ServerMessage {
  id: string;
  conversationId: string;
  role: string;
  content: string;
  status: string;
  errorMessage?: string | null;
  errorKind?: string | null;
  sequenceNumber: number;
  createdAt: string;
  providerId?: string | null;
  modelId?: string | null;
  inputTokens?: number | null;
  outputTokens?: number | null;
  reasoningTokens?: number | null;
  generationTimeMs?: number | null;
  attachments: ServerAttachment[];
  isContextSummary: boolean;
  isCompacted: boolean;
  toolCallsJson?: string | null;
  toolCallId?: string | null;
  toolName?: string | null;
  toolSucceeded?: boolean | null;
}

export interface ServerConversationDetail {
  id: string;
  title: string;
  providerId?: string | null;
  modelId?: string | null;
  createdAt: string;
  updatedAt: string;
  projectId?: string | null;
  messages: ServerMessage[];
}

export const conversations = {
  list: (take = 100) => api<ServerConversationSummary[]>(`/api/conversations?take=${take}`),
  search: (q: string) => api<ServerConversationSummary[]>(`/api/conversations/search?q=${encodeURIComponent(q)}`),
  get: (id: string) => api<ServerConversationDetail>(`/api/conversations/${id}`),
  create: (title?: string, providerId?: string, modelId?: string) =>
    api<ServerConversationSummary>(`/api/conversations`, {
      method: "POST",
      body: JSON.stringify({ title, providerId, modelId }),
    }),
  patch: (id: string, patch: { title?: string; isPinned?: boolean; providerId?: string; modelId?: string; projectId?: string | null }) =>
    api<ServerConversationDetail>(`/api/conversations/${id}`, { method: "PATCH", body: JSON.stringify(patch) }),
  remove: (id: string) => api<void>(`/api/conversations/${id}`, { method: "DELETE" }),
  deleteMessage: (id: string) => api<void>(`/api/messages/${id}`, { method: "DELETE" }),
  updateMessage: (id: string, content: string) =>
    api<void>(`/api/messages/${id}`, { method: "PATCH", body: JSON.stringify({ content }) }),
  truncateAfter: (id: string) => api<void>(`/api/messages/${id}/truncate-after`, { method: "POST" }),
};

export interface ServerProject {
  id: string;
  name: string;
}

export const projects = {
  list: () => api<ServerProject[]>(`/api/projects`),
  create: (name: string) =>
    api<ServerProject>(`/api/projects`, { method: "POST", body: JSON.stringify({ name }) }),
  remove: (id: string) => api<void>(`/api/projects/${id}`, { method: "DELETE" }),
};

/* ---------------- providers & models ---------------- */

export interface ServerProvider {
  id: string;
  name: string;
  isEnabled: boolean;
  hasApiKey: boolean;
  connectionState: string;
  statusMessage?: string | null;
  cachedModelCount: number;
  apiKeyUrl?: string | null;
  isCustom: boolean;
}

export interface ServerModel {
  providerId: string;
  providerName: string;
  modelId: string;
  name: string;
  description?: string | null;
  contextWindow?: number | null;
  supportsStreaming: boolean;
  supportsImages: boolean;
  supportsTools: boolean;
  promptPricePerMillion?: number | null;
  completionPricePerMillion?: number | null;
}

export const providers = {
  list: () => api<ServerProvider[]>(`/api/providers`),
  models: (id: string) => api<ServerModel[]>(`/api/providers/${encodeURIComponent(id)}/models`),
  allModels: () => api<ServerModel[]>(`/api/models`),
  refresh: (id: string) => api<ServerModel[]>(`/api/providers/${encodeURIComponent(id)}/refresh`, { method: "POST" }),
  setKey: (id: string, apiKey: string) =>
    api<void>(`/api/providers/${encodeURIComponent(id)}/key`, { method: "POST", body: JSON.stringify({ apiKey }) }),
  deleteKey: (id: string) => api<void>(`/api/providers/${encodeURIComponent(id)}/key`, { method: "DELETE" }),
  test: (id: string) => api<{ success: boolean; message?: string }>(`/api/providers/${encodeURIComponent(id)}/test`, { method: "POST" }),
  setEnabled: (id: string, isEnabled: boolean) =>
    api<void>(`/api/providers/${encodeURIComponent(id)}/enabled`, { method: "POST", body: JSON.stringify({ isEnabled }) }),
  addCustom: (name: string, baseUrl: string) =>
    api<ServerProvider>(`/api/providers/custom`, { method: "POST", body: JSON.stringify({ name, baseUrl }) }),
  removeCustom: (id: string) => api<void>(`/api/providers/${encodeURIComponent(id)}/custom`, { method: "DELETE" }),
};

export interface ServerTool {
  name: string;
  description: string;
  risk: string;
}

export const tools = {
  list: () => api<ServerTool[]>(`/api/tools`),
};

/* ---------------- workspace ---------------- */

export interface ServerWorkspaceEntry {
  path: string;
  name: string;
  isDirectory: boolean;
  size: number;
  modifiedAt: string;
}

export const workspace = {
  status: () => api<{ isOpen: boolean; root?: string | null }>(`/api/workspace`),
  open: (directory: string) => api<{ root: string }>(`/api/workspace/open`, { method: "POST", body: JSON.stringify({ directory }) }),
  close: () => api<void>(`/api/workspace/close`, { method: "POST" }),
  list: (path = ".", recursive = false) =>
    api<{ path: string; isTruncated: boolean; entries: ServerWorkspaceEntry[] }>(
      `/api/workspace/list?path=${encodeURIComponent(path)}&recursive=${recursive}`,
    ),
  read: (path: string, startLine = 1, lineCount?: number) =>
    api<{ path: string; content: string; firstLine: number; lineCount: number; totalLines: number; size: number; isTruncated: boolean }>(
      `/api/workspace/read?path=${encodeURIComponent(path)}&startLine=${startLine}${lineCount ? `&lineCount=${lineCount}` : ""}`,
    ),
  write: (path: string, content: string) =>
    api<{ created: boolean; linesAfter: number }>(`/api/workspace/write`, { method: "POST", body: JSON.stringify({ path, content }) }),
  search: (query: string, path?: string, filePattern?: string) =>
    api<{ filesScanned: number; isTruncated: boolean; matches: { path: string; lineNumber: number; line: string }[] }>(
      `/api/workspace/search`,
      { method: "POST", body: JSON.stringify({ query, path: path ?? null, filePattern: filePattern ?? null }) },
    ),
};

/* ---------------- graph ---------------- */

export interface ServerGraphNode {
  id: string;
  kind: string;
  title: string;
  subtitle?: string | null;
  detail?: string | null;
  path?: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface ServerGraphEdge {
  id: string;
  sourceId: string;
  targetId: string;
  kind: string;
  label?: string | null;
}

export interface ServerGraphSnapshot {
  version: number;
  nodes: ServerGraphNode[];
  edges: ServerGraphEdge[];
}

export interface ServerTimelineEntry {
  id: string;
  at: string;
  title: string;
  description?: string | null;
  origin: string;
  resultingVersion: number;
  nodeCount: number;
  edgeCount: number;
  wasRejected: boolean;
}

export interface GraphChangeInput {
  kind: "add-node" | "update-node" | "move-node" | "remove-node" | "add-edge" | "update-edge" | "remove-edge";
  nodeId?: string;
  node?: { id: string; kind: string; title: string; subtitle?: string; detail?: string; path?: string; x: number; y: number; width?: number; height?: number };
  title?: string;
  subtitle?: string;
  detail?: string;
  nodeKind?: string;
  x?: number;
  y?: number;
  edgeId?: string;
  edge?: { id: string; sourceId: string; targetId: string; kind: string; label?: string };
  edgeKind?: string;
  label?: string;
}

export const graph = {
  snapshot: () => api<ServerGraphSnapshot>(`/api/graph`),
  timeline: () => api<ServerTimelineEntry[]>(`/api/graph/timeline`),
  undo: () => api<{ applied: number; rejected: string[] }>(`/api/graph/undo`, { method: "POST" }),
  redo: () => api<{ applied: number; rejected: string[] }>(`/api/graph/redo`, { method: "POST" }),
  reindex: () => api<{ rejected: string[]; snapshot: ServerGraphSnapshot }>(`/api/graph/reindex`, { method: "POST" }),
  apply: (title: string, changes: GraphChangeInput[], description?: string) =>
    api<{ applied: number; rejected: string[]; snapshot: ServerGraphSnapshot }>(`/api/graph/apply`, {
      method: "POST",
      body: JSON.stringify({ title, description, changes }),
    }),
};

/* ---------------- settings ---------------- */

export const settings = {
  get: () => api<Record<string, unknown>>(`/api/settings`),
  put: (section: string, body: unknown) =>
    api<Record<string, unknown>>(`/api/settings/${section}`, { method: "PUT", body: JSON.stringify(body) }),
};

/* ---------------- export ---------------- */

export const exportApi = {
  conversation: async (id: string, format: "markdown" | "json" | "text"): Promise<string> => {
    const base = await getBackendUrl();
    const res = await fetch(`${base}/api/export/conversation/${id}?format=${format}`);
    if (!res.ok) throw new Error(`export failed: ${res.status}`);
    return res.text();
  },
};

/* ---------------- checkpoints ---------------- */

export interface ServerCheckpointListItem {
  id: string;
  conversationId: string;
  label: string;
  createdAt: string;
  messageId?: string | null;
  fileCount: number;
  hasGraph: boolean;
}

export interface ServerCheckpoint extends ServerCheckpointListItem {
  files: Record<string, string>;
  graphKey?: string | null;
}

export const checkpoints = {
  list: () => api<ServerCheckpointListItem[]>(`/api/checkpoints`),
  get: (id: string) => api<ServerCheckpoint>(`/api/checkpoints/${id}`),
  create: (conversationId: string, label: string, messageId: string | null, files: string[], includeGraph: boolean) =>
    api<{ id: string }>(`/api/checkpoints`, {
      method: "POST",
      body: JSON.stringify({ conversationId, label, messageId, files, includeGraph }),
    }),
  restore: (id: string) => api<{ restoredFiles: number; graphRestored: boolean }>(`/api/checkpoints/${id}/restore`, { method: "POST" }),
  remove: (id: string) => api<void>(`/api/checkpoints/${id}`, { method: "DELETE" }),
};

/* ---------------- runs ---------------- */

export interface RunAttachment {
  fileName: string;
  mimeType?: string;
  textContent?: string;
}

export const runs = {
  chatSend: (
    body: { runId: string; conversationId?: string; title?: string; content: string; providerId: string; modelId: string; attachments?: RunAttachment[] },
    onFrame: (f: StreamFrame) => void | Promise<void>,
    signal?: AbortSignal,
  ) => streamNdjsonWithRetry(`/api/chat/send`, body, onFrame, signal),
  chatRegenerate: (
    body: { runId: string; conversationId: string; assistantMessageId: string; providerId: string; modelId: string },
    onFrame: (f: StreamFrame) => void | Promise<void>,
    signal?: AbortSignal,
  ) => streamNdjsonWithRetry(`/api/chat/regenerate`, body, onFrame, signal),
  agentRun: (
    body: { runId: string; conversationId?: string; title?: string; content: string; providerId: string; modelId: string; mode: string; attachments?: RunAttachment[] },
    onFrame: (f: StreamFrame) => void | Promise<void>,
    signal?: AbortSignal,
  ) => streamNdjsonWithRetry(`/api/agent/run`, body, onFrame, signal),
  approval: (runId: string) => api<{
    approvalId: string;
    runId: string;
    conversationId: string;
    toolName: string;
    risk: string;
    argumentsJson: string;
    summary?: string | null;
    preview?: string | null;
    isRepeat: boolean;
    askedAt: string;
  } | null>(`/api/runs/${runId}/approval`),
  answerApproval: (runId: string, approvalId: string, outcome: "allowed" | "allowed-for-run" | "denied", reason?: string) =>
    api<void>(`/api/runs/${runId}/approval`, {
      method: "POST",
      body: JSON.stringify({ approvalId, outcome, reason }),
    }),
  cancel: (runId: string) => api<void>(`/api/runs/${runId}/cancel`, { method: "POST" }).catch(() => undefined),
};

/* ---------------- git ---------------- */

// Mirrors GitFileState in the sidecar. `git status --porcelain` maps each side to a single
// state, so this is a plain union rather than a bitmask on the wire.
export type GitFileState =
  | "None"
  | "Added"
  | "Modified"
  | "Deleted"
  | "Renamed"
  | "Copied"
  | "Untracked"
  | "Conflicted";

export interface GitFileStatus {
  path: string;
  staged: GitFileState;
  unstaged: GitFileState;
}

export interface GitStatus {
  branch: string;
  upstreamBranch?: string | null;
  isClean: boolean;
  files: GitFileStatus[];
}

export interface GitFileDiff {
  path: string;
  oldPath?: string | null;
  linesAdded: number;
  linesRemoved: number;
}

export interface GitDiff {
  rawDiff: string;
  files: GitFileDiff[];
}

export interface GitCommit {
  sha: string;
  shortSha: string;
  message: string;
  author: string;
  date: string;
  parentShas: string[];
}

export interface GitBranchInfo {
  name: string;
  isCurrent: boolean;
  trackingBranch?: string | null;
  ahead: number;
  behind: number;
}

// Mutating git ops always answer HTTP 200 with this shape — a rejected push or an empty commit
// is a normal outcome the panel renders, so callers read `success`/`error` (they never throw).
export interface GitResult {
  success: boolean;
  output: string;
  error?: string | null;
}

export const git = {
  repo: () => api<{ isRepository: boolean }>(`/api/git/repo`),
  status: () => api<GitStatus>(`/api/git/status`),
  branch: () => api<GitBranchInfo>(`/api/git/branch`),
  branches: () => api<GitBranchInfo[]>(`/api/git/branches`),
  diff: () => api<GitDiff>(`/api/git/diff`),
  fileDiff: (path: string) => api<GitDiff>(`/api/git/diff/file?path=${encodeURIComponent(path)}`),
  history: (maxCount = 50) => api<GitCommit[]>(`/api/git/history?maxCount=${maxCount}`),
  commit: (sha: string) => api<GitCommit>(`/api/git/commit/${encodeURIComponent(sha)}`),
  stage: (paths?: string[]) =>
    api<GitResult>(`/api/git/stage`, { method: "POST", body: JSON.stringify({ paths: paths ?? null }) }),
  unstage: (paths?: string[]) =>
    api<GitResult>(`/api/git/unstage`, { method: "POST", body: JSON.stringify({ paths: paths ?? null }) }),
  createCommit: (message: string) =>
    api<GitResult>(`/api/git/commit`, { method: "POST", body: JSON.stringify({ message }) }),
  createBranch: (name: string) =>
    api<GitResult>(`/api/git/branch/create`, { method: "POST", body: JSON.stringify({ name }) }),
  checkout: (name: string) =>
    api<GitResult>(`/api/git/checkout`, { method: "POST", body: JSON.stringify({ name }) }),
  push: (opts?: { remote?: string; branch?: string; setUpstream?: boolean }) =>
    api<GitResult>(`/api/git/push`, { method: "POST", body: JSON.stringify(opts ?? {}) }),
  pull: (opts?: { remote?: string; branch?: string }) =>
    api<GitResult>(`/api/git/pull`, { method: "POST", body: JSON.stringify(opts ?? {}) }),
  fetch: (remote?: string) =>
    api<GitResult>(`/api/git/fetch`, { method: "POST", body: JSON.stringify({ remote: remote ?? null }) }),
};

/* ---------------- ai completion (inline edits & ghost-text) ---------------- */

export interface CompleteResult {
  text: string;
  finishReason?: string | null;
}

export interface CompleteRequestBody {
  providerId: string;
  modelId: string;
  prompt: string;
  system?: string;
  temperature?: number;
  maxTokens?: number;
}

export const ai = {
  // Single-shot completion. Rejects with an Error on a provider refusal (HTTP 502) and with an
  // AbortError when `signal` fires — ghost-text supersedes its own requests, so callers swallow that.
  complete: (body: CompleteRequestBody, signal?: AbortSignal) =>
    api<CompleteResult>(`/api/ai/complete`, { method: "POST", body: JSON.stringify(body), signal }),
};
