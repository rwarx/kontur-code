/* ============================================================
   KONTUR CODE — domain types (mirrors the WPF app models)
   ============================================================ */

export type Locale = "en" | "ru" | "de";
export type ThemeMode = "dark" | "light";

export type Surface =
  | "chat"
  | "canvas"
  | "graph"
  | "files"
  | "code"
  | "git"
  | "models"
  | "tasks"
  | "trajectory"
  | "settings"
  | "workflows";

export type AgentMode = "plan" | "plancanvas" | "build" | "off";

/* Top-level workspace switch (Claude-Code-style). Chat & Cowork share one
   chat surface — the difference is whether the agent runs tools; Code opens
   the IDE surface. Maps onto the engine AgentMode + WorkMethod. */
export type WorkMode = "chat" | "cowork" | "code";

/* Autonomy selector shown while coworking:
   plan  — read-only planning (engine "plan")
   ask   — agent works, asks before every change (engine "build", manual approvals)
   auto  — auto-approves read/write, still asks before running commands
   full  — auto-approves everything (backend still gates execute via AllowCommands) */
export type WorkMethod = "plan" | "ask" | "auto" | "full";

export type ToolRisk = "read" | "write" | "execute";
export type ToolState =
  | "proposed"
  | "running"
  | "succeeded"
  | "failed"
  | "denied"
  | "abandoned";

export interface DiffLine {
  kind: "context" | "add" | "del" | "header" | "notice";
  text: string;
}

export interface ToolCall {
  id: string;
  tool: string;
  risk: ToolRisk;
  state: ToolState;
  headline: string;
  body?: string;
  diff?: DiffLine[];
  durationMs?: number;
  agent?: string;
}

export interface Attachment {
  id: string;
  name: string;
  sizeKb: number;
}

/** A file staged in the composer, carrying the text the run will send. */
export interface DraftAttachment extends Attachment {
  mimeType: string;
  textContent: string;
}

export interface PlanStep {
  id: string;
  title: string;
  status: "pending" | "running" | "done" | "failed";
  detail?: string;
}

export interface ContractItem {
  id: string;
  text: string;
  done: boolean;
}

export interface SubAgent {
  id: string;
  name: string;
  role: string;
  status: "pending" | "running" | "done" | "failed";
  summary?: string;
  tokens?: number;
}

export type MessageBlock =
  | { type: "plan"; goal: string; steps: PlanStep[] }
  | { type: "contract"; title: string; items: ContractItem[] }
  | { type: "agenttree"; agents: SubAgent[] }
  | { type: "checkpoint"; label: string; file: string; restorable: boolean }
  | { type: "event"; level: "info" | "error" | "success"; title: string; detail?: string };

export interface Message {
  id: string;
  role: "user" | "assistant";
  content: string;
  reasoning?: string;
  createdAt: number;
  modelId?: string;
  providerId?: string;
  streaming?: boolean;
  toolCalls: ToolCall[];
  blocks?: MessageBlock[];
  attachments?: Attachment[];
  usage?: { inputTokens: number; outputTokens: number; ms: number };
  folded?: boolean;
  /** true when the content came from the live model backend (not the scripted scenario) */
  live?: boolean;
  /** skill ids the agent auto-activated for this run (see scenario.selectSkillsForRun) */
  skills?: string[];
  /** set when the run that produced this assistant message failed; surfaced inline in
   *  the bubble with a Retry action instead of leaving an empty/broken-looking reply */
  error?: string;
  /** server errorKind for the failed run (e.g. "InvalidApiKey"), when known */
  errorKind?: string;
}

export type TrajectoryKind =
  | "request"
  | "plan"
  | "context"
  | "tool"
  | "subagent"
  | "build"
  | "test"
  | "error"
  | "fix"
  | "review"
  | "checkpoint"
  | "approval"
  | "complete"
  | "compact"
  | "fork"
  | "skills";

export interface TrajectoryEvent {
  id: string;
  ts: number;
  kind: TrajectoryKind;
  title: string;
  detail?: string;
  status?: "running" | "done" | "failed" | "warning";
  agent?: string;
  durationMs?: number;
}

export type GoalStatus =
  | "planning"
  | "running"
  | "waiting"
  | "verifying"
  | "completed"
  | "failed";

export interface Goal {
  id: string;
  title: string;
  createdAt: number;
  status: GoalStatus;
  criteria: ContractItem[];
  sessionId: string;
}

export interface Approval {
  id: string;
  tool: string;
  risk: ToolRisk;
  headline: string;
  consequence: string;
  diff?: DiffLine[];
  argsNote?: string;
  allowForRunAvailable: boolean;
}

export type ApprovalDecision = "allowed" | "allowed-for-run" | "denied";

/* ---------- canvas ---------- */

export type NodeKind =
  | "file"
  | "folder"
  | "module"
  | "service"
  | "interface"
  | "data"
  | "view"
  | "test"
  | "plan"
  | "task"
  | "agent"
  | "model"
  | "external"
  | "note";

export type EdgeKind =
  | "contains"
  | "depends"
  | "calls"
  | "implements"
  | "relates"
  | "plans";

export interface CanvasNode {
  id: string;
  kind: NodeKind;
  title: string;
  meta?: string;
  x: number;
  y: number;
  w?: number;
  h?: number;
  path?: string;
  /** free-form annotation attached to the node (persisted with the graph) */
  note?: string;
}

export interface CanvasEdge {
  id: string;
  from: string;
  to: string;
  kind: EdgeKind;
  /** free-form label attached to the connection (persisted with the graph,
   *  rendered as a sticky chip at the edge midpoint) */
  note?: string;
}

export interface CanvasState {
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  selectedIds: string[];
  primaryId: string | null;
  selectedEdgeId: string | null;
  /** bulk edge selection (graph-outline marquee / shift-click) — the canvas
   *  highlights every member; `selectedEdgeId` stays the single primary edge */
  selectedEdgeIds: string[];
  zoom: number;
  panX: number;
  panY: number;
  showGrid: boolean;
  tool: "select" | "pan";
  /** edge kinds hidden via the legend toggles (view state, not undoable) */
  hiddenEdgeKinds: EdgeKind[];
}

/* ---------- sessions ---------- */

export interface Session {
  id: string;
  title: string;
  createdAt: number;
  updatedAt: number;
  pinned?: boolean;
  preview: string;
  messages: Message[];
  events: TrajectoryEvent[];
  goalIds: string[];
  modelId: string;
  forkedFrom?: string;
  foldedCount?: number;
  contextTokens?: number;
  /** which top-level work mode this chat belongs to. Chats are partitioned by
   *  mode so chat / cowork / code keep separate, independently-switched lists.
   *  Absent on legacy/pre-feature conversations — treated as "chat". */
  mode?: WorkMode;
}

/* ---------- providers / models ---------- */

export type ProviderState = "connected" | "missing-key" | "testing" | "failed" | "unknown";

export interface ModelInfo {
  id: string;
  name: string;
  providerId: string;
  contextK: number;
  vision?: boolean;
  tools?: boolean;
  pricePrompt?: number; // $ per 1M prompt tokens
  kind?: "chat" | "reasoner";
}

export interface Provider {
  id: string;
  name: string;
  endpoint: string;
  state: ProviderState;
  statusMessage?: string;
  models: ModelInfo[];
  builtin: boolean;
  /** Server-side enable flag. Undefined (offline/legacy) is treated as enabled. */
  enabled?: boolean;
  /** Deep-link to the provider's API-key page (server `apiKeyUrl`). */
  apiKeyUrl?: string;
}

/* ---------- workspace files (demo project) ---------- */

export interface DemoFile {
  path: string;
  language: "csharp" | "json" | "markdown" | "xml" | "text";
  content: string;
  modified?: boolean;
}

export interface ContextFile {
  path: string;
  tokens: number;
  reason: "pinned" | "relevant" | "history" | "tool";
}

export interface Checkpoint {
  id: string;
  label: string;
  createdAt: number;
  filesSnapshot: { path: string; content: string }[];
  /** optional canvas graph snapshot (nodes + edges) captured with the checkpoint */
  canvasSnapshot?: { nodes: CanvasNode[]; edges: CanvasEdge[] };
  sessionId: string;
  message: string;
}

export interface RunnerState {
  status: "idle" | "running" | "paused" | "awaiting-approval" | "done" | "stopped";
  label?: string;
}
