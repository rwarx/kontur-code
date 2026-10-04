"use client";

import { create } from "zustand";
import { persist, createJSONStorage } from "zustand/middleware";
import type {
  AgentMode,
  Approval,
  ApprovalDecision,
  CanvasEdge,
  CanvasNode,
  CanvasState,
  Checkpoint,
  ContextFile,
  DemoFile,
  DraftAttachment,
  EdgeKind,
  Goal,
  Locale,
  Message,
  MessageBlock,
  Provider,
  RunnerState,
  Session,
  Surface,
  ThemeMode,
  ToolCall,
  ToolRisk,
  TrajectoryEvent,
  WorkMethod,
  WorkMode,
} from "./types";
import {
  DEFAULT_MODEL_ID,
  DEMO_FILES,
  INITIAL_EDGES,
  INITIAL_NODES,
  PROVIDERS,
  SEED_SESSIONS,
  SKILLS,
} from "./data";
import { modeDefaultSurface, surfaceAllowedInMode } from "./modes";

/* approval waiters (module-scoped, outside the store) */
type ApprovalResolution = { decision: ApprovalDecision; reason?: string };
type ApprovalWaiter = {
  resolve: (r: ApprovalResolution) => void;
  reject: (reason: Error) => void;
};
const approvalWaiters = new Map<string, ApprovalWaiter>();

/**
 * Waits for the person to answer one approval question.
 *
 * Rejects rather than hanging forever if the same question is registered twice, and is rejected
 * outright once the run has finished. The previous version resolved through a single
 * `store.approval` slot: two approvals arriving close together overwrote each other, the first
 * waiter's resolve was never called, and since the promise could not reject either, `await` on it
 * blocked the frame handler, which blocked the run. A run that hangs with a spinner and no error is
 * the worst of the available failure modes, so every path out of here terminates.
 */
export function waitForApproval(id: string): Promise<ApprovalResolution> {
  return new Promise<ApprovalResolution>((resolve, reject) => {
    const existing = approvalWaiters.get(id);

    if (existing) {
      existing.reject(new Error(`Approval ${id} was registered twice.`));
      approvalWaiters.delete(id);
    }

    approvalWaiters.set(id, { resolve, reject });
  });
}

/**
 * Fails every question still open, and why.
 *
 * Called when the run ends. Anything left waiting would otherwise wait for an answer that can no
 * longer arrive, because the store's single approval slot has been cleared.
 */
export function failPendingApprovals(reason: string): void {
  if (approvalWaiters.size === 0) return;

  const waiters = [...approvalWaiters.values()];
  approvalWaiters.clear();

  for (const waiter of waiters) {
    waiter.reject(new Error(reason));
  }
}

export interface ToastPayload {
  id: number;
  title: string;
  description?: string;
  variant?: "default" | "destructive";
}

/**
 * Where one file's editor state stands relative to the backend.
 *
 * `unsaved` exists because writes are debounced: without a state between the
 * keystroke and the request there is a window in which the file has been edited
 * and nothing says so.
 */
export type SaveStatus = "unsaved" | "saving" | "saved" | "error";

/**
 * Persisted-shape version. Bump when `partialize` changes shape, so an older cache is rebuilt from
 * defaults instead of being read as if it were current.
 */
const PERSIST_VERSION = 2;

/**
 * Ceilings that keep a long session from growing the cache or memory without limit.
 *
 * Not correctness limits — the backend holds the full history, and the UI paginates by scrolling —
 * so the numbers are about when a person would notice. 2000 messages is roughly a hundred long
 * exchanges; 500 events is well past a screen of scrollback.
 */
const MAX_PERSISTED_MESSAGES = 2000;
const MAX_EVENTS = 500;
const MAX_MESSAGES_PER_SESSION = 2000;

interface UiPrefs {
  theme: ThemeMode;
  locale: Locale;
  sidebarCollapsed: boolean;
  sidebarWidth: number;
  /** interface zoom (app scale). 1 = 100%. Applied natively via the Electron
   *  `view.setZoom` bridge, with a CSS `zoom` fallback in the browser build. */
  appZoom: number;
  contextPanelOpen: boolean;
  /** top-level Chat / Cowork / Code switch */
  workMode: WorkMode;
  /** false until the user picks a mode in the first-run onboarding screen */
  modeChosen: boolean;
  /** autonomy while coworking: plan / ask / auto / full */
  workMethod: WorkMethod;
  /** engine mode consumed by the run pipeline — kept in sync with
   *  workMode + workMethod so scenario.ts / agent-live.ts stay unchanged */
  agentMode: AgentMode;
  /** last-open chat per work mode, so switching Chat/Cowork/Code returns you to
   *  the conversation you were in for that mode rather than a shared one. Keyed
   *  by WorkMode; entries are pruned on delete and re-pointed on create/select. */
  activeSessionByMode: Partial<Record<WorkMode, string>>;
  selectedModelId: string;
  showGrid: boolean;
  enterSend: boolean;
  renderMarkdown: boolean;
  autoscroll: boolean;
  maxSteps: number;
  /** LLM ghost-text inline completion in the code editor (Tier 1 #7).
   *  Off by default — opt-in per session to avoid surprise token cost. */
  ghostText: boolean;
  /** which auto-selectable skills are active (id → on). Seeded from SKILLS
   *  defaults; the agent only draws on enabled skills when picking per-run. */
  skillsEnabled: Record<string, boolean>;
}

/* Derive the engine AgentMode from the user-facing controls.
   Chat = plain conversation (no tools); Cowork/Code = agentic. */
export function engineModeFor(workMode: WorkMode, workMethod: WorkMethod): AgentMode {
  if (workMode === "chat") return "off";
  return workMethod === "plan" ? "plan" : "build";
}

/* Client-side auto-approval policy for the autonomy selector.
   Returns the decision to apply automatically, or null to prompt the user.
   NB: "full" auto-approves execute here, but the backend still enforces its
   own Execute gate (AgentSettings.AllowCommands, default OFF). */
export function autoApprovalDecision(
  workMethod: WorkMethod,
  risk: ToolRisk,
): ApprovalDecision | null {
  if (workMethod === "auto") return risk === "execute" ? null : "allowed";
  if (workMethod === "full") return "allowed";
  return null;
}

/* ---------- app scale (interface zoom) ----------
   The desktop shell scales crisply via Chromium's own zoom
   (webContents.setZoomFactor, reached through the preload `view` bridge). The
   plain browser build has no bridge, so it falls back to CSS `zoom` on <html>
   — same visual result, so the control is never a dead button. Bounded to a
   readable range. */
export const APP_ZOOM_MIN = 0.6;
export const APP_ZOOM_MAX = 2;
export const APP_ZOOM_PRESETS = [0.8, 0.9, 1, 1.1, 1.25, 1.5] as const;

export function clampAppZoom(factor: number): number {
  const n = Number(factor);
  if (!Number.isFinite(n)) return 1;
  return Math.min(APP_ZOOM_MAX, Math.max(APP_ZOOM_MIN, n));
}

export function applyAppZoom(factor: number): void {
  const f = clampAppZoom(factor);
  if (typeof window === "undefined") return;
  const view = window.kontur?.view;
  if (view?.setZoom) {
    void view.setZoom(f);
    /* drop any CSS fallback a prior browser session may have left on <html> */
    if (typeof document !== "undefined") document.documentElement.style.zoom = "";
  } else if (typeof document !== "undefined") {
    document.documentElement.style.zoom = f === 1 ? "" : String(f);
  }
}

interface KonturState {
  hydrated: boolean;

  /* ui */
  surface: Surface;
  history: Surface[];
  historyIndex: number;
  paletteOpen: boolean;
  checkpointJumpOpen: boolean;
  cheatsheetOpen: boolean;
  projectExportOpen: boolean;
  /** provider/model management modal (opencode-style) */
  providerStudioOpen: boolean;
  providerStudioTab: "manage" | "connect";
  /** transient: the canvas has a local overlay/gesture that owns Escape
   *  (kind picker, search, connect/reconnect drag) — the global chain
   *  must not treat that Escape as "stop generation" */
  canvasOverlayActive: boolean;
  draft: string;
  draftAttachments: DraftAttachment[];
  recording: boolean;
  sidebarOpenMobile: boolean;
  ui: UiPrefs;
  toasts: ToastPayload[];

  /* data */
  sessions: Session[];
  currentSessionId: string;
  approval: Approval | null;
  allowForRunTools: string[];
  goals: Goal[];
  checkpoints: Checkpoint[];
  contextFiles: ContextFile[];
  files: DemoFile[];
  canvas: CanvasState;
  canvasUndo: { nodes: CanvasNode[]; edges: CanvasEdge[] }[];
  canvasRedo: { nodes: CanvasNode[]; edges: CanvasEdge[] }[];
  codeTabs: string[];
  activeCodeTab: string | null;
  /** transient per-file save state for the Code editor (server-mode write-through) */
  saveStatus: Record<string, SaveStatus>;
  /** checkpoint id whose diff review is active in the Code surface (transient) */
  codeReviewCheckpointId: string | null;
  providers: Provider[];
  runner: RunnerState;
  indexing: boolean;
  /** absolute path of the open sidecar workspace (null in demo / none open).
   *  transient (not persisted) — kept in sync by syncWorkspaceFiles and the
   *  shared openWorkspaceDialog() so every surface reads one source of truth. */
  workspaceRoot: string | null;

  /* ui actions */
  setSurface: (s: Surface, opts?: { noHistory?: boolean }) => void;
  goHistory: (delta: number) => void;
  setPaletteOpen: (open: boolean) => void;
  setCheckpointJumpOpen: (open: boolean) => void;
  setCheatsheetOpen: (open: boolean) => void;
  setProjectExportOpen: (open: boolean) => void;
  setProviderStudioOpen: (open: boolean, tab?: "manage" | "connect") => void;
  setCanvasOverlayActive: (active: boolean) => void;
  setCodeReviewCheckpoint: (id: string | null) => void;
  togglePalette: () => void;
  toggleSidebar: () => void;
  setSidebarWidth: (w: number) => void;
  setSidebarOpenMobile: (open: boolean) => void;
  toggleContextPanel: () => void;
  setUi: (patch: Partial<UiPrefs>) => void;
  /** set the Chat/Cowork/Code switch, keeping agentMode in sync */
  setWorkMode: (mode: WorkMode) => void;
  /** set the autonomy selector, keeping agentMode in sync */
  setWorkMethod: (method: WorkMethod) => void;
  setDraft: (draft: string) => void;
  addDraftAttachment: (attachment: DraftAttachment) => void;
  removeDraftAttachment: (id: string) => void;
  clearDraftAttachments: () => void;
  setRecording: (recording: boolean) => void;
  setRunner: (patch: Partial<RunnerState>) => void;
  setIndexing: (indexing: boolean) => void;
  setWorkspaceRoot: (root: string | null) => void;
  pushToast: (title: string, description?: string, variant?: "default" | "destructive") => void;
  dismissToast: (id: number) => void;

  /* session actions */
  newSession: () => string;
  /** restore a session + goals from an imported bundle (ids remapped on collision) */
  importSession: (session: Session, goals: Goal[]) => string;
  selectSession: (id: string) => void;
  renameSession: (id: string, title: string) => void;
  deleteSession: (id: string) => void;
  setSessionPinned: (id: string, pinned: boolean) => void;
  forkSession: (id: string) => string;
  currentSession: () => Session | undefined;
  addMessage: (msg: Message) => void;
  deleteMessage: (id: string) => void;
  updateMessage: (id: string, patch: Partial<Message>) => void;
  appendToMessage: (id: string, text: string) => void;
  /** Buffered sibling of appendToMessage: coalesces live-stream deltas into
   *  one store write per animation frame. Flush with flushMessageBuffer. */
  appendToMessageBuffered: (id: string, text: string) => void;
  /** Apply any buffered stream deltas synchronously (call before reading the
   *  message, finalizing a stream, or handling a structural frame). */
  flushMessageBuffer: () => void;
  addToolCall: (msgId: string, call: ToolCall) => void;
  updateToolCall: (msgId: string, callId: string, patch: Partial<ToolCall>) => void;
  addBlock: (msgId: string, block: MessageBlock) => void;
  updateBlock: (msgId: string, index: number, patch: Record<string, unknown>) => void;
  addEvent: (ev: TrajectoryEvent) => void;
  setContextTokens: (tokens: number) => void;
  compactSession: () => void;

  /* goals */
  addGoal: (goal: Goal) => void;
  updateGoal: (id: string, patch: Partial<Goal>) => void;
  updateGoalCriteria: (goalId: string, itemId: string, done: boolean) => void;

  /* approvals */
  setApproval: (approval: Approval | null) => void;
  resolveApproval: (decision: ApprovalDecision, reason?: string) => void;

  /* canvas */
  setCanvas: (patch: Partial<CanvasState>) => void;
  setViewport: (zoom: number, panX: number, panY: number) => void;
  zoomAt: (factor: number, screenX: number, screenY: number, viewW: number, viewH: number) => void;
  panBy: (dx: number, dy: number) => void;
  selectNode: (id: string, additive?: boolean) => void;
  clearSelection: () => void;
  selectEdge: (id: string | null) => void;
  /** bulk edge selection — sets the whole set (outline marquee / shift-click);
   *  the first member becomes the single primary `selectedEdgeId` */
  setSelectedEdgeIds: (ids: string[]) => void;
  moveNode: (id: string, x: number, y: number, commit?: boolean) => void;
  setNodeNote: (id: string, note: string | undefined) => void;
  /** set / remove a connection label (undoable, mirrors setNodeNote) */
  setEdgeNote: (id: string, note: string | undefined) => void;
  addNodes: (nodes: CanvasNode[]) => void;
  addEdges: (edges: CanvasEdge[]) => void;
  /** change an edge's kind; undoable when edited deliberately (inspector),
   *  silent for the post-create kind popover (creation itself is undoable) */
  updateEdgeKind: (id: string, kind: EdgeKind, opts?: { undoable?: boolean }) => void;
  /** re-point an edge's from/to endpoint (reconnect drag) — snapshot for
   *  undo is pushed by the caller (gesture commits once) */
  updateEdgeEndpoints: (id: string, from: string, to: string) => void;
  pushUndoSnapshot: () => void;
  undoCanvas: () => void;
  redoCanvas: () => void;

  /* files & code */
  setFileContent: (path: string, content: string) => void;
  setFileModified: (path: string, modified: boolean) => void;
  openCodeTab: (path: string) => void;
  closeCodeTab: (path: string) => void;
  setActiveCodeTab: (path: string) => void;
  markSave: (path: string, status: SaveStatus) => void;
  /**
   * Writes every file with a queued edit, and resolves when they have all landed.
   *
   * Installed by `sync.ts`; a no-op in demo mode, where nothing is written anywhere. The editor's
   * writes are debounced, so this is what stands between a keystroke and a lost edit whenever the
   * renderer is about to stop being able to send it.
   */
  flushPendingWrites: () => Promise<void>;

  /* context */
  setContextFiles: (files: ContextFile[]) => void;
  addContextFile: (file: ContextFile) => void;
  removeContextFile: (path: string) => void;
  pinContextFile: (path: string) => void;

  /* checkpoints */
  addCheckpoint: (cp: Checkpoint) => void;
  restoreCheckpoint: (id: string) => void;

  /* providers */
  setProviderState: (id: string, patch: Partial<Provider>) => void;
}

const uid = (() => {
  let counter = 0;
  return (prefix: string) => `${prefix}-${Date.now().toString(36)}-${(counter++).toString(36)}`;
})();

export const newId = uid;

const initialCanvas: CanvasState = {
  nodes: INITIAL_NODES,
  edges: INITIAL_EDGES,
  selectedIds: [],
  primaryId: null,
  selectedEdgeId: null,
  selectedEdgeIds: [],
  zoom: 0.85,
  panX: 0,
  panY: 0,
  showGrid: true,
  tool: "select",
  hiddenEdgeKinds: [],
};

const defaultUi: UiPrefs = {
  theme: "dark",
  locale: "ru",
  sidebarCollapsed: false,
  sidebarWidth: 248,
  appZoom: 1,
  contextPanelOpen: true,
  workMode: "chat",
  modeChosen: false,
  workMethod: "ask",
  agentMode: "off",
  activeSessionByMode: {},
  selectedModelId: DEFAULT_MODEL_ID,
  showGrid: true,
  enterSend: true,
  renderMarkdown: true,
  autoscroll: true,
  maxSteps: 40,
  ghostText: false,
  skillsEnabled: Object.fromEntries(SKILLS.map((s) => [s.id, s.enabled])),
};

function sortSessions(sessions: Session[]): Session[] {
  return [...sessions].sort((a, b) => {
    if (!!b.pinned !== !!a.pinned) return b.pinned ? 1 : -1;
    return b.updatedAt - a.updatedAt;
  });
}

/* Chats are partitioned by work mode. A session with no explicit mode is a
   legacy/pre-feature conversation and reads as "chat" so nothing is orphaned. */
export function sessionModeOf(s: { mode?: WorkMode }): WorkMode {
  return s.mode ?? "chat";
}

/* The chat to open for a mode: the remembered one if it still exists and still
   belongs to the mode, else the most-recent chat in that mode, else undefined
   (caller mints a fresh placeholder). */
function pickSessionForMode(
  sessions: Session[],
  mode: WorkMode,
  remembered?: string,
): string | undefined {
  if (remembered) {
    const r = sessions.find((s) => s.id === remembered);
    if (r && sessionModeOf(r) === mode) return remembered;
  }
  const inMode = sessions.filter((s) => sessionModeOf(s) === mode);
  return inMode.length ? sortSessions(inMode)[0].id : undefined;
}

/* A fresh, empty local chat stamped with its mode. Stays local (offline id)
   until the first message upgrades it to a server conversation. */
function makePlaceholder(mode: WorkMode, modelId: string): Session {
  return {
    id: uid("s"),
    title: "New chat",
    createdAt: Date.now(),
    updatedAt: Date.now(),
    preview: "",
    messages: [],
    events: [],
    goalIds: [],
    modelId,
    mode,
  };
}

/* ------------------------------------------------------------------ *
 * Streaming coalescer
 *
 * Live NDJSON deltas can arrive faster than the browser paints. The read
 * loop (`backend.streamNdjson`) resolves each `reader.read()` on a microtask
 * and calls `onFrame` synchronously, so appending to the store on every delta
 * never yields a frame to the compositor — text accumulates in state but the
 * screen only repaints in one burst (e.g. when a keypress like Space forces a
 * paint). We coalesce appends into a single store write per animation frame:
 * smooth streaming, far fewer renders, far less persist churn. No tokens are
 * dropped — every stream-terminal path flushes synchronously first.
 * ------------------------------------------------------------------ */
const pendingAppends = new Map<string, string>();
let flushHandle: number | null = null;

function applyPendingAppends(): void {
  if (pendingAppends.size === 0) return;
  // Swap the buffer out before applying, so a re-entrant append queued by a
  // store subscriber re-buffers for the next frame instead of being dropped.
  const batch = new Map(pendingAppends);
  pendingAppends.clear();
  useKontur.setState((state) => {
    const { sessions, currentSessionId } = state;
    return {
      sessions: sessions.map((s) =>
        s.id === currentSessionId
          ? {
              ...s,
              messages: s.messages.map((m) =>
                batch.has(m.id) ? { ...m, content: m.content + batch.get(m.id)! } : m,
              ),
            }
          : s,
      ),
    } as Partial<KonturState>;
  });
}

function scheduleAppendFlush(): void {
  if (flushHandle !== null) return;
  if (typeof requestAnimationFrame === "undefined") {
    // Headless / SSR / tests: no compositor to wait for — apply immediately.
    applyPendingAppends();
    return;
  }
  flushHandle = requestAnimationFrame(() => {
    flushHandle = null;
    applyPendingAppends();
  });
}

export const useKontur = create<KonturState>()(
  persist(
    (set, get) => ({
      hydrated: false,

      surface: "chat",
      history: ["chat"],
      historyIndex: 0,
      paletteOpen: false,
      checkpointJumpOpen: false,
      cheatsheetOpen: false,
      projectExportOpen: false,
      providerStudioOpen: false,
      providerStudioTab: "manage",
      canvasOverlayActive: false,
      draft: "",
      draftAttachments: [],
      recording: false,
      sidebarOpenMobile: false,
      ui: defaultUi,
      toasts: [],

      sessions: [
        ...SEED_SESSIONS,
        {
          id: "current",
          title: "New chat",
          createdAt: Date.now(),
          updatedAt: Date.now(),
          preview: "",
          messages: [],
          events: [],
          goalIds: [],
          modelId: DEFAULT_MODEL_ID,
          mode: "chat",
        },
      ],
      currentSessionId: "current",
      approval: null,
      allowForRunTools: [],
      goals: [],
      checkpoints: [],
      contextFiles: [],
      files: DEMO_FILES.map((f) => ({ ...f })),
      canvas: initialCanvas,
      canvasUndo: [],
      canvasRedo: [],
      codeTabs: [],
      activeCodeTab: null,
      saveStatus: {},
      codeReviewCheckpointId: null,
      providers: PROVIDERS.map((p) => ({ ...p, models: p.models.map((mm) => ({ ...mm })) })),
      runner: { status: "idle" },
      indexing: false,
      workspaceRoot: null,

      /* ---------- ui ---------- */
      setSurface: (s, opts) => {
        const { history, historyIndex } = get();
        if (opts?.noHistory || history[historyIndex] === s) {
          set({ surface: s });
          return;
        }
        const trimmed = history.slice(0, historyIndex + 1);
        trimmed.push(s);
        set({ surface: s, history: trimmed.slice(-32), historyIndex: Math.min(trimmed.length - 1, 31) });
      },
      goHistory: (delta) => {
        const { history, historyIndex } = get();
        const next = historyIndex + delta;
        if (next < 0 || next >= history.length) return;
        set({ historyIndex: next, surface: history[next] });
      },
      setPaletteOpen: (open) => set({ paletteOpen: open }),
      togglePalette: () => set({ paletteOpen: !get().paletteOpen }),
      setCheckpointJumpOpen: (open) => set({ checkpointJumpOpen: open }),
      setCheatsheetOpen: (open) => set({ cheatsheetOpen: open }),
      setProjectExportOpen: (open) => set({ projectExportOpen: open }),
      setProviderStudioOpen: (open, tab) =>
        set((s) => ({
          providerStudioOpen: open,
          providerStudioTab: tab ?? s.providerStudioTab,
        })),
      setCanvasOverlayActive: (active) => set({ canvasOverlayActive: active }),
      setCodeReviewCheckpoint: (id) => set({ codeReviewCheckpointId: id }),
      toggleSidebar: () => set({ ui: { ...get().ui, sidebarCollapsed: !get().ui.sidebarCollapsed } }),
      setSidebarWidth: (w) =>
        set({ ui: { ...get().ui, sidebarWidth: Math.max(200, Math.min(420, w)) } }),
      setSidebarOpenMobile: (open) => set({ sidebarOpenMobile: open }),
      toggleContextPanel: () =>
        set({ ui: { ...get().ui, contextPanelOpen: !get().ui.contextPanelOpen } }),
      setUi: (patch) => set({ ui: { ...get().ui, ...patch } }),
      setWorkMode: (mode) => {
        const { ui, surface, sessions, currentSessionId } = get();
        if (mode === ui.workMode) return;
        /* keep the current surface if it still belongs to the new mode,
           otherwise land on the mode's home surface (chat / chat / code). */
        const nextSurface = surfaceAllowedInMode(surface, mode)
          ? surface
          : modeDefaultSurface[mode];
        /* remember the chat we're leaving under its own mode, so coming back to
           that mode returns here rather than to a shared conversation. */
        const remembered: Partial<Record<WorkMode, string>> = { ...ui.activeSessionByMode };
        const leaving = sessions.find((s) => s.id === currentSessionId);
        if (leaving) remembered[sessionModeOf(leaving)] = leaving.id;
        /* the chat to open for the new mode: the remembered one, else the most
           recent in that mode, else a fresh placeholder minted just for it. */
        let nextSessions = sessions;
        let targetId = pickSessionForMode(sessions, mode, remembered[mode]);
        if (!targetId) {
          const placeholder = makePlaceholder(mode, ui.selectedModelId);
          nextSessions = sortSessions([placeholder, ...sessions]);
          targetId = placeholder.id;
        }
        remembered[mode] = targetId;
        set({
          sessions: nextSessions,
          currentSessionId: targetId,
          ui: {
            ...ui,
            workMode: mode,
            agentMode: engineModeFor(mode, ui.workMethod),
            activeSessionByMode: remembered,
          },
          surface: nextSurface,
          approval: null,
        });
      },
      setWorkMethod: (method) => {
        const ui = get().ui;
        set({ ui: { ...ui, workMethod: method, agentMode: engineModeFor(ui.workMode, method) } });
      },
      setDraft: (draft) => set({ draft }),
      addDraftAttachment: (attachment) =>
        set((s) => ({
          draftAttachments: s.draftAttachments.some((a) => a.id === attachment.id)
            ? s.draftAttachments
            : [...s.draftAttachments, attachment],
        })),
      removeDraftAttachment: (id) =>
        set((s) => ({ draftAttachments: s.draftAttachments.filter((a) => a.id !== id) })),
      clearDraftAttachments: () => set({ draftAttachments: [] }),
      setRecording: (recording) => set({ recording }),
      setRunner: (patch) => set({ runner: { ...get().runner, ...patch } }),
      setIndexing: (indexing) => set({ indexing }),
      setWorkspaceRoot: (root) => set({ workspaceRoot: root }),
      pushToast: (title, description, variant) =>
        set({ toasts: [...get().toasts, { id: Date.now() + Math.random(), title, description, variant }] }),
      dismissToast: (id) => set({ toasts: get().toasts.filter((t) => t.id !== id) }),

      /* ---------- sessions ---------- */
      newSession: () => {
        const id = uid("s");
        const { ui } = get();
        const mode = ui.workMode;
        const session: Session = {
          id,
          title: "New chat",
          createdAt: Date.now(),
          updatedAt: Date.now(),
          preview: "",
          messages: [],
          events: [],
          goalIds: [],
          modelId: ui.selectedModelId,
          mode,
        };
        set({
          sessions: sortSessions([session, ...get().sessions]),
          currentSessionId: id,
          surface: "chat",
          ui: { ...ui, activeSessionByMode: { ...ui.activeSessionByMode, [mode]: id } },
        });
        return id;
      },
      importSession: (session, goals) => {
        const { sessions, goals: existingGoals, ui } = get();
        let id = session.id;
        if (sessions.some((s) => s.id === id)) id = uid("s");
        const remapped = goals.map((g) => {
          let gid = g.id;
          if (existingGoals.some((x) => x.id === gid)) gid = uid("g");
          return { ...g, id: gid, sessionId: id };
        });
        /* an imported chat keeps its own tagged mode; an untagged one joins the
           mode we're in now. Selecting it aligns workMode so the invariant holds. */
        const mode = session.mode ?? ui.workMode;
        const imported: Session = {
          ...session,
          id,
          pinned: false,
          goalIds: remapped.map((g) => g.id),
          mode,
        };
        set({
          sessions: sortSessions([imported, ...sessions]),
          goals: [...existingGoals, ...remapped],
          currentSessionId: id,
          surface: "chat",
          approval: null,
          ui: {
            ...ui,
            workMode: mode,
            agentMode: engineModeFor(mode, ui.workMethod),
            activeSessionByMode: { ...ui.activeSessionByMode, [mode]: id },
          },
        });
        return id;
      },
      selectSession: (id) => {
        const { sessions, ui } = get();
        const target = sessions.find((s) => s.id === id);
        if (!target) {
          set({ currentSessionId: id, surface: "chat", approval: null });
          return;
        }
        /* selecting a chat aligns the work mode to it (so the mode-filtered list
           and the open conversation never disagree) and remembers it for that mode. */
        const mode = sessionModeOf(target);
        const activeSessionByMode = { ...ui.activeSessionByMode, [mode]: id };
        set({
          currentSessionId: id,
          surface: "chat",
          approval: null,
          ui:
            mode === ui.workMode
              ? { ...ui, activeSessionByMode }
              : { ...ui, workMode: mode, agentMode: engineModeFor(mode, ui.workMethod), activeSessionByMode },
        });
      },
      renameSession: (id, title) => {
        set({ sessions: get().sessions.map((s) => (s.id === id ? { ...s, title } : s)) });
      },
      deleteSession: (id) => {
        const { sessions: all, currentSessionId, ui } = get();
        const removed = all.find((s) => s.id === id);
        const sessions = all.filter((s) => s.id !== id);
        const removedMode = removed ? sessionModeOf(removed) : ui.workMode;
        /* drop the deleted id out of every mode's memory */
        const remembered: Partial<Record<WorkMode, string>> = { ...ui.activeSessionByMode };
        for (const k of Object.keys(remembered) as WorkMode[]) {
          if (remembered[k] === id) delete remembered[k];
        }
        let nextSessions = sessions;
        let nextCurrent = currentSessionId;
        if (currentSessionId === id) {
          /* stay inside the mode of the chat we just closed */
          let targetId = pickSessionForMode(sessions, removedMode, remembered[removedMode]);
          if (!targetId) {
            const placeholder = makePlaceholder(removedMode, ui.selectedModelId);
            nextSessions = sortSessions([placeholder, ...sessions]);
            targetId = placeholder.id;
          }
          nextCurrent = targetId;
          remembered[removedMode] = targetId;
        }
        set({ sessions: nextSessions, currentSessionId: nextCurrent, ui: { ...ui, activeSessionByMode: remembered } });
      },
      setSessionPinned: (id, pinned) => {
        set({ sessions: sortSessions(get().sessions.map((s) => (s.id === id ? { ...s, pinned } : s))) });
      },
      forkSession: (id) => {
        const source = get().sessions.find((s) => s.id === id);
        if (!source) return "";
        const newSessionId = uid("s");
        const fork: Session = {
          ...source,
          id: newSessionId,
          title: `${source.title} · fork`,
          createdAt: Date.now(),
          updatedAt: Date.now(),
          pinned: false,
          forkedFrom: source.id,
          preview: source.preview,
          messages: source.messages.map((m) => ({ ...m, id: uid("m"), toolCalls: m.toolCalls.map((t) => ({ ...t })) })),
          events: source.events.map((e) => ({ ...e, id: uid("ev") })),
        };
        /* a fork inherits its source's mode (copied via the spread) and becomes
           the remembered chat for that mode. */
        const forkMode = sessionModeOf(fork);
        const ui = get().ui;
        set({
          sessions: sortSessions([fork, ...get().sessions]),
          currentSessionId: newSessionId,
          ui: { ...ui, activeSessionByMode: { ...ui.activeSessionByMode, [forkMode]: newSessionId } },
        });
        get().addEvent({ id: uid("ev"), ts: Date.now(), kind: "fork", title: `Forked from “${source.title}”` });
        get().pushToast("Session forked", "Context, goal and trajectory were copied to the fork.");
        return newSessionId;
      },
      currentSession: () => get().sessions.find((s) => s.id === get().currentSessionId),

      addMessage: (msg) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: [...s.messages, msg].slice(-MAX_MESSAGES_PER_SESSION),
                  updatedAt: Date.now(),
                  preview: msg.content.slice(0, 90),
                  title: s.title === "New chat" && msg.role === "user" ? msg.content.slice(0, 42).replace(/\s*[—–-]?\s*$/, "") : s.title,
                }
              : s,
          ),
        });
      },
      deleteMessage: (id) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? { ...s, messages: s.messages.filter((m) => m.id !== id) }
              : s,
          ),
        });
      },
      updateMessage: (id, patch) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? { ...s, messages: s.messages.map((m) => (m.id === id ? { ...m, ...patch } : m)) }
              : s,
          ),
        });
      },
      appendToMessage: (id, text) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: s.messages.map((m) =>
                    m.id === id ? { ...m, content: m.content + text } : m,
                  ),
                }
              : s,
          ),
        });
      },
      appendToMessageBuffered: (id, text) => {
        if (!text) return;
        pendingAppends.set(id, (pendingAppends.get(id) ?? "") + text);
        scheduleAppendFlush();
      },
      flushMessageBuffer: () => {
        if (flushHandle !== null) {
          if (typeof cancelAnimationFrame !== "undefined") cancelAnimationFrame(flushHandle);
          flushHandle = null;
        }
        applyPendingAppends();
      },
      addToolCall: (msgId, call) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: s.messages.map((m) =>
                    m.id === msgId ? { ...m, toolCalls: [...m.toolCalls, call] } : m,
                  ),
                }
              : s,
          ),
        });
      },
      updateToolCall: (msgId, callId, patch) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: s.messages.map((m) =>
                    m.id === msgId
                      ? {
                          ...m,
                          toolCalls: m.toolCalls.map((t) => (t.id === callId ? { ...t, ...patch } : t)),
                        }
                      : m,
                  ),
                }
              : s,
          ),
        });
      },
      addBlock: (msgId, block) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: s.messages.map((m) =>
                    m.id === msgId ? { ...m, blocks: [...(m.blocks ?? []), block] } : m,
                  ),
                }
              : s,
          ),
        });
      },
      updateBlock: (msgId, index, patch) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  messages: s.messages.map((m) => {
                    if (m.id !== msgId || !m.blocks) return m;
                    const blocks = [...m.blocks];
                    blocks[index] = { ...blocks[index], ...patch } as MessageBlock;
                    return { ...m, blocks };
                  }),
                }
              : s,
          ),
        });
      },
      addEvent: (ev) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  // Rolling window. Every tool call, approval and streamed delta lands here, and
                  // nothing ever removed any of it.
                  events: [...s.events, ev].slice(-MAX_EVENTS),
                }
              : s,
          ),
        });
      },
      setContextTokens: (tokens) => {
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) => (s.id === currentSessionId ? { ...s, contextTokens: tokens } : s)),
        });
      },
      compactSession: () => {
        const session = get().currentSession();
        if (!session) return;
        const foldable = session.messages.filter((m) => !m.folded && m.role === "user").length;
        if (foldable === 0) {
          get().pushToast("Nothing to compact", "The session is already minimal.");
          return;
        }
        const cutoff = session.messages.length - 6;
        const freed = session.contextTokens
          ? Math.round(session.contextTokens * 0.55)
          : 38000;
        const { sessions, currentSessionId } = get();
        set({
          sessions: sessions.map((s) =>
            s.id === currentSessionId
              ? {
                  ...s,
                  foldedCount: (s.foldedCount ?? 0) + Math.max(0, cutoff),
                  contextTokens: Math.max(4000, (s.contextTokens ?? 94000) - freed),
                  messages:
                    cutoff > 0
                      ? s.messages.map((m, i) => (i < cutoff ? { ...m, folded: true } : m))
                      : s.messages,
                }
              : s,
          ),
        });
        get().addEvent({
          id: uid("ev"),
          ts: Date.now(),
          kind: "compact",
          title: `Folded ${Math.max(0, cutoff)} message(s) and freed ${freed.toLocaleString("en-US")} tokens`,
          status: "done",
        });
        get().pushToast("Session compacted", `Freed ~${freed.toLocaleString("en-US")} tokens.`);
      },

      /* ---------- goals ---------- */
      addGoal: (goal) => {
        const { sessions, currentSessionId, goals } = get();
        set({
          goals: [...goals, goal],
          sessions: sessions.map((s) =>
            s.id === currentSessionId ? { ...s, goalIds: [...s.goalIds, goal.id] } : s,
          ),
        });
      },
      updateGoal: (id, patch) => set({ goals: get().goals.map((g) => (g.id === id ? { ...g, ...patch } : g)) }),
      updateGoalCriteria: (goalId, itemId, done) =>
        set({
          goals: get().goals.map((g) =>
            g.id === goalId
              ? { ...g, criteria: g.criteria.map((c) => (c.id === itemId ? { ...c, done } : c)) }
              : g,
          ),
        }),

      /* ---------- approvals ---------- */
      setApproval: (approval) => {
        set({ approval });
        /* autonomy selector: auto-resolve gates the current work-method allows.
           Deferred a tick so the caller's waitForApproval() is registered first;
           for the live backend this still routes through resolveApproval →
           runs.answerApproval, so the server sees a real decision. */
        if (approval) {
          const decision = autoApprovalDecision(get().ui.workMethod, approval.risk);
          if (decision) {
            setTimeout(() => {
              if (get().approval?.id === approval.id) get().resolveApproval(decision);
            }, 0);
          }
        }
      },
      resolveApproval: (decision, reason) => {
        const approval = get().approval;
        if (!approval) return;

        // Keyed off the approval's own id rather than off "whatever is in the slot". The two are
        // the same when only one question is ever open, and different the moment two are: the second
        // setApproval overwrote the first, so answering the second left the first waiter's promise
        // unresolved forever. Taking the id from the object that is actually on screen closes that.
        const waiter = approvalWaiters.get(approval.id);
        approvalWaiters.delete(approval.id);
        waiter?.resolve({ decision, reason });

        if (decision === "allowed-for-run") {
          set({ allowForRunTools: [...get().allowForRunTools, approval.tool], approval: null });
        } else {
          set({ approval: null });
        }
      },

      /* ---------- canvas ---------- */
      setCanvas: (patch) => {
        /* keep the bulk edge selection coherent with single-selection patches:
         *  selecting nodes or a single edge implicitly collapses the bulk set */
        const derived: Partial<CanvasState> = { ...patch };
        if (patch.selectedEdgeIds !== undefined) {
          /* explicit bulk set — keep it verbatim */
        } else if (patch.selectedEdgeId !== undefined) {
          derived.selectedEdgeIds = patch.selectedEdgeId ? [patch.selectedEdgeId] : [];
        } else if (patch.selectedIds !== undefined || patch.primaryId !== undefined) {
          derived.selectedEdgeIds = [];
        }
        set({ canvas: { ...get().canvas, ...derived } });
      },
      setViewport: (zoom, panX, panY) => set({ canvas: { ...get().canvas, zoom, panX, panY } }),
      zoomAt: (factor, screenX, screenY, viewW, viewH) => {
        const { zoom, panX, panY } = get().canvas;
        const next = Math.max(0.06, Math.min(3.5, zoom * factor));
        const wx = (screenX - viewW / 2 + panX) / zoom;
        const wy = (screenY - viewH / 2 + panY) / zoom;
        set({
          canvas: {
            ...get().canvas,
            zoom: next,
            panX: viewW / 2 - wx * next,
            panY: viewH / 2 - wy * next,
          },
        });
      },
      panBy: (dx, dy) => {
        const { panX, panY } = get().canvas;
        set({ canvas: { ...get().canvas, panX: panX + dx, panY: panY + dy } });
      },
      selectNode: (id, additive) => {
        const { selectedIds, primaryId } = get().canvas;
        if (additive) {
          const has = selectedIds.includes(id);
          set({
            canvas: {
              ...get().canvas,
              selectedIds: has ? selectedIds.filter((x) => x !== id) : [...selectedIds, id],
              primaryId: has ? (primaryId === id ? (selectedIds[0] ?? null) : primaryId) : id,
              selectedEdgeId: null,
              selectedEdgeIds: [],
            },
          });
        } else {
          set({
            canvas: {
              ...get().canvas,
              selectedIds: [id],
              primaryId: id,
              selectedEdgeId: null,
              selectedEdgeIds: [],
            },
          });
        }
      },
      clearSelection: () =>
        set({
          canvas: {
            ...get().canvas,
            selectedIds: [],
            primaryId: null,
            selectedEdgeId: null,
            selectedEdgeIds: [],
          },
        }),
      selectEdge: (id) =>
        set({
          canvas: {
            ...get().canvas,
            selectedEdgeId: id,
            selectedEdgeIds: id ? [id] : [],
          },
        }),
      setSelectedEdgeIds: (ids) =>
        set({
          canvas: {
            ...get().canvas,
            selectedIds: [],
            primaryId: null,
            selectedEdgeId: ids.length > 0 ? ids[0] : null,
            selectedEdgeIds: ids,
          },
        }),
      moveNode: (id, x, y, commit) => {
        set({
          canvas: {
            ...get().canvas,
            nodes: get().canvas.nodes.map((n) => (n.id === id ? { ...n, x, y } : n)),
          },
        });
        if (commit) get().pushUndoSnapshot();
      },
      addNodes: (nodes) => set({ canvas: { ...get().canvas, nodes: [...get().canvas.nodes, ...nodes] } }),
      setNodeNote: (id, note) => {
        const current = get().canvas.nodes.find((n) => n.id === id);
        if (!current) return;
        const next = note === undefined || note.trim() === "" ? undefined : note.trim();
        if ((current.note ?? undefined) === next) return;
        get().pushUndoSnapshot();
        set({
          canvas: {
            ...get().canvas,
            nodes: get().canvas.nodes.map((n) =>
              n.id === id ? { ...n, note: next } : n,
            ),
          },
        });
      },
      addEdges: (edges) => set({ canvas: { ...get().canvas, edges: [...get().canvas.edges, ...edges] } }),
      setEdgeNote: (id, note) => {
        const current = get().canvas.edges.find((e) => e.id === id);
        if (!current) return;
        const next = note === undefined || note.trim() === "" ? undefined : note.trim();
        if ((current.note ?? undefined) === next) return;
        get().pushUndoSnapshot();
        set({
          canvas: {
            ...get().canvas,
            edges: get().canvas.edges.map((e) =>
              e.id === id ? { ...e, note: next } : e,
            ),
          },
        });
      },
      updateEdgeKind: (id, kind, opts) => {
        if (opts?.undoable) get().pushUndoSnapshot();
        set({
          canvas: {
            ...get().canvas,
            edges: get().canvas.edges.map((e) => (e.id === id ? { ...e, kind } : e)),
          },
        });
      },
      updateEdgeEndpoints: (id, from, to) =>
        set({
          canvas: {
            ...get().canvas,
            edges: get().canvas.edges.map((e) => (e.id === id ? { ...e, from, to } : e)),
          },
        }),
      pushUndoSnapshot: () => {
        const snapshot = {
          nodes: get().canvas.nodes.map((n) => ({ ...n })),
          edges: get().canvas.edges.map((e) => ({ ...e })),
        };
        set({ canvasUndo: [...get().canvasUndo.slice(-24), snapshot], canvasRedo: [] });
      },
      undoCanvas: () => {
        const { canvasUndo, canvasRedo, canvas } = get();
        const snapshot = canvasUndo[canvasUndo.length - 1];
        if (!snapshot) return;
        set({
          canvasUndo: canvasUndo.slice(0, -1),
          canvasRedo: [
            ...canvasRedo,
            { nodes: canvas.nodes.map((n) => ({ ...n })), edges: canvas.edges.map((e) => ({ ...e })) },
          ],
          canvas: { ...canvas, nodes: snapshot.nodes, edges: snapshot.edges },
        });
      },
      redoCanvas: () => {
        const { canvasUndo, canvasRedo, canvas } = get();
        const snapshot = canvasRedo[canvasRedo.length - 1];
        if (!snapshot) return;
        set({
          canvasRedo: canvasRedo.slice(0, -1),
          canvasUndo: [
            ...canvasUndo,
            { nodes: canvas.nodes.map((n) => ({ ...n })), edges: canvas.edges.map((e) => ({ ...e })) },
          ],
          canvas: { ...canvas, nodes: snapshot.nodes, edges: snapshot.edges },
        });
      },

      /* ---------- files & code ---------- */
      setFileContent: (path, content) =>
        set({ files: get().files.map((f) => (f.path === path ? { ...f, content } : f)) }),
      setFileModified: (path, modified) =>
        set({ files: get().files.map((f) => (f.path === path ? { ...f, modified } : f)) }),
      openCodeTab: (path) => {
        const { codeTabs, activeCodeTab } = get();
        if (codeTabs.includes(path)) {
          set({ activeCodeTab: path });
          return;
        }
        set({ codeTabs: [...codeTabs, path], activeCodeTab: path });
      },
      closeCodeTab: (path) => {
        const { codeTabs, activeCodeTab } = get();
        const next = codeTabs.filter((p) => p !== path);
        set({
          codeTabs: next,
          activeCodeTab: activeCodeTab === path ? (next[next.length - 1] ?? null) : activeCodeTab,
        });
      },
      setActiveCodeTab: (path) => set({ activeCodeTab: path }),
      markSave: (path, status) => set({ saveStatus: { ...get().saveStatus, [path]: status } }),

      // Replaced in server mode by sync.ts, which owns the debounced write queue. Demo mode has
      // nowhere to write to, so this is the honest no-op rather than a promise that never settles.
      flushPendingWrites: async () => {},

      /* ---------- context ---------- */
      setContextFiles: (files) => set({ contextFiles: files }),
      addContextFile: (file) =>
        set({ contextFiles: [...get().contextFiles.filter((c) => c.path !== file.path), file] }),
      removeContextFile: (path) => set({ contextFiles: get().contextFiles.filter((c) => c.path !== path) }),
      pinContextFile: (path) =>
        set({
          contextFiles: get().contextFiles.map((c) =>
            c.path === path ? { ...c, reason: c.reason === "pinned" ? "relevant" : "pinned" } : c,
          ),
        }),

      /* ---------- checkpoints ---------- */
      addCheckpoint: (cp) => set({ checkpoints: [...get().checkpoints, cp] }),
      restoreCheckpoint: (id) => {
        const cp = get().checkpoints.find((c) => c.id === id);
        if (!cp) return;
        cp.filesSnapshot.forEach((snap) => {
          get().setFileContent(snap.path, snap.content);
          get().setFileModified(snap.path, false);
        });
        /* restore the canvas graph captured with the checkpoint (keep viewport) */
        if (cp.canvasSnapshot) {
          const { nodes, edges } = cp.canvasSnapshot;
          set((state) => ({
            canvas: {
              ...state.canvas,
              nodes: [...nodes],
              edges: [...edges],
              selectedIds: [],
              primaryId: null,
              selectedEdgeId: null,
            },
          }));
        }
        get().addEvent({
          id: uid("ev"),
          ts: Date.now(),
          kind: "checkpoint",
          title: `Restored checkpoint “${cp.label}”`,
          detail: `${cp.filesSnapshot.length} file(s) reverted${cp.canvasSnapshot ? ` · graph (${cp.canvasSnapshot.nodes.length} nodes) restored` : ""}`,
          status: "done",
        });
        get().pushToast("Checkpoint restored", `${cp.filesSnapshot.length} file(s) reverted to “${cp.label}”.`);
      },

      /* ---------- providers ---------- */
      setProviderState: (id, patch) =>
        set({ providers: get().providers.map((p) => (p.id === id ? { ...p, ...patch } : p)) }),
    }),
    {
      name: "kontur-code-proto",
      storage: createJSONStorage(() => localStorage),
      /**
       * What goes to localStorage, and — more importantly — what does not.
       *
       * The backend is the source of truth for conversations, files and the graph; `sync.ts` hydrates
       * them on boot. This cache exists so a reload is not a blank screen for a moment, not so it can
       * hold a second copy of every file body.
       *
       * It used to persist `files` whole, plus each checkpoint's `filesSnapshot` — which is every
       * file's text again — plus every message of every session. On a real project that is
       * megabytes, and localStorage caps out around 5–10 MB. The failure is the bad part: the quota
       * exception is thrown inside the persist middleware's own subscriber, so it is swallowed, and
       * from that point on *nothing* is written again. No error, no warning, and a reload loses the
       * session. So bodies stay out and paths come in.
       */
      partialize: (state) => ({
        sessions: state.sessions.map((session) => ({
          ...session,
          // Bounded so a long-lived install cannot grow without limit; the backend holds the rest.
          messages: session.messages.slice(-MAX_PERSISTED_MESSAGES),
        })),
        currentSessionId: state.currentSessionId,
        ui: state.ui,
        goals: state.goals,
        // Metadata only. `filesSnapshot` is every file's text, once per checkpoint.
        checkpoints: state.checkpoints.map(({ filesSnapshot: _bodies, ...rest }) => rest),
        canvas: state.canvas,
        // Paths and sizes, not contents.
        files: state.files.map(({ content: _content, ...rest }) => rest),
        codeTabs: state.codeTabs,
        activeCodeTab: state.activeCodeTab,
        contextFiles: state.contextFiles,
      }),
      /**
       * Bumped when the persisted shape changes. An install carrying an older shape is not migrated
       * field by field — the backend rehydrates it on boot, so starting from defaults is both correct
       * and far cheaper than guessing at a shape that no longer exists.
       */
      version: PERSIST_VERSION,
      migrate: (persisted: unknown) => {
        const shape = persisted as { state?: { version?: number } } | undefined;
        const version = shape?.state?.version;

        if (version === undefined || version < PERSIST_VERSION) {
          console.info(
            `[kontur] local cache written by an older build (v${version ?? "none"}); rebuilding from defaults. The backend has the real state.`,
          );
          return undefined;
        }

        return persisted;
      },
      onRehydrateStorage: () => (state) => {
        if (!state) return;
        /* NB: runs synchronously during store creation for sync storage —
           defer setState until the module-level const exists. */
        queueMicrotask(() => {
          /* migrate persisted prefs that predate the Chat/Cowork/Code +
             work-method controls: derive them from the stored agentMode. */
          const s0 = useKontur.getState();
          const ui = s0.ui;
          /* older persisted prefs predate these axes — treat them as maybe-absent */
          const persisted = ui as { workMode?: WorkMode; workMethod?: WorkMethod; modeChosen?: boolean };
          const migratedUi =
            persisted.workMode === undefined || persisted.workMethod === undefined
              ? {
                  ...ui,
                  workMode: (ui.agentMode === "off" ? "chat" : "cowork") as WorkMode,
                  workMethod: (ui.agentMode === "plan" || ui.agentMode === "plancanvas"
                    ? "plan"
                    : "ask") as WorkMethod,
                }
              : ui;
          /* per-mode chats: ensure the memory map exists, then reconcile the
             restored mode with the chat that is actually open so the mode-filtered
             list and the open conversation never disagree. The open chat wins —
             legacy chats are all untagged ("chat"), so an upgrading user keeps
             their last conversation visible and selected rather than being
             stranded on an empty mode. */
          const activeSessionByMode: Partial<Record<WorkMode, string>> = {
            ...(migratedUi.activeSessionByMode ?? {}),
          };
          let sessions = s0.sessions;
          let currentSessionId = s0.currentSessionId;
          const cur = sessions.find((x) => x.id === currentSessionId);
          let workMode = migratedUi.workMode;
          if (cur) {
            workMode = sessionModeOf(cur);
            activeSessionByMode[workMode] = cur.id;
          } else {
            let targetId = pickSessionForMode(sessions, workMode, activeSessionByMode[workMode]);
            if (!targetId) {
              const placeholder = makePlaceholder(workMode, migratedUi.selectedModelId);
              sessions = sortSessions([placeholder, ...sessions]);
              targetId = placeholder.id;
            }
            currentSessionId = targetId;
            activeSessionByMode[workMode] = targetId;
          }
          /* first-run onboarding: show the mode picker until a mode is chosen.
             absent flag (fresh or pre-onboarding installs) => show it once. */
          const startSurface = s0.surface;
          useKontur.setState({
            hydrated: true,
            sessions,
            currentSessionId,
            ui: {
              ...migratedUi,
              appZoom: migratedUi.appZoom ?? 1,
              workMode,
              modeChosen: persisted.modeChosen ?? false,
              agentMode: engineModeFor(workMode, migratedUi.workMethod),
              activeSessionByMode,
            },
            /* clamp a persisted surface that doesn't belong to the restored mode */
            surface: surfaceAllowedInMode(startSurface, workMode) ? startSurface : modeDefaultSurface[workMode],
            runner: { status: "idle" },
            approval: null,
            toasts: [],
            recording: false,
            paletteOpen: false,
            checkpointJumpOpen: false,
            cheatsheetOpen: false,
            projectExportOpen: false,
            providerStudioOpen: false,
            canvasOverlayActive: false,
          });
        });
      },
    },
  ),
);

/**
 * Watches the size of what persistence would write, and says so before the browser refuses.
 *
 * The quota error this guards against is thrown by `setItem` inside the persist middleware's own
 * subscriber, where nothing catches it: the write silently stops happening and the only symptom is
 * that a reload loses the session. Warning at a threshold well under the real cap turns a mystery
 * into a line in the console, and the throttle keeps a streaming run from logging it per frame.
 */
const PERSIST_WARN_BYTES = 2 * 1024 * 1024;
let persistWarned = false;
let lastPersistCheck = 0;

if (typeof window !== "undefined") {
  window.addEventListener("beforeunload", () => {
    /* Best effort only: beforeunload cannot await, so this marks the state and relies on the
       document being kept alive long enough for the request to leave. Debouncing means there is
       normally nothing to flush. */
    void useKontur.getState().flushPendingWrites?.();
  });

  window.setInterval(() => {
    const now = Date.now();
    if (persistWarned || now - lastPersistCheck < 5_000) return;
    lastPersistCheck = now;

    try {
      const serialised = JSON.stringify(
        (useKontur.getState() as unknown as Record<string, unknown>),
      ).length;

      if (serialised > PERSIST_WARN_BYTES) {
        persistWarned = true;
        console.warn(
          `[kontur] local state is ${Math.round(serialised / 1024 / 1024)} MB. Browsers cap this near 5-10 MB, and exceeding the cap stops persistence silently. The backend still holds the real conversations; this cache is a fast first paint, not a backup.`,
        );
      }
    } catch {
      /* A value that will not serialise is its own problem, and logging it every five seconds
         would be noise. */
    }
  }, 5_000);
}
