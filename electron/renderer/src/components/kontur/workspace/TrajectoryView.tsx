"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Activity,
  Bookmark,
  Bot,
  ChevronDown,
  ChevronRight,
  Circle,
  CircleCheck,
  CircleX,
  Download,
  Eye,
  FlaskConical,
  FoldVertical,
  FolderSearch,
  GitFork,
  Hammer,
  ListChecks,
  LoaderCircle,
  MessageSquare,
  Play,
  Search,
  ShieldAlert,
  Terminal,
  TriangleAlert,
  Waypoints,
  Wrench,
  X,
  Zap,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { exportTrajectoryJson, exportTrajectoryMarkdown } from "@/lib/kontur/export";
import type { RunnerState, SubAgent, TrajectoryEvent, TrajectoryKind } from "@/lib/kontur/types";
import {
  KcBadge,
  KcCard,
  KcGhostButton,
  Overline,
  StatusDot,
} from "@/components/kontur/ui";
import { cn } from "@/lib/utils";
import { formatClock, formatDuration, relativeTime, runnerStatusText } from "./helpers";

/* ============================================================
   TRAJECTORY — agent-run timeline + agent tree.
   The flagship "what the runtime did" surface.
   ============================================================ */

const KIND_ICONS: Record<TrajectoryKind, LucideIcon> = {
  request: MessageSquare,
  plan: ListChecks,
  context: FolderSearch,
  tool: Terminal,
  subagent: Bot,
  build: Hammer,
  test: FlaskConical,
  error: TriangleAlert,
  fix: Wrench,
  review: Eye,
  checkpoint: Bookmark,
  approval: ShieldAlert,
  complete: CircleCheck,
  compact: FoldVertical,
  fork: GitFork,
  skills: Zap,
};

type FilterCategory = "all" | "tools" | "build" | "agents" | "issues";

const FILTER_CHIPS: { id: FilterCategory; key: string }[] = [
  { id: "all", key: "trajectory.all" },
  { id: "tools", key: "trajectory.filterTools" },
  { id: "build", key: "trajectory.filterBuild" },
  { id: "agents", key: "trajectory.filterAgents" },
  { id: "issues", key: "trajectory.filterIssues" },
];

function inCategory(ev: TrajectoryEvent, cat: FilterCategory): boolean {
  switch (cat) {
    case "tools":
      return ev.kind === "tool" || ev.kind === "fix";
    case "build":
      return ev.kind === "build" || ev.kind === "test";
    case "agents":
      return ev.kind === "subagent";
    case "issues":
      return ev.kind === "error" || ev.status === "failed" || ev.status === "warning";
    default:
      return true;
  }
}

function runnerDot(status: RunnerState["status"]): { color: "success" | "warning" | "error" | "muted"; pulse: boolean } {
  switch (status) {
    case "running":
      return { color: "warning", pulse: true };
    case "awaiting-approval":
      return { color: "warning", pulse: true };
    case "done":
      return { color: "success", pulse: false };
    case "stopped":
      return { color: "error", pulse: false };
    default:
      return { color: "muted", pulse: false };
  }
}

function eventDotClass(ev: TrajectoryEvent): string {
  switch (ev.status) {
    case "done":
      return "bg-success";
    case "running":
      return "animate-pulse-dot bg-warning";
    case "failed":
      return "bg-error";
    case "warning":
      return "bg-warning";
    default:
      return "bg-fg-3";
  }
}

function eventIconTone(ev: TrajectoryEvent): string {
  if (ev.status === "failed" || ev.kind === "error") return "text-error";
  if (ev.status === "running" || ev.status === "warning") return "text-warning";
  if (ev.kind === "complete") return "text-success";
  return "text-fg-3";
}

function eventBadgeTone(ev: TrajectoryEvent): "neutral" | "success" | "warning" | "error" {
  if (ev.status === "failed" || ev.kind === "error") return "error";
  if (ev.status === "warning") return "warning";
  if (ev.status === "running") return "warning";
  if (ev.kind === "complete") return "success";
  return "neutral";
}

/* ---------- subagent status icon ---------- */
function AgentStatusIcon({ status }: { status: SubAgent["status"] }) {
  if (status === "running") return <LoaderCircle size={12} className="shrink-0 animate-spin text-warning" />;
  if (status === "done") return <CircleCheck size={12} className="shrink-0 text-success" />;
  if (status === "failed") return <CircleX size={12} className="shrink-0 text-error" />;
  return <Circle size={12} className="shrink-0 text-fg-3" />;
}

/* ---------- one timeline event row ---------- */
function EventRow({
  ev,
  isFirst,
  isLast,
  collapsed,
  onToggle,
}: {
  ev: TrajectoryEvent;
  isFirst: boolean;
  isLast: boolean;
  collapsed: boolean;
  onToggle: () => void;
}) {
  const Icon = KIND_ICONS[ev.kind] ?? Activity;
  const hasDetail = !!ev.detail;
  return (
    <div className="flex">
      <span className="w-16 shrink-0 select-none pt-[7px] pr-3 text-right font-mono text-[10px] leading-[14px] text-fg-3">
        {formatClock(ev.ts)}
      </span>
      {/* dot column with the connecting rail */}
      <div className="relative w-[22px] shrink-0 self-stretch" aria-hidden>
        <span
          className={cn(
            "absolute left-1/2 w-px -translate-x-1/2 bg-line-faint",
            isFirst ? "top-[14px]" : "top-0",
            isLast ? "bottom-[calc(100%_-_14px)]" : "bottom-0",
          )}
        />
        <span
          className={cn("absolute left-1/2 top-[10px] h-2 w-2 -translate-x-1/2 rounded-full", eventDotClass(ev))}
        />
      </div>
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={hasDetail ? !collapsed : undefined}
        className="kc-focus-ring group mb-0.5 flex min-w-0 flex-1 items-start gap-2 rounded-sm px-2 py-1.5 text-left transition-colors duration-100 hover:bg-surface-hover"
      >
        <Icon size={13} className={cn("mt-[3px] shrink-0", eventIconTone(ev))} />
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-2">
            <span className="min-w-0 truncate text-[12.5px] font-medium text-fg-1">{ev.title}</span>
            <KcBadge tone={eventBadgeTone(ev)}>{ev.kind}</KcBadge>
            {ev.durationMs != null && (
              <span className="ml-auto shrink-0 font-mono text-[10.5px] text-fg-3">
                {formatDuration(ev.durationMs)}
              </span>
            )}
          </span>
          {hasDetail && !collapsed && (
            <span className="mt-0.5 block break-words text-[11.5px] leading-[1.45] text-fg-2">
              {ev.detail}
            </span>
          )}
          {ev.agent && (
            <span className="mt-0.5 block font-mono text-[10px] text-fg-3">{ev.agent}</span>
          )}
        </span>
        {hasDetail && (
          <ChevronDown
            size={12}
            className={cn(
              "mt-[4px] shrink-0 text-fg-3 transition-transform duration-100",
              collapsed && "-rotate-90",
            )}
          />
        )}
      </button>
    </div>
  );
}

/* ============================================================ */

export default function TrajectoryView() {
  const t = useT();
  const session = useKontur((s) => s.sessions.find((x) => x.id === s.currentSessionId));
  const runner = useKontur((s) => s.runner);
  const checkpoints = useKontur((s) => s.checkpoints);
  const setSurface = useKontur((s) => s.setSurface);
  const restoreCheckpoint = useKontur((s) => s.restoreCheckpoint);

  const [pane, setPane] = useState<"tree" | "timeline">("timeline");
  const [filter, setFilter] = useState<FilterCategory>("all");
  const [query, setQuery] = useState("");
  const [agentFilter, setAgentFilter] = useState<string | null>(null);
  const [collapsedIds, setCollapsedIds] = useState<Set<string>>(new Set());
  const [exportOpen, setExportOpen] = useState(false);
  const exportRef = useRef<HTMLDivElement>(null);
  const timelineRef = useRef<HTMLDivElement>(null);
  const stickToBottom = useRef(true);

  const events = session?.events ?? [];

  /* close the export menu on outside click */
  useEffect(() => {
    if (!exportOpen) return;
    const onDown = (e: MouseEvent) => {
      if (exportRef.current && !exportRef.current.contains(e.target as Node)) setExportOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [exportOpen]);

  /* keep the tail of the trace in view while the run is live */
  useEffect(() => {
    const el = timelineRef.current;
    if (!el || !stickToBottom.current) return;
    if (runner.status === "running" || runner.status === "awaiting-approval") {
      el.scrollTop = el.scrollHeight;
    }
  }, [events.length, runner.status]);

  /* latest agenttree block in the transcript = current subagent roster */
  const subagents = useMemo<SubAgent[]>(() => {
    let found: SubAgent[] = [];
    for (const m of session?.messages ?? []) {
      for (const b of m.blocks ?? []) {
        if (b.type === "agenttree") found = b.agents;
      }
    }
    return found;
  }, [session]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return events.filter((ev) => {
      if (agentFilter && ev.agent !== agentFilter) return false;
      if (filter !== "all" && !inCategory(ev, filter)) return false;
      if (q && !`${ev.title} ${ev.detail ?? ""}`.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [events, filter, query, agentFilter]);

  /* last ~3 events animate in when they appear */
  const recentIds = useMemo(() => new Set(events.slice(-3).map((e) => e.id)), [events]);

  const dot = runnerDot(runner.status);
  const runnerLabel = runnerStatusText(t, runner.status, runner.label);

  const toggleEvent = (id: string) =>
    setCollapsedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const filterActive = filter !== "all" || !!agentFilter || query.trim().length > 0;

  return (
    <div className="flex h-full w-full flex-col">
      {/* mobile: switch between the two columns */}
      <div className="flex items-center gap-1 border-b border-line-faint px-2 py-1.5 md:hidden">
        <KcGhostButton active={pane === "tree"} onClick={() => setPane("tree")}>
          <Bot size={13} />
          {t("trajectory.agentTree")}
        </KcGhostButton>
        <KcGhostButton active={pane === "timeline"} onClick={() => setPane("timeline")}>
          <Waypoints size={13} />
          {t("trajectory.timeline")}
        </KcGhostButton>
      </div>

      <div className="flex min-h-0 flex-1">
        {/* ---------------- agent tree ---------------- */}
        <aside
          className={cn(
            "min-h-0 w-full shrink-0 overflow-y-auto md:w-[260px]",
            pane === "tree" ? "block" : "hidden md:block",
          )}
        >
          <div className="flex items-center justify-between px-3 py-2">
            <Overline>{t("trajectory.agentTree")}</Overline>
            {subagents.length > 0 && (
              <span className="font-mono text-[10px] text-fg-3">{subagents.length}</span>
            )}
          </div>

          <div className="space-y-0.5 px-2 pb-3">
            {/* main agent */}
            <button
              type="button"
              onClick={() => setAgentFilter(null)}
              title={agentFilter ? t("trajectory.all") : undefined}
              className={cn(
                "kc-focus-ring flex w-full items-center gap-2 rounded-sm px-2 py-1.5 transition-colors duration-100 hover:bg-surface-hover",
                !agentFilter && "bg-surface-hover",
              )}
            >
              <StatusDot color={dot.color} pulse={dot.pulse} />
              <Bot size={13} className="shrink-0 text-fg-2" />
              <span className="min-w-0 truncate text-[12.5px] font-medium text-fg-1">
                {t("agents.main")}
              </span>
              <span className="ml-auto max-w-[96px] truncate text-[10.5px] text-fg-3">{runnerLabel}</span>
            </button>

            {/* folded-transcript note */}
            {!!session?.foldedCount && (
              <div className="px-2 pt-1">
                <span className="inline-flex max-w-full items-center gap-1.5 rounded-xs border border-line bg-sunken px-1.5 py-0.5 font-mono text-[10px] text-fg-3">
                  <FoldVertical size={10} className="shrink-0" />
                  <span className="truncate">{t("chat.folded", session.foldedCount)}</span>
                </span>
              </div>
            )}

            {/* subagents from the latest agenttree block */}
            {subagents.length > 0 ? (
              <div className="ml-3 border-l border-line-faint pl-3 pt-1">
                <div className="space-y-0.5">
                  {subagents.map((agent) => (
                    <button
                      key={agent.id}
                      type="button"
                      onClick={() => setAgentFilter(agentFilter === agent.name ? null : agent.name)}
                      className={cn(
                        "kc-focus-ring flex w-full items-start gap-2 rounded-sm px-2 py-1.5 text-left transition-colors duration-100 hover:bg-surface-hover",
                        agentFilter === agent.name && "bg-accent-soft hover:bg-accent-soft",
                      )}
                    >
                      <AgentStatusIcon status={agent.status} />
                      <span className="min-w-0 flex-1">
                        <span className="flex items-baseline gap-1.5">
                          <span className="shrink-0 text-[12.5px] font-medium text-fg-1">{agent.name}</span>
                          <span className="min-w-0 truncate text-[10.5px] text-fg-3">{agent.role}</span>
                          {agent.tokens != null && (
                            <span className="ml-auto shrink-0 font-mono text-[10.5px] text-fg-3">
                              {agent.tokens.toLocaleString("en-US")}
                            </span>
                          )}
                        </span>
                        {agent.summary && (
                          <span className="mt-0.5 line-clamp-2 block text-[11.5px] leading-[1.4] text-fg-2">
                            {agent.summary}
                          </span>
                        )}
                      </span>
                    </button>
                  ))}
                </div>
              </div>
            ) : (
              <p className="px-2 py-2 text-[11.5px] leading-[1.45] text-fg-3">{t("trajectory.noAgents")}</p>
            )}
          </div>

          {/* checkpoints */}
          {checkpoints.length > 0 && (
            <div className="border-t border-line-faint px-2 py-2">
              <Overline className="mb-1 px-1">{t("trajectory.checkpoints")}</Overline>
              <div className="space-y-0.5">
                {checkpoints.map((cp) => (
                  <div key={cp.id} className="flex items-center gap-2 rounded-sm px-1 py-1 hover:bg-surface-hover">
                    <Bookmark size={12} className="shrink-0 text-fg-3" />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[11.5px] text-fg-2">{cp.label}</span>
                      <span className="flex items-center gap-1 font-mono text-[10px] text-fg-3">
                        {formatClock(cp.createdAt)} · {relativeTime(cp.createdAt, t)}
                        {cp.canvasSnapshot && (
                          <span
                            className="inline-flex items-center gap-0.5 text-fg-3/80"
                            title={t("trajectory.checkpointGraph", cp.canvasSnapshot.nodes.length)}
                          >
                            <Waypoints size={9} aria-hidden />
                            {cp.canvasSnapshot.nodes.length}
                          </span>
                        )}
                      </span>
                    </span>
                    <KcGhostButton
                      className="h-6 px-2 text-[11px]"
                      onClick={() => restoreCheckpoint(cp.id)}
                    >
                      {t("checkpoint.restore")}
                    </KcGhostButton>
                  </div>
                ))}
              </div>
            </div>
          )}
        </aside>

        {/* ---------------- timeline ---------------- */}
        <section
          className={cn(
            "min-h-0 min-w-0 flex-1 flex-col md:border-l md:border-line-faint",
            pane === "timeline" ? "flex" : "hidden md:flex",
          )}
        >
          <header className="border-b border-line-faint px-3 py-2">
            <div className="flex items-center gap-2">
              <Overline>{t("trajectory.timeline")}</Overline>
              <KcBadge>{filterActive ? filtered.length : events.length}</KcBadge>
            </div>
            <p className="mt-0.5 hidden text-[11.5px] leading-[1.4] text-fg-3 sm:block">
              {t("trajectory.subtitle")}
            </p>
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              {FILTER_CHIPS.map((chip) => (
                <button
                  key={chip.id}
                  type="button"
                  aria-pressed={filter === chip.id}
                  onClick={() => setFilter(chip.id)}
                  className={cn(
                    "kc-focus-ring h-[22px] rounded-sm border px-2 text-[11px] font-medium transition-colors duration-100",
                    filter === chip.id
                      ? "border-line-strong bg-surface-hover text-fg-1"
                      : "border-line text-fg-3 hover:bg-surface-hover hover:text-fg-1",
                  )}
                >
                  {t(chip.key)}
                </button>
              ))}
              {agentFilter && (
                <span className="inline-flex h-[22px] items-center gap-1 rounded-sm border border-accent-border bg-accent-soft px-1.5 font-mono text-[10.5px] text-accent">
                  <Bot size={11} className="shrink-0" />
                  <span className="max-w-[120px] truncate">{agentFilter}</span>
                  <button
                    type="button"
                    onClick={() => setAgentFilter(null)}
                    aria-label={t("common.close")}
                    className="kc-focus-ring flex h-4 w-4 items-center justify-center rounded-xs transition-colors duration-100 hover:bg-accent-soft"
                  >
                    <X size={11} />
                  </button>
                </span>
              )}
              <div className="relative ml-auto min-w-[130px] max-w-[220px] flex-1 sm:flex-none">
                <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-fg-3" />
                <input
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder={t("trajectory.filter")}
                  aria-label={t("trajectory.filter")}
                  className="h-[26px] w-full rounded-sm border border-line bg-sunken pl-7 pr-2 text-[12px] text-fg-1 outline-none transition-colors duration-100 placeholder:text-fg-3 focus:border-line-strong"
                />
              </div>

              {/* export — Markdown report / JSON */}
              {events.length > 0 && (
                <div ref={exportRef} className="relative shrink-0">
                  <button
                    type="button"
                    onClick={() => setExportOpen((v) => !v)}
                    aria-haspopup="menu"
                    aria-expanded={exportOpen}
                    aria-label={t("trajectory.export.label")}
                    title={t("trajectory.export.label")}
                    className={cn(
                      "kc-focus-ring flex h-[26px] items-center gap-1.5 rounded-sm border px-2 text-[11px] font-medium transition-colors duration-100",
                      exportOpen
                        ? "border-line-strong bg-surface-hover text-fg-1"
                        : "border-line text-fg-3 hover:bg-surface-hover hover:text-fg-1",
                    )}
                  >
                    <Download size={12} />
                    <ChevronDown
                      size={11}
                      className={cn("transition-transform duration-150", exportOpen && "rotate-180")}
                    />
                  </button>
                  {exportOpen && (
                    <div
                      role="menu"
                      aria-label={t("trajectory.export.label")}
                      className="animate-rise absolute right-0 top-[calc(100%+4px)] z-30 w-[220px] overflow-hidden rounded-md border border-line bg-surface-2 shadow-overlay"
                    >
                      <button
                        type="button"
                        role="menuitem"
                        onClick={() => {
                          if (session) exportTrajectoryMarkdown(session);
                          setExportOpen(false);
                        }}
                        className="kc-focus-ring flex w-full items-start gap-2.5 border-b border-line-faint/60 px-2.5 py-2 text-left transition-colors hover:bg-surface-hover"
                      >
                        <ListChecks size={13} className="mt-0.5 shrink-0 text-fg-3" />
                        <span>
                          <span className="block text-[12px] font-medium text-fg-1">
                            {t("trajectory.export.md")}
                          </span>
                          <span className="block text-[10.5px] leading-snug text-fg-3">
                            {t("trajectory.export.mdHint")}
                          </span>
                        </span>
                      </button>
                      <button
                        type="button"
                        role="menuitem"
                        onClick={() => {
                          if (session) exportTrajectoryJson(session);
                          setExportOpen(false);
                        }}
                        className="kc-focus-ring flex w-full items-start gap-2.5 px-2.5 py-2 text-left transition-colors hover:bg-surface-hover"
                      >
                        <Waypoints size={13} className="mt-0.5 shrink-0 text-fg-3" />
                        <span>
                          <span className="block text-[12px] font-medium text-fg-1">
                            {t("trajectory.export.json")}
                          </span>
                          <span className="block text-[10.5px] leading-snug text-fg-3">
                            {t("trajectory.export.jsonHint")}
                          </span>
                        </span>
                      </button>
                    </div>
                  )}
                </div>
              )}
            </div>
          </header>

          <div
            ref={timelineRef}
            onScroll={() => {
              const el = timelineRef.current;
              if (!el) return;
              stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60;
            }}
            className="min-h-0 flex-1 overflow-y-auto px-3 py-3"
          >
            {events.length === 0 ? (
              <KcCard className="mx-auto mt-10 max-w-sm p-6 text-center">
                <Waypoints size={28} className="mx-auto text-fg-3" />
                <p className="mt-3 text-[13px] leading-[1.5] text-fg-2">{t("trajectory.empty")}</p>
                <div className="mt-4 flex justify-center">
                  <KcGhostButton onClick={() => setSurface("chat")}>
                    <Play size={13} />
                    {t("demo.run")}
                  </KcGhostButton>
                </div>
              </KcCard>
            ) : filtered.length === 0 ? (
              <div className="mt-10 text-center">
                <Search size={22} className="mx-auto text-fg-3" />
                <p className="mt-2.5 text-[12.5px] text-fg-3">{t("outline.empty")}</p>
              </div>
            ) : (
              <div role="list" aria-label={t("trajectory.timeline")}>
                {filtered.map((ev, i) => (
                  <div
                    key={ev.id}
                    role="listitem"
                    className={cn(recentIds.has(ev.id) && "animate-rise")}
                  >
                    <EventRow
                      ev={ev}
                      isFirst={i === 0}
                      isLast={i === filtered.length - 1}
                      collapsed={collapsedIds.has(ev.id)}
                      onToggle={() => toggleEvent(ev.id)}
                    />
                  </div>
                ))}
              </div>
            )}
          </div>
        </section>
      </div>
    </div>
  );
}
