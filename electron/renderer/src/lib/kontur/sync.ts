"use client";

/* ============================================================
   Backend sync — makes the zustand store server-backed.
   - boot hydration: providers/models, settings, sessions,
     checkpoints, workspace files, canvas graph
   - server-backed session actions (id is a Guid) + transcript
     reload after runs (canonical ids, usage, titles)
   Local demo/seed sessions (non-Guid ids) keep working offline.
   ============================================================ */

import {
  api,
  checkBackend,
  checkpoints,
  conversations,
  exportApi,
  getBackendUrl,
  graph,
  providers,
  runs,
  settings,
  workspace,
  type GraphChangeInput,
  type ServerConversationDetail,
  type ServerConversationSummary,
  type ServerGraphSnapshot,
  type ServerMessage,
  type ServerModel,
} from "./backend";
import { newId, sessionModeOf, useKontur } from "./store";
import type {
  CanvasEdge,
  CanvasNode,
  Checkpoint,
  DemoFile,
  EdgeKind,
  Message,
  ModelInfo,
  NodeKind,
  Provider,
  ProviderState,
  Session,
  ToolCall,
  TrajectoryEvent,
  WorkMode,
} from "./types";

const S = () => useKontur.getState();

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const isServerId = (id: string) => GUID_RE.test(id);

let serverMode = false;
export const isServerMode = () => serverMode;

/** Fired after a workspace is opened/reindexed so the canvas can fit the fresh
 *  graph into view. CanvasView listens for this; other surfaces ignore it. */
export const CANVAS_FIT_EVENT = "kontur:canvas-fit";
function requestCanvasFit(): void {
  if (typeof window !== "undefined") window.dispatchEvent(new Event(CANVAS_FIT_EVENT));
}

let booted = false;

/* ---------------- mapping ---------------- */

const toMs = (iso: string) => {
  const t = Date.parse(iso);
  return Number.isNaN(t) ? Date.now() : t;
};

function splitModelId(composite: string): { providerId: string; modelId: string } {
  const i = composite.indexOf("/");
  if (i < 0) return { providerId: "openrouter", modelId: composite };
  return { providerId: composite.slice(0, i), modelId: composite.slice(i + 1) };
}

export function toCompositeModel(providerId: string, modelId: string) {
  return `${providerId}/${modelId}`;
}

export function splitCompositeModel(composite: string): { providerId: string; modelId: string } {
  const i = composite.indexOf("/");
  if (i < 0) return { providerId: "openrouter", modelId: composite };
  return { providerId: composite.slice(0, i), modelId: composite.slice(i + 1) };
}

interface StoredToolCall {
  id: string;
  name: string;
  argumentsJson: string;
}

function parseStoredCalls(json: string | null | undefined): StoredToolCall[] {
  if (!json) return [];
  try {
    const arr = JSON.parse(json) as StoredToolCall[];
    if (!Array.isArray(arr)) return [];
    return arr.filter((c) => c && typeof c.id === "string" && typeof c.name === "string");
  } catch {
    return [];
  }
}

function toolStateFrom(outcome: boolean | null | undefined, hasMessage: boolean): ToolCall["state"] {
  if (!hasMessage) return "proposed";
  if (outcome === true) return "succeeded";
  if (outcome === false) return "failed";
  return "running";
}

function riskFrom(tool: string): ToolCall["risk"] {
  const t = tool.toLowerCase();
  if (t.includes("run_command") || t.includes("command")) return "execute";
  if (
    t.includes("write") || t.includes("edit") || t.includes("delete") || t.includes("move") ||
    t.includes("commit") || t.includes("checkout") || t.includes("revert") || t.includes("mkdir") ||
    t.includes("create_directory")
  ) {
    return "write";
  }
  return "read";
}

/** Fold tool rows under their assistant step; synthesize trajectory events. */
export function mapDetail(detail: ServerConversationDetail): { messages: Message[]; events: TrajectoryEvent[] } {
  const messages: Message[] = [];
  const events: TrajectoryEvent[] = [];
  const toolByCallId = new Map<string, ServerMessage>();
  for (const m of detail.messages) {
    if (m.role === "Tool" && m.toolCallId) toolByCallId.set(m.toolCallId, m);
  }

  let assistantById = new Map<string, Message>();
  let firstUser: ServerMessage | null = null;

  for (const m of detail.messages) {
    const role = m.role;
    if (role === "Tool") continue;
    if (role === "User" && !firstUser) {
      firstUser = m;
      events.push({
        id: newId("ev"),
        ts: toMs(m.createdAt),
        kind: "request",
        title: m.content.slice(0, 80),
        status: "done",
      });
    }
    const msg: Message = {
      id: m.id,
      role: role === "User" ? "user" : "assistant",
      content: m.content,
      reasoning: undefined,
      createdAt: toMs(m.createdAt),
      modelId: m.modelId ? toCompositeModel(m.providerId ?? "openrouter", m.modelId) : undefined,
      providerId: m.providerId ?? undefined,
      streaming: m.status === "Streaming",
      toolCalls: [],
      live: true,
      usage:
        m.inputTokens || m.outputTokens
          ? {
              inputTokens: m.inputTokens ?? 0,
              outputTokens: m.outputTokens ?? 0,
              ms: m.generationTimeMs ?? 0,
            }
          : undefined,
      folded: m.isCompacted || undefined,
    };
    if (msg.role === "assistant") {
      const stored = parseStoredCalls(m.toolCallsJson);
      for (const call of stored) {
        const row = toolByCallId.get(call.id);
        msg.toolCalls.push({
          id: call.id,
          tool: call.name,
          risk: riskFrom(call.name),
          state: toolStateFrom(row?.toolSucceeded, !!row),
          headline: row?.content?.split("\n")[0]?.slice(0, 120) || call.name,
          body: row?.content,
        });
        if (row) {
          events.push({
            id: newId("ev"),
            ts: toMs(row.createdAt),
            kind: "tool",
            title: `${call.name} · ${(row.content?.split("\n")[0] ?? "").slice(0, 80)}`,
            status: row.toolSucceeded === false ? "failed" : "done",
          });
        }
      }
      // tool rows without a stored call entry (interrupted runs): attach by proximity
      for (const [callId, row] of toolByCallId) {
        if (msg.toolCalls.some((t) => t.id === callId)) continue;
        if (toMs(row.createdAt) < toMs(m.createdAt)) continue;
        msg.toolCalls.push({
          id: callId,
          tool: row.toolName ?? "tool",
          risk: riskFrom(row.toolName ?? ""),
          state: toolStateFrom(row.toolSucceeded, true),
          headline: row.content?.split("\n")[0]?.slice(0, 120) || (row.toolName ?? "tool"),
          body: row.content,
        });
      }
      assistantById.set(m.id, msg);
      if (m.status === "Failed") {
        // Surface the failure on the message itself so the bubble can show it
        // inline (see MessageItem) — not only as a trajectory event the user
        // never looks at. Empty content + no error read as a broken reply.
        msg.error = m.errorMessage || "Request failed";
        if (m.errorKind) msg.errorKind = m.errorKind;
        events.push({
          id: newId("ev"),
          ts: toMs(m.createdAt),
          kind: "error",
          title: m.errorMessage || "Request failed",
          status: "failed",
        });
      }
    }
    messages.push(msg);
  }
  void assistantById;
  return { messages, events };
}

/* Server summaries carry no work mode (chats aren't partitioned server-side),
   so the caller supplies the mode it remembers for this conversation id; an
   unknown one stays undefined and reads as "chat" via sessionModeOf. */
function mapSummary(s: ServerConversationSummary, mode?: WorkMode): Session {
  return {
    id: s.id,
    title: s.title,
    createdAt: toMs(s.createdAt),
    updatedAt: toMs(s.updatedAt),
    pinned: s.isPinned ?? s.pinned ?? false,
    preview: s.preview ?? "",
    messages: [],
    events: [],
    goalIds: [],
    modelId:
      s.providerId && s.modelId ? toCompositeModel(s.providerId, s.modelId) : S().ui.selectedModelId,
    contextTokens: undefined,
    mode,
  };
}

function mapProviders(list: Awaited<ReturnType<typeof providers.list>>): Provider[] {
  return list.map((p) => {
    /* Trust the backend's probed ConnectionState (serialized as its enum name)
       rather than inferring "connected" from mere key presence — a stored-but-
       untested or failed key must never read as live. Falls back to "unknown"
       when a key exists but no state was reported. */
    const raw = (p.connectionState ?? "").toLowerCase();
    const state: ProviderState =
      raw === "connected" ? "connected"
      : raw === "testing" ? "testing"
      : raw === "failed" ? "failed"
      : raw === "notconfigured" ? "missing-key"
      : raw === "unknown" ? "unknown"
      : p.hasApiKey ? "unknown" : "missing-key";
    return {
      id: p.id,
      name: p.name,
      endpoint: p.apiKeyUrl ?? "",
      state,
      statusMessage: p.statusMessage ?? undefined,
      models: [],
      builtin: !p.isCustom,
      enabled: p.isEnabled,
      apiKeyUrl: p.apiKeyUrl ?? undefined,
    };
  });
}

/* ---------------- boot hydration ---------------- */

export interface HydrateResult {
  ok: boolean;
  sessions: number;
  providers: number;
}

export async function hydrateFromBackend(): Promise<HydrateResult> {
  if (booted) return { ok: serverMode, sessions: 0, providers: 0 };
  booted = true;
  const ok = await checkBackend();
  if (!ok) return { ok: false, sessions: 0, providers: 0 };
  serverMode = true;

  await refreshProviders();

  // settings → ui prefs (theme/locale are renderer-owned; map the rest)
  let uiPatch: Record<string, unknown> = {};
  try {
    const s = await settings.get();
    const general = (s["general"] ?? {}) as Record<string, unknown>;
    const chat = (s["chat"] ?? {}) as Record<string, unknown>;
    const agent = (s["agent"] ?? {}) as Record<string, unknown>;
    const appearance = (s["appearance"] ?? {}) as Record<string, unknown>;
    const lang = general["language"];
    const locale = lang === 1 || lang === "Russian" ? "ru" : lang === 2 || lang === "German" ? "de" : "en";
    const themeRaw = appearance["theme"];
    const theme =
      themeRaw === 1 || themeRaw === "Light"
        ? "light"
        : themeRaw === 2 || themeRaw === "Dark"
          ? "dark"
          : typeof window !== "undefined" && window.matchMedia?.("(prefers-color-scheme: light)").matches
            ? "light"
            : "dark";
    uiPatch = {
      locale,
      theme,
      enterSend: chat["sendWithEnter"] ?? true,
      renderMarkdown: chat["renderMarkdown"] ?? true,
      autoscroll: chat["autoScroll"] ?? true,
      maxSteps: (agent["maxSteps"] as number) ?? 40,
    };
    const dp = (chat["defaultProviderId"] as string) || "";
    const dm = (chat["defaultModelId"] as string) || "";
    if (dp && dm) {
      // The server's chat default only *seeds* the picker; it must not clobber
      // the model the user last chose (persisted in ui.selectedModelId) on every
      // boot/re-hydrate — that was the "selected model isn't retained" bug.
      // Keep a still-valid persisted pick; fall back to the server default only
      // when the catalogue actually loaded and the pick isn't in it (fresh
      // install, or a model that no longer exists). Never clobber just because
      // the model list failed to load this boot.
      const current = S().ui.selectedModelId;
      const catalogueLoaded = S().providers.some((p) => p.models.length > 0);
      const resolvable = !!current && S().providers.some((p) => p.models.some((m) => m.id === current));
      if (catalogueLoaded && !resolvable) uiPatch["selectedModelId"] = toCompositeModel(dp, dm);
    }
  } catch {
    /* keep renderer defaults */
  }

  // sessions (summaries only; details load on select). Server summaries have no
  // work mode, so re-stamp each from the mode we persisted for that id last time
  // (server ids are stable) — otherwise per-mode chat partitioning would reset to
  // "chat" for every server conversation on every boot.
  const priorModes = new Map(S().sessions.map((s) => [s.id, s.mode] as const));
  const summaries = await conversations.list(100).catch(() => [] as ServerConversationSummary[]);
  const sessions = summaries.map((s) => mapSummary(s, priorModes.get(s.id)));

  // checkpoints
  const cps = await checkpoints.list().catch(() => []);
  const mappedCps: Checkpoint[] = [];
  for (const c of cps) {
    try {
      const full = await checkpoints.get(c.id);
      mappedCps.push({
        id: full.id,
        label: full.label,
        createdAt: toMs(full.createdAt),
        filesSnapshot: Object.entries(full.files).map(([path, content]) => ({ path, content })),
        sessionId: full.conversationId,
        message: full.messageId ?? "",
      });
    } catch {
      /* skip unreadable checkpoint */
    }
  }

  const prev = S();
  const keepLocal = prev.sessions.filter((s) => !isServerId(s.id));
  useKontur.setState({
    providers: S().providers,
    ui: { ...prev.ui, ...uiPatch },
    sessions: [...sessions, ...keepLocal].sort((a, b) => {
      if (!!b.pinned !== !!a.pinned) return b.pinned ? 1 : -1;
      return b.updatedAt - a.updatedAt;
    }),
    checkpoints: mappedCps,
  });
  installServerActions();
  installCanvasOverrides(prev);
  void syncWorkspaceFiles().catch(() => undefined);
  void syncCanvas().catch(() => undefined);
  return { ok: true, sessions: sessions.length, providers: S().providers.length };
}

/* ---------------- providers ---------------- */

const modelToUi = (m: ServerModel): ModelInfo => ({
  id: toCompositeModel(m.providerId, m.modelId),
  name: m.name,
  providerId: m.providerId,
  contextK: m.contextWindow ? Math.max(1, Math.round(m.contextWindow / 1024)) : 32,
  vision: m.supportsImages,
  tools: m.supportsTools,
  pricePrompt: m.promptPricePerMillion ?? undefined,
});

/** (Re)load providers + catalogue from the server into the store. */
export async function refreshProviders(): Promise<void> {
  if (!serverMode && !(await checkBackend())) return;
  serverMode = true;
  const [infos, allModels] = await Promise.all([providers.list(), providers.allModels().catch(() => [])]);
  const mapped = mapProviders(infos);
  const byId = new Map(mapped.map((p) => [p.id, p]));
  for (const m of allModels) {
    const p = byId.get(m.providerId);
    if (p && !p.models.some((x) => x.id === toCompositeModel(m.providerId, m.modelId))) {
      p.models.push(modelToUi(m));
    }
  }
  for (const p of mapped.slice(0, 12)) {
    if (p.models.length === 0) {
      try {
        const ms = await providers.models(p.id);
        for (const m of ms) {
          if (!p.models.some((x) => x.id === toCompositeModel(m.providerId, m.modelId))) {
            p.models.push(modelToUi(m));
          }
        }
      } catch {
        /* offline catalogue for this provider — keep the row */
      }
    }
  }
  useKontur.setState({ providers: mapped });
}

/* ---------------- server settings ---------------- */

export interface ServerSettingsSections {
  general: Record<string, unknown>;
  chat: Record<string, unknown>;
  agent: Record<string, unknown>;
  appearance: Record<string, unknown>;
  storage: Record<string, unknown>;
  canvas: Record<string, unknown>;
}

export async function loadServerSettings(): Promise<ServerSettingsSections | null> {
  try {
    const s = await settings.get();
    return {
      general: (s["general"] ?? {}) as Record<string, unknown>,
      chat: (s["chat"] ?? {}) as Record<string, unknown>,
      agent: (s["agent"] ?? {}) as Record<string, unknown>,
      appearance: (s["appearance"] ?? {}) as Record<string, unknown>,
      storage: (s["storage"] ?? {}) as Record<string, unknown>,
      canvas: (s["canvas"] ?? {}) as Record<string, unknown>,
    };
  } catch {
    return null;
  }
}

export async function saveServerSettings(section: string, body: unknown): Promise<boolean> {
  try {
    await settings.put(section, body);
    return true;
  } catch {
    return false;
  }
}

/* ---------------- local sessions (demo/offline) ---------------- */

/** Create a local-only session (demo theater, offline fallback). Never touches the server. */
export function newLocalSession(title = "New chat"): string {
  const id = newId("s");
  useKontur.setState((st) => {
    const mode = st.ui.workMode;
    return {
      sessions: [
        {
          id,
          title,
          createdAt: Date.now(),
          updatedAt: Date.now(),
          preview: "",
          messages: [],
          events: [],
          goalIds: [],
          modelId: st.ui.selectedModelId,
          mode,
        },
        ...st.sessions,
      ],
      currentSessionId: id,
      surface: "chat",
      approval: null,
      ui: { ...st.ui, activeSessionByMode: { ...st.ui.activeSessionByMode, [mode]: id } },
    };
  });
  return id;
}

/* ---------------- server-backed actions ---------------- */

/** Pull the canonical transcript for a server conversation into its session row.
 *  Used lazily on select and on mode-switch — the summary lists carry no messages. */
async function loadServerTranscript(id: string): Promise<void> {
  try {
    const detail = await conversations.get(id);
    const { messages, events } = mapDetail(detail);
    useKontur.setState((st) => ({
      sessions: st.sessions.map((s) =>
        s.id === id
          ? {
              ...s,
              title: detail.title,
              messages,
              events,
              modelId:
                detail.providerId && detail.modelId
                  ? toCompositeModel(detail.providerId, detail.modelId)
                  : s.modelId,
            }
          : s,
      ),
    }));
  } catch {
    S().pushToast("Could not open conversation", "The backend did not return the transcript.");
  }
}

function installServerActions() {
  const prev = S();
  useKontur.setState({
    selectSession: (id: string) => {
      // Base action aligns work mode to the chat, sets the chat surface, clears any
      // approval and remembers it for its mode; we only add the server transcript pull.
      prev.selectSession(id);
      if (isServerId(id)) void loadServerTranscript(id);
    },
    setWorkMode: (mode: WorkMode) => {
      // Base action repoints currentSessionId to this mode's remembered/most-recent
      // chat (or a fresh placeholder). If that chat is a server one we haven't opened
      // yet, pull its transcript so switching modes shows the conversation, not a blank.
      prev.setWorkMode(mode);
      const t = S().sessions.find((x) => x.id === S().currentSessionId);
      if (t && isServerId(t.id) && t.messages.length === 0) void loadServerTranscript(t.id);
    },
    newSession: () => {
      const local = newId("s");
      // Stamp the chat with the mode it's born into so the mode-filtered sidebar
      // shows it, and remember it as that mode's active chat. Captured now, since
      // the user may switch modes before the server round-trip resolves.
      const mode = S().ui.workMode;
      void (async () => {
        try {
          const { providerId, modelId } = splitModelId(S().ui.selectedModelId);
          const created = await conversations.create("New chat", providerId, modelId);
          const session: Session = { ...mapSummary(created, mode), mode };
          useKontur.setState((st) => ({
            sessions: [session, ...st.sessions.filter((s) => s.id !== session.id)],
            currentSessionId: session.id,
            surface: "chat",
            ui: { ...st.ui, activeSessionByMode: { ...st.ui.activeSessionByMode, [mode]: session.id } },
          }));
        } catch {
          // offline fallback: local session
          useKontur.setState((st) => ({
            sessions: [
              {
                id: local,
                title: "New chat",
                createdAt: Date.now(),
                updatedAt: Date.now(),
                preview: "",
                messages: [],
                events: [],
                goalIds: [],
                modelId: st.ui.selectedModelId,
                mode,
              },
              ...st.sessions,
            ],
            currentSessionId: local,
            surface: "chat",
            ui: { ...st.ui, activeSessionByMode: { ...st.ui.activeSessionByMode, [mode]: local } },
          }));
        }
      })();
      return local;
    },
    deleteSession: (id: string) => {
      if (isServerId(id)) {
        void conversations.remove(id).catch(() => undefined);
      }
      prev.deleteSession(id);
    },
    renameSession: (id: string, title: string) => {
      if (isServerId(id)) {
        void conversations.patch(id, { title }).catch(() => undefined);
      }
      useKontur.setState((st) => ({
        sessions: st.sessions.map((s) => (s.id === id ? { ...s, title } : s)),
      }));
    },
    setSessionPinned: (id: string, pinned: boolean) => {
      if (isServerId(id)) {
        void conversations.patch(id, { isPinned: pinned }).catch(() => undefined);
      }
      useKontur.setState((st) => ({
        sessions: st.sessions
          .map((s) => (s.id === id ? { ...s, pinned } : s))
          .sort((a, b) => {
            if (!!b.pinned !== !!a.pinned) return b.pinned ? 1 : -1;
            return b.updatedAt - a.updatedAt;
          }),
      }));
    },
    addCheckpoint: (cp: Checkpoint) => {
      void (async () => {
        try {
          const session = S().sessions.find((s) => s.id === S().currentSessionId);
          const convId = session && isServerId(session.id) ? session.id : S().currentSessionId;
          const files = cp.filesSnapshot.map((f) => f.path);
          const created = await checkpoints.create(
            isServerId(convId) ? convId : "00000000-0000-0000-0000-000000000000",
            cp.label,
            null,
            files,
            !!cp.canvasSnapshot,
          );
          useKontur.setState((st) => ({
            checkpoints: [...st.checkpoints, { ...cp, id: created.id }],
          }));
        } catch {
          useKontur.setState((st) => ({ checkpoints: [...st.checkpoints, cp] }));
        }
      })();
    },
    restoreCheckpoint: (id: string) => {
      void (async () => {
        if (isServerId(id)) {
          try {
            const res = await checkpoints.restore(id);
            S().pushToast("Checkpoint restored", `${res.restoredFiles} file(s) reverted.`);
            await syncWorkspaceFiles();
            await syncCanvas();
            return;
          } catch {
            S().pushToast("Restore failed", "The backend could not restore the checkpoint.", "destructive");
            return;
          }
        }
        prev.restoreCheckpoint(id);
      })();
    },
  });
}

/** Reload the canonical transcript for a server conversation (after runs). */
export async function reloadCurrentSession(): Promise<void> {
  const st = S();
  const id = st.currentSessionId;
  if (!isServerId(id)) return;
  const detail = await conversations.get(id);
  const { messages, events } = mapDetail(detail);
  useKontur.setState((s) => {
    const local = s.sessions.find((x) => x.id === id);
    // A failed/empty provider turn can leave the server conversation with the
    // whole turn rolled back (no persisted messages). The wire already streamed
    // the transcript into the local store, so a shorter/empty server reload must
    // NOT wipe it — that was the "chat fully resets on a provider error" bug.
    // Keep the local messages whenever the server hands back strictly fewer.
    const keepLocal = !!local && local.messages.length > messages.length;
    const nextMessages = keepLocal && local ? local.messages : messages;
    return {
      sessions: s.sessions.map((x) =>
        x.id === id
          ? {
              ...x,
              title: detail.title,
              updatedAt: toMs(detail.updatedAt),
              messages: nextMessages,
              events: mergeEvents(x.events, events),
              modelId:
                detail.providerId && detail.modelId
                  ? toCompositeModel(detail.providerId, detail.modelId)
                  : x.modelId,
            }
          : x,
      ),
    };
  });
}

function mergeEvents(local: TrajectoryEvent[], server: TrajectoryEvent[]): TrajectoryEvent[] {
  // keep local-only kinds the server never emits (fork/compact/skills/checkpoint-local)
  const keep = local.filter((e) => ["fork", "compact", "skills"].includes(e.kind));
  return [...server, ...keep].sort((a, b) => a.ts - b.ts);
}

/** Ensure the current session is server-backed; returns its conversation id. */
export async function ensureServerSession(): Promise<string> {
  const st = S();
  if (isServerId(st.currentSessionId)) return st.currentSessionId;
  const localId = st.currentSessionId;
  const { providerId, modelId } = splitModelId(st.ui.selectedModelId);
  const current = st.currentSession();
  // the upgraded conversation stays in the mode its placeholder belonged to
  const mode = current?.mode ?? st.ui.workMode;
  const created = await conversations.create(
    current?.title && current.title !== "New chat" ? current.title : "New chat",
    providerId,
    modelId,
  );
  const session: Session = { ...mapSummary(created, mode), mode };
  // carry over local messages? No — a fresh server transcript starts clean;
  // the run's user message is persisted by the run itself. Drop the empty local
  // placeholder we just upgraded so the mode's list doesn't keep a phantom "New
  // chat" beside the real conversation (a placeholder has no messages; a seed
  // chat with demo history is left in place).
  const dropId = current && current.messages.length === 0 ? localId : null;
  useKontur.setState((s) => ({
    sessions: [session, ...s.sessions.filter((x) => x.id !== session.id && x.id !== dropId)],
    currentSessionId: session.id,
    ui: { ...s.ui, activeSessionByMode: { ...s.ui.activeSessionByMode, [mode]: session.id } },
  }));
  return session.id;
}

/* ---------------- workspace files ---------------- */

const serverPaths = new Set<string>();

const langOf = (path: string): DemoFile["language"] => {
  if (path.endsWith(".cs") || path.endsWith(".csproj") || path.endsWith(".slnx")) return "csharp";
  if (path.endsWith(".json")) return "json";
  if (path.endsWith(".md")) return "markdown";
  if (path.endsWith(".xml") || path.endsWith(".xaml") || path.endsWith(".csproj")) return "xml";
  return "text";
};

export async function syncWorkspaceFiles(): Promise<void> {
  if (!serverMode) return;
  const status = await workspace.status();
  // single source of truth for the open project (item 5) — every surface reads
  // store.workspaceRoot instead of fetching workspace.status() on its own.
  useKontur.setState({ workspaceRoot: status.isOpen && status.root ? status.root : null });
  if (!status.isOpen) return;
  const listing = await workspace.list(".", true).catch(() => null);
  if (!listing) return;
  const files: DemoFile[] = listing.entries
    .filter((e) => !e.isDirectory)
    .slice(0, 2000)
    .map((e) => ({ path: e.path.replace(/\//g, "/"), language: langOf(e.path), content: "", modified: false }));
  serverPaths.clear();
  for (const f of files) serverPaths.add(f.path);
  useKontur.setState({ files });
}

/** Shared "open a workspace folder" flow (start screen, header chip, palette).
 *  Opens the OS folder picker, opens the folder on the sidecar, updates the
 *  shared workspaceRoot, then syncs files + re-indexes the canvas. Returns the
 *  opened root, or null if cancelled / no sidecar. Throws only when the sidecar
 *  `workspace.open` call fails, so callers can surface the message in a toast. */
export async function openWorkspaceDialog(): Promise<string | null> {
  const bridge = typeof window !== "undefined" ? window.kontur : undefined;
  if (!serverMode || !bridge) return null;
  const dir = await bridge.pickFolder();
  if (!dir) return null;
  const res = await workspace.open(dir);
  useKontur.setState({ workspaceRoot: res.root });
  await syncWorkspaceFiles().catch(() => undefined);
  await reindexCanvas().catch(() => undefined);
  return res.root;
}

export async function readServerFile(path: string): Promise<string | null> {
  try {
    const res = await workspace.read(path, 1);
    return res.content;
  } catch {
    return null;
  }
}

/** Re-read every open code tab from the server (after agent edits). */
export async function refreshOpenTabs(): Promise<void> {
  if (!serverMode) return;
  const tabs = S().codeTabs;
  for (const path of tabs) {
    if (!serverPaths.has(path)) continue;
    const content = await readServerFile(path);
    if (content == null) continue;
    const current = S().files.find((f) => f.path === path);
    if (current && current.content !== content) {
      useKontur.setState((st) => ({
        files: st.files.map((f) => (f.path === path ? { ...f, content, modified: false } : f)),
      }));
    }
  }
}

/* ---------------- canvas ⇄ graph ---------------- */

const NODE_KINDS: NodeKind[] = [
  "file", "folder", "module", "service", "interface", "data", "view", "test",
  "plan", "task", "agent", "model", "external", "note",
];

function toNodeKind(raw: string): NodeKind {
  const lower = raw.toLowerCase();
  return (NODE_KINDS as string[]).includes(lower) ? (lower as NodeKind) : "note";
}

const EDGE_KINDS: EdgeKind[] = ["contains", "depends", "calls", "implements", "relates", "plans"];

function toEdgeKind(raw: string): EdgeKind {
  const lower = raw.toLowerCase();
  return (EDGE_KINDS as string[]).includes(lower) ? (lower as EdgeKind) : "relates";
}

export async function syncCanvas(): Promise<void> {
  if (!serverMode) return;
  const snap = await graph.snapshot().catch(() => null);
  if (!snap) return;
  applyGraphSnapshot(snap);
}

/**
 * Re-pull the graph after the agent drew a plan (submit_plan) and frame it.
 * The sidecar's ServerCanvasPlanSink mutates the shared graph and persists it
 * before the tool-finished event fires, so GET /graph already carries the plan
 * by the time this runs — no new wire event is needed. The fit is a no-op unless
 * the canvas is mounted, so calling it from any surface is safe.
 */
export async function refreshCanvasAfterPlan(): Promise<void> {
  if (!serverMode) return;
  await syncCanvas();
  requestCanvasFit();
}

/**
 * Rebuild the graph by walking the open workspace folder, then show it.
 * The server does not auto-index on open, so `graph.snapshot()` is empty for a
 * freshly opened project — this is what actually fills the canvas.
 */
export async function reindexCanvas(): Promise<void> {
  if (!serverMode) return;
  const res = await graph.reindex().catch(() => null);
  if (res) {
    applyGraphSnapshot(res.snapshot);
    requestCanvasFit();
    return;
  }
  // reindex unavailable (older sidecar) — fall back to whatever snapshot exists
  await syncCanvas();
  requestCanvasFit();
}

function applyGraphSnapshot(snap: ServerGraphSnapshot): void {
  const nodes: CanvasNode[] = snap.nodes.map((n) => ({
    id: n.id,
    kind: toNodeKind(n.kind),
    title: n.title,
    meta: n.subtitle ?? n.path ?? undefined,
    x: n.x,
    y: n.y,
    w: n.width,
    h: n.height,
    path: n.path ?? undefined,
    note: n.detail ?? undefined,
  }));
  const edges: CanvasEdge[] = snap.edges.map((e) => ({
    id: e.id,
    from: e.sourceId,
    to: e.targetId,
    kind: toEdgeKind(e.kind),
    note: e.label ?? undefined,
  }));
  useKontur.setState((st) => ({
    canvas: {
      ...st.canvas,
      nodes,
      edges,
      selectedIds: [],
      primaryId: null,
      selectedEdgeId: null,
      selectedEdgeIds: [],
    },
  }));
}

export function pushGraphChanges(title: string, changes: GraphChangeInput[]): void {
  if (!serverMode || changes.length === 0) return;
  void graph.apply(title, changes).catch(() => undefined);
}

/** Push a canvas deletion (nodes + incident edges) as one server change set. */
export function pushCanvasDeletion(nodeIds: string[], edgeIds: string[]): void {
  if (!serverMode) return;
  const changes: GraphChangeInput[] = [
    ...edgeIds.map((id): GraphChangeInput => ({ kind: "remove-edge", edgeId: id })),
    ...nodeIds.map((nodeId): GraphChangeInput => ({ kind: "remove-node", nodeId })),
  ];
  pushGraphChanges("Delete selection", changes);
}

/** Push current node positions (auto-layout) as move changes. */
export function pushCanvasMoves(): void {
  if (!serverMode) return;
  const { nodes } = S().canvas;
  pushGraphChanges(
    "Arrange canvas",
    nodes.map((n): GraphChangeInput => ({ kind: "move-node", nodeId: n.id, x: n.x, y: n.y })),
  );
}

function installCanvasOverrides(prev: ReturnType<typeof useKontur.getState>) {
  useKontur.setState({
    openCodeTab: (path: string) => {
      prev.openCodeTab(path);
      if (!serverMode || !serverPaths.has(path)) return;
      const file = S().files.find((f) => f.path === path);
      if (file && file.content) return;
      void readServerFile(path).then((content) => {
        if (content == null) return;
        useKontur.setState((st) => ({
          files: st.files.map((f) => (f.path === path ? { ...f, content } : f)),
        }));
      });
    },
    setFileContent: (path: string, content: string) => {
      prev.setFileContent(path, content);
      if (!serverMode || !serverPaths.has(path)) return;
      // User edit in the Code surface: write straight through (no approval —
      // the person typed it themselves). Agent edits arrive via tool runs.
      S().markSave(path, "saving");
      void workspace
        .write(path, content)
        .then(() => S().markSave(path, "saved"))
        .catch(() => {
          S().markSave(path, "error");
          S().pushToast("Could not save the file", path, "destructive");
        });
    },
    addNodes: (nodes) => {
      prev.addNodes(nodes);
      if (!serverMode) return;
      pushGraphChanges(
        "Add nodes",
        nodes.map((n): GraphChangeInput => ({
          kind: "add-node",
          node: {
            id: n.id,
            kind: n.kind,
            title: n.title,
            subtitle: n.meta ?? undefined,
            detail: n.note ?? undefined,
            path: n.path ?? undefined,
            x: n.x,
            y: n.y,
            width: n.w,
            height: n.h,
          },
        })),
      );
    },
    addEdges: (edges) => {
      prev.addEdges(edges);
      if (!serverMode) return;
      pushGraphChanges(
        "Add connections",
        edges.map((e): GraphChangeInput => ({
          kind: "add-edge",
          edge: { id: e.id, sourceId: e.from, targetId: e.to, kind: e.kind, label: e.note ?? undefined },
        })),
      );
    },
    moveNode: (id: string, x: number, y: number, commit?: boolean) => {
      prev.moveNode(id, x, y, commit);
      if (!serverMode || !commit) return;
      pushGraphChanges("Move node", [{ kind: "move-node", nodeId: id, x, y }]);
    },
    setNodeNote: (id: string, note: string | undefined) => {
      prev.setNodeNote(id, note);
      if (!serverMode) return;
      pushGraphChanges("Edit note", [{ kind: "update-node", nodeId: id, detail: note ?? null } as GraphChangeInput]);
    },
    setEdgeNote: (id: string, note: string | undefined) => {
      prev.setEdgeNote(id, note);
      if (!serverMode) return;
      pushGraphChanges("Edit connection label", [{ kind: "update-edge", edgeId: id, label: note ?? null } as GraphChangeInput]);
    },
    updateEdgeKind: (id: string, kind, opts) => {
      prev.updateEdgeKind(id, kind, opts);
      if (!serverMode) return;
      pushGraphChanges("Change connection kind", [{ kind: "update-edge", edgeId: id, edgeKind: kind }]);
    },
    updateEdgeEndpoints: (id: string, from: string, to: string) => {
      const before = S().canvas.edges.find((e) => e.id === id);
      prev.updateEdgeEndpoints(id, from, to);
      if (!serverMode) return;
      pushGraphChanges("Reconnect edge", [
        { kind: "remove-edge", edgeId: id },
        {
          kind: "add-edge",
          edge: {
            id,
            sourceId: from,
            targetId: to,
            kind: before?.kind ?? "relates",
            label: before?.note ?? undefined,
          },
        },
      ]);
    },
    undoCanvas: () => {
      if (!serverMode) {
        prev.undoCanvas();
        return;
      }
      void (async () => {
        try {
          await graph.undo();
        } catch {
          /* nothing to undo — stay quiet */
        }
        await syncCanvas().catch(() => undefined);
      })();
    },
    redoCanvas: () => {
      if (!serverMode) {
        prev.redoCanvas();
        return;
      }
      void (async () => {
        try {
          await graph.redo();
        } catch {
          /* nothing to redo — stay quiet */
        }
        await syncCanvas().catch(() => undefined);
      })();
    },
  });
}

/* ---------------- misc server ops ---------------- */

export async function exportCurrentSession(format: "markdown" | "json" | "text"): Promise<string | null> {
  const id = S().currentSessionId;
  if (!isServerId(id)) return null;
  return exportApi.conversation(id, format).catch(() => null);
}

export { api, getBackendUrl };
