"use client";

import {
  CircleCheck,
  FileText,
  FlaskConical,
  GitCompare,
  ListChecks,
  Play,
  ShieldAlert,
  Terminal,
  Wrench,
} from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { sendMessage } from "@/lib/kontur/scenario";
import { SKILLS } from "@/lib/kontur/data";
import type { AgentMode } from "@/lib/kontur/types";
import { KcCard, KcPrimaryButton, Overline, StatusDot } from "@/components/kontur/ui";
import { Switch } from "@/components/ui/switch";

/* ============================================================
   WORKFLOWS — automation hub: runnable recipes, the skills
   library, and how agent runs work. One click sets the agent
   mode, sends the recipe prompt and follows the run in Chat.
   ============================================================ */

interface Recipe {
  id: string;
  icon: React.ElementType;
  mode: AgentMode;
  nameKey: string;
  descKey: string;
  stepKeys: string[];
  prompt: string;
}

const RECIPES: Recipe[] = [
  {
    id: "fix-test",
    icon: FlaskConical,
    mode: "build",
    nameKey: "workflows.recipe.fixTest.name",
    descKey: "workflows.recipe.fixTest.desc",
    stepKeys: [
      "workflows.recipe.fixTest.s1",
      "workflows.recipe.fixTest.s2",
      "workflows.recipe.fixTest.s3",
      "workflows.recipe.fixTest.s4",
    ],
    prompt:
      "Run the full agent loop to fix the failing test in the AuthFlow workspace: reproduce it, diagnose the root cause, apply the minimal fix, then verify with build and tests.",
  },
  {
    id: "draft-readme",
    icon: FileText,
    mode: "plan",
    nameKey: "workflows.recipe.readme.name",
    descKey: "workflows.recipe.readme.desc",
    stepKeys: [
      "workflows.recipe.readme.s1",
      "workflows.recipe.readme.s2",
      "workflows.recipe.readme.s3",
      "workflows.recipe.readme.s4",
    ],
    prompt:
      "Draft a README.md for the AuthFlow solution: inspect the four projects, outline structure, build and test instructions, then review for accuracy.",
  },
  {
    id: "refactor-service",
    icon: Wrench,
    mode: "build",
    nameKey: "workflows.recipe.refactor.name",
    descKey: "workflows.recipe.refactor.desc",
    stepKeys: [
      "workflows.recipe.refactor.s1",
      "workflows.recipe.refactor.s2",
      "workflows.recipe.refactor.s3",
      "workflows.recipe.refactor.s4",
    ],
    prompt:
      "Refactor TokenService for clarity: map its callers, propose the refactor plan, apply edits with diff approvals, and keep tests green.",
  },
  {
    id: "review-changes",
    icon: GitCompare,
    mode: "plan",
    nameKey: "workflows.recipe.review.name",
    descKey: "workflows.recipe.review.desc",
    stepKeys: [
      "workflows.recipe.review.s1",
      "workflows.recipe.review.s2",
      "workflows.recipe.review.s3",
      "workflows.recipe.review.s4",
    ],
    prompt:
      "Review the recent changes in the workspace: read the git diff, inspect the touched files, and report findings with severity levels.",
  },
];

const RUN_STEPS: { icon: React.ElementType; titleKey: string; captionKey: string }[] = [
  { icon: ListChecks, titleKey: "workflows.runs.s1.title", captionKey: "workflows.runs.s1.caption" },
  { icon: Terminal, titleKey: "workflows.runs.s2.title", captionKey: "workflows.runs.s2.caption" },
  { icon: ShieldAlert, titleKey: "workflows.runs.s3.title", captionKey: "workflows.runs.s3.caption" },
  { icon: CircleCheck, titleKey: "workflows.runs.s4.title", captionKey: "workflows.runs.s4.caption" },
];

function RecipeCard({ recipe, busy, onRun }: { recipe: Recipe; busy: boolean; onRun: (recipe: Recipe) => void }) {
  const t = useT();
  const Icon = recipe.icon;
  return (
    <KcCard as="article" className="flex flex-col p-4 transition-colors duration-100 hover:border-line">
      <header className="flex items-start gap-2.5">
        <span
          className="flex h-7 w-7 shrink-0 items-center justify-center rounded-sm border border-line bg-surface-hover text-fg-2"
          aria-hidden
        >
          <Icon size={15} />
        </span>
        <div className="min-w-0">
          <h3 className="text-[13.5px] font-semibold leading-tight text-fg-1">{t(recipe.nameKey)}</h3>
          <p className="mt-1 text-[12px] leading-[1.45] text-fg-2">{t(recipe.descKey)}</p>
        </div>
      </header>

      <div className="mt-3 flex flex-wrap gap-1">
        {recipe.stepKeys.map((key, i) => (
          <span
            key={key}
            className="inline-flex items-baseline gap-1 rounded-full border border-line-faint bg-sunken px-2 py-0.5"
          >
            <span className="font-mono text-[10px] leading-none text-fg-3">{i + 1}</span>
            <span className="text-[11px] leading-[1.2] text-fg-2">{t(key)}</span>
          </span>
        ))}
      </div>

      <footer className="mt-auto flex flex-wrap items-center justify-between gap-2 border-t border-line-faint pt-3">
        <span className="font-mono text-[10.5px] text-fg-3">
          {t("workflows.modeBadge", t(`agent.mode.${recipe.mode}`))}
        </span>
        <KcPrimaryButton onClick={() => onRun(recipe)} title={busy ? t("workflows.busy.description") : undefined}>
          <Play size={12} />
          {t("workflows.run")}
        </KcPrimaryButton>
      </footer>
    </KcCard>
  );
}

function SkillsLibrary() {
  const t = useT();
  /* skill toggles are store-backed (persisted) so the choice actually gates
     the agent's per-run skill auto-selection — see scenario.selectSkillsForRun.
     Shared with the Settings → Agent skills list via ui.skillsEnabled. */
  const skillsEnabled = useKontur((s) => s.ui.skillsEnabled);
  const setUi = useKontur((s) => s.setUi);
  const setSkill = (id: string, on: boolean) =>
    setUi({ skillsEnabled: { ...skillsEnabled, [id]: on } });
  return (
    <section aria-label={t("workflows.skills.overline")} className="animate-rise">
      <div className="mb-2.5 flex items-center gap-2.5">
        <Overline>{t("workflows.skills.overline")}</Overline>
        <span className="rounded-full border border-line bg-sunken px-1.5 py-px font-mono text-[10px] leading-none text-fg-3">
          {SKILLS.length}
        </span>
      </div>
      <p className="mb-2.5 text-[11.5px] text-fg-3">{t("workflows.skills.hint")}</p>
      <KcCard>
        <ul className="divide-y divide-line-faint">
          {SKILLS.map((skill) => (
            <li key={skill.id} className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-2.5">
              <span className="shrink-0 rounded-sm border border-line-faint bg-sunken px-1.5 py-0.5 font-mono text-[11px] leading-none text-fg-1">
                {skill.name}
              </span>
              <span className="min-w-0 flex-1 text-[12px] leading-[1.45] text-fg-2">{skill.description}</span>
              <Switch
                checked={skillsEnabled?.[skill.id] ?? skill.enabled}
                onCheckedChange={(v) => setSkill(skill.id, v)}
                aria-label={skill.name}
              />
            </li>
          ))}
        </ul>
      </KcCard>
    </section>
  );
}

function RunsStrip() {
  const t = useT();
  return (
    <section aria-label={t("workflows.runs.title")} className="animate-rise">
      <Overline>{t("workflows.runs.title")}</Overline>
      <div className="mt-2.5 grid grid-cols-1 gap-1.5 sm:grid-cols-4">
        {RUN_STEPS.map((step, i) => {
          const Icon = step.icon;
          return (
            <KcCard key={step.titleKey} className="p-3">
              <div className="flex items-center justify-between">
                <Icon size={16} className="text-fg-3" aria-hidden />
                <span className="font-mono text-[10px] leading-none text-fg-3">
                  {String(i + 1).padStart(2, "0")}
                </span>
              </div>
              <p className="mt-2 text-[11px] leading-[1.45] text-fg-2">
                <span className="font-medium text-fg-1">{t(step.titleKey)}</span>
                <span aria-hidden> — </span>
                {t(step.captionKey)}
              </p>
            </KcCard>
          );
        })}
      </div>
    </section>
  );
}

export default function WorkflowsView() {
  const t = useT();
  const runner = useKontur((s) => s.runner);
  const setWorkMode = useKontur((s) => s.setWorkMode);
  const setWorkMethod = useKontur((s) => s.setWorkMethod);
  const setSurface = useKontur((s) => s.setSurface);
  const pushToast = useKontur((s) => s.pushToast);

  const busy = runner.status === "running" || runner.status === "awaiting-approval";

  const runRecipe = (recipe: Recipe) => {
    if (busy) {
      pushToast(t("workflows.busy.title"), t("workflows.busy.description"), "destructive");
      return;
    }
    /* apply through the work-mode axes so the composer/header stay in sync
       (a recipe always runs the agent, i.e. Cowork) */
    setWorkMode("cowork");
    setWorkMethod(recipe.mode === "plan" ? "plan" : "ask");
    sendMessage(recipe.prompt);
    setSurface("chat");
    pushToast(t(recipe.nameKey), t("workflows.started.description", t(`agent.mode.${recipe.mode}`)));
  };

  return (
    <div className="h-full w-full overflow-y-auto">
      <div className="mx-auto w-full max-w-[940px] space-y-6 px-4 py-4 sm:px-6 sm:py-5">
        <header>
          <Overline>{t("workflows.overline")}</Overline>
          <h1 className="mt-1 text-[18px] font-semibold leading-tight text-fg-1">{t("workflows.title")}</h1>
          <p className="mt-1 max-w-2xl text-[12.5px] leading-[1.5] text-fg-2">{t("workflows.subtitle")}</p>
        </header>

        {/* Section A — workflow recipes */}
        <section aria-label={t("workflows.recipes.overline")} className="animate-rise">
          <div className="mb-2.5 flex flex-wrap items-center gap-2.5">
            <Overline>{t("workflows.recipes.overline")}</Overline>
            {busy && (
              <span className="inline-flex items-center gap-1.5 rounded-full border border-warning/40 bg-warning-soft px-2 py-0.5 font-mono text-[10.5px] text-warning">
                <StatusDot color="warning" pulse size={6} />
                {t("workflows.busy.title")}
              </span>
            )}
          </div>
          <p className="mb-2.5 text-[11.5px] text-fg-3">{t("workflows.recipes.hint")}</p>
          <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
            {RECIPES.map((recipe) => (
              <RecipeCard key={recipe.id} recipe={recipe} busy={busy} onRun={runRecipe} />
            ))}
          </div>
        </section>

        {/* Section B — skills library */}
        <SkillsLibrary />

        {/* Section C — how runs work */}
        <RunsStrip />
      </div>
    </div>
  );
}
