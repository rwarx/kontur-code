"use client";

import { useMemo } from "react";
import { Bot, Check, Cpu, Play, Target, Waypoints } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useModel } from "@/lib/kontur/useModel";
import type { GoalStatus, RunnerState } from "@/lib/kontur/types";
import { KcCard, KcGhostButton, KcPrimaryButton, Overline, StatusDot } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";
import { relativeTime, runnerStatusText, type TFn } from "./helpers";

/* ============================================================
   TASKS — goals & completion contracts for the current run.
   ============================================================ */

const GOAL_PILL: Record<GoalStatus, string> = {
  planning: "border-line bg-sunken text-fg-3",
  running: "border-warning/40 bg-warning-soft text-warning",
  waiting: "border-warning/40 bg-warning-soft text-warning",
  verifying: "border-accent-border bg-accent-soft text-accent",
  completed: "border-success/40 bg-success-soft text-success",
  failed: "border-error/40 bg-error-soft text-error",
};

function StatusPill({ status, t }: { status: GoalStatus; t: TFn }) {
  return (
    <span
      className={cn(
        "inline-flex items-center rounded-xs border px-2 py-0.5 font-mono text-[10px] uppercase tracking-wider",
        GOAL_PILL[status],
      )}
    >
      {t(`goal.status.${status}`)}
    </span>
  );
}

function runDot(status: RunnerState["status"]): { color: "success" | "warning" | "error" | "muted"; pulse: boolean } {
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

export default function TasksView() {
  const t = useT();
  const session = useKontur((s) => s.sessions.find((x) => x.id === s.currentSessionId));
  const goals = useKontur((s) => s.goals);
  const runner = useKontur((s) => s.runner);
  const selectedModelId = useKontur((s) => s.ui.selectedModelId);
  const setSurface = useKontur((s) => s.setSurface);
  const updateGoalCriteria = useKontur((s) => s.updateGoalCriteria);

  const sessionGoals = useMemo(
    () => goals.filter((g) => session?.goalIds.includes(g.id)),
    [goals, session],
  );

  const eventsCount = session?.events.length ?? 0;
  const model = useModel(selectedModelId);
  const dot = runDot(runner.status);

  if (sessionGoals.length === 0) {
    return (
      <div className="h-full w-full overflow-y-auto">
        <div className="flex h-full min-h-[340px] flex-col items-center justify-center gap-2.5 p-6 text-center">
          <Bot size={28} className="text-fg-3" />
          <p className="text-[13.5px] font-medium text-fg-1">{t("tasks.empty.title")}</p>
          <p className="max-w-xs text-[12px] leading-[1.5] text-fg-3">{t("tasks.empty.hint")}</p>
          <KcPrimaryButton className="mt-2" onClick={() => setSurface("chat")}>
            <Play size={13} />
            {t("demo.run")}
          </KcPrimaryButton>
        </div>
      </div>
    );
  }

  return (
    <div className="h-full w-full overflow-y-auto">
      <div className="mx-auto w-full max-w-3xl space-y-3 p-4">
        {/* run state strip */}
        <KcCard className="flex flex-wrap items-center gap-x-4 gap-y-2 p-3">
          <div className="flex min-w-0 items-center gap-2.5">
            <StatusDot color={dot.color} pulse={dot.pulse} size={8} />
            <span className="min-w-0 truncate text-[13px] font-medium text-fg-1">
              {runnerStatusText(t, runner.status, runner.label)}
            </span>
          </div>
          <div className="ml-auto flex items-center gap-3.5 pl-2">
            {model && (
              <span className="flex min-w-0 items-center gap-1.5 text-[11.5px] text-fg-2">
                <Cpu size={12} className="shrink-0 text-fg-3" />
                <span className="truncate">{model.name}</span>
              </span>
            )}
            <span className="shrink-0 font-mono text-[11px] text-fg-3">
              {t("tasks.steps", eventsCount)}
            </span>
          </div>
        </KcCard>

        {/* goals */}
        {sessionGoals.map((goal) => {
          const done = goal.criteria.filter((c) => c.done).length;
          const total = goal.criteria.length;
          const pct = total > 0 ? Math.round((done / total) * 100) : 0;
          return (
            <KcCard as="article" key={goal.id} className="animate-rise p-4">
              <header className="flex items-start gap-3">
                <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-sm border border-line-faint bg-sunken text-fg-2">
                  <Target size={15} />
                </span>
                <div className="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-1.5">
                  <h3 className="min-w-0 truncate text-[14px] font-semibold text-fg-1">{goal.title}</h3>
                  <StatusPill status={goal.status} t={t} />
                  <span className="ml-auto shrink-0 font-mono text-[10.5px] text-fg-3">
                    {relativeTime(goal.createdAt, t)}
                  </span>
                </div>
              </header>

              <div className="mt-3.5">
                <div className="mb-1.5 flex items-center justify-between">
                  <Overline>{t("tasks.criteria")}</Overline>
                  <span className="font-mono text-[10.5px] text-fg-3">
                    {t("tasks.progress", done, total)}
                  </span>
                </div>
                <div className="h-1 overflow-hidden rounded-full bg-sunken" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
                  <div
                    className="h-full rounded-full bg-success transition-all duration-150"
                    style={{ width: `${pct}%` }}
                  />
                </div>

                <ul className="mt-2.5 space-y-0.5">
                  {goal.criteria.map((item) => (
                    <li key={item.id}>
                      <button
                        type="button"
                        role="checkbox"
                        aria-checked={item.done}
                        onClick={() => updateGoalCriteria(goal.id, item.id, !item.done)}
                        className="kc-focus-ring flex w-full items-center gap-2.5 rounded-sm px-1.5 py-1.5 text-left transition-colors duration-100 hover:bg-surface-hover"
                      >
                        <span
                          className={cn(
                            "flex h-[16px] w-[16px] shrink-0 items-center justify-center rounded-xs border transition-colors duration-100",
                            item.done
                              ? "border-success/40 bg-success-soft text-success"
                              : "border-line text-transparent",
                          )}
                        >
                          <Check size={11} strokeWidth={3} />
                        </span>
                        <span className={cn("text-[12.5px] leading-[1.45]", item.done ? "text-fg-2" : "text-fg-1")}>
                          {item.text}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              </div>

              <div className="mt-3 flex items-center justify-end border-t border-line-faint pt-3">
                <KcGhostButton onClick={() => setSurface("trajectory")}>
                  <Waypoints size={13} />
                  {t("tasks.viewTrajectory")}
                </KcGhostButton>
              </div>
            </KcCard>
          );
        })}
      </div>
    </div>
  );
}
