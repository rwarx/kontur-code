"use client";

import type { MessageBlock } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import {
  Bookmark,
  Bot,
  Check,
  ChevronRight,
  Circle,
  ListChecks,
  Loader2,
  Target,
  XCircle,
} from "lucide-react";
import { Overline } from "@/components/kontur/ui";

function PlanCard({ block }: { block: Extract<MessageBlock, { type: "plan" }> }) {
  const t = useT();
  return (
    <div className="my-2 overflow-hidden rounded-md border border-line-faint bg-surface-1">
      <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2/60 px-3 py-2">
        <ListChecks size={13} className="text-accent" />
        <Overline className="!text-fg-2">{t("plan.title")}</Overline>
        <span className="ml-auto font-mono text-[10px] text-fg-3">
          {block.steps.filter((s) => s.status === "done").length}/{block.steps.length}
        </span>
      </div>
      <div className="px-2 py-1.5">
        {block.steps.map((step, i) => (
          <div key={step.id} className="flex items-start gap-2 rounded-sm px-1.5 py-1">
            <span className="mt-[2px] w-4 shrink-0 text-right font-mono text-[10px] text-fg-3">
              {i + 1}
            </span>
            {step.status === "done" ? (
              <Check size={12} className="mt-[3px] shrink-0 text-success" />
            ) : step.status === "running" ? (
              <Loader2 size={12} className="mt-[3px] shrink-0 animate-spin-slow text-warning" />
            ) : step.status === "failed" ? (
              <XCircle size={12} className="mt-[3px] shrink-0 text-error" />
            ) : (
              <Circle size={12} className="mt-[3px] shrink-0 text-fg-3/60" />
            )}
            <span
              className={`text-[12.5px] leading-snug ${
                step.status === "done" ? "text-fg-2" : step.status === "running" ? "font-medium text-fg-1" : "text-fg-1"
              }`}
            >
              {step.title}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}

function ContractCard({ block }: { block: Extract<MessageBlock, { type: "contract" }> }) {
  const t = useT();
  const done = block.items.filter((i) => i.done).length;
  return (
    <div className="my-2 overflow-hidden rounded-md border border-line-faint bg-surface-1">
      <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2/60 px-3 py-2">
        <Target size={13} className="text-accent" />
        <Overline className="!text-fg-2">{block.title || t("contract.title")}</Overline>
        <span className="ml-auto font-mono text-[10px] text-fg-3">
          {done}/{block.items.length}
        </span>
      </div>
      <div className="px-3 py-2">
        <div className="mb-2 h-[3px] overflow-hidden rounded-full bg-sunken">
          <div
            className="h-full rounded-full bg-success transition-all duration-300"
            style={{ width: `${(done / block.items.length) * 100}%` }}
          />
        </div>
        <ul className="space-y-1">
          {block.items.map((item) => (
            <li key={item.id} className="flex items-center gap-2">
              <span
                className={`flex h-[15px] w-[15px] shrink-0 items-center justify-center rounded-[4px] border transition-colors ${
                  item.done ? "border-success/50 bg-success-soft text-success" : "border-line text-transparent"
                }`}
                aria-hidden
              >
                <Check size={9} strokeWidth={3} />
              </span>
              <span className={`text-[12.5px] ${item.done ? "text-fg-2" : "text-fg-1"}`}>
                {item.text}
              </span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function AgentTreeCard({ block }: { block: Extract<MessageBlock, { type: "agenttree" }> }) {
  const t = useT();
  return (
    <div className="my-2 overflow-hidden rounded-md border border-line-faint bg-surface-1">
      <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2/60 px-3 py-2">
        <Bot size={13} className="text-accent" />
        <Overline className="!text-fg-2">{t("agents.title")}</Overline>
        <span className="ml-auto font-mono text-[10px] text-fg-3">
          {block.agents.filter((a) => a.status === "done").length}/{block.agents.length + 1}
        </span>
      </div>
      <div className="px-3 py-2">
        {/* main agent */}
        <div className="flex items-center gap-2 py-1">
          <span className="flex h-5 w-5 items-center justify-center rounded-xs bg-accent-soft text-accent">
            <Bot size={11} />
          </span>
          <span className="text-[12.5px] font-semibold text-fg-1">{t("agents.main")}</span>
          <span className="ml-auto flex items-center gap-1 font-mono text-[10px] text-warning">
            <Loader2 size={9} className="animate-spin-slow" />
            run
          </span>
        </div>
        {/* subagents */}
        <div className="ml-[10px] border-l border-line pl-3">
          {block.agents.map((agent) => (
            <div key={agent.id} className="py-1">
              <div className="flex items-center gap-2">
                {agent.status === "done" ? (
                  <Check size={11} className="shrink-0 text-success" />
                ) : agent.status === "running" ? (
                  <Loader2 size={11} className="shrink-0 animate-spin-slow text-warning" />
                ) : (
                  <Circle size={11} className="shrink-0 text-fg-3/60" />
                )}
                <span className="text-[12px] font-medium text-fg-1">{agent.name}</span>
                <span className="text-[10.5px] text-fg-3">{agent.role}</span>
                {agent.tokens !== undefined && (
                  <span className="ml-auto font-mono text-[10px] text-fg-3">
                    {(agent.tokens / 1000).toFixed(1)}k tok
                  </span>
                )}
              </div>
              {agent.summary && (
                <p className="ml-[19px] mt-0.5 max-w-[480px] text-[11.5px] leading-snug text-fg-2">
                  {agent.summary}
                </p>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function CheckpointCard({ block }: { block: Extract<MessageBlock, { type: "checkpoint" }> }) {
  const t = useT();
  const checkpoints = useKontur((s) => s.checkpoints);
  const restoreCheckpoint = useKontur((s) => s.restoreCheckpoint);
  const cp = checkpoints.find((c) => c.label === block.label);

  return (
    <div className="my-2 flex items-center gap-2.5 rounded-md border border-line-faint bg-surface-1 px-3 py-2">
      <Bookmark size={12} className="shrink-0 text-warning" />
      <span className="text-[12px] text-fg-2">{t("checkpoint.created", block.label)}</span>
      <span className="truncate font-mono text-[10.5px] text-fg-3">{block.file}</span>
      {cp && (
        <button
          type="button"
          onClick={() => restoreCheckpoint(cp.id)}
          className="ml-auto flex h-6 shrink-0 items-center gap-1 rounded-xs border border-line bg-surface-2 px-2 text-[11px] font-medium text-fg-2 transition-colors hover:border-warning/50 hover:text-warning"
        >
          <ChevronRight size={10} />
          {t("checkpoint.restore")}
        </button>
      )}
    </div>
  );
}

function EventBlock({ block }: { block: Extract<MessageBlock, { type: "event" }> }) {
  return (
    <div
      className={`my-2 flex items-start gap-2.5 rounded-md border px-3 py-2 ${
        block.level === "error"
          ? "border-error/30 bg-error-soft"
          : block.level === "success"
            ? "border-success/30 bg-success-soft"
            : "border-line-faint bg-surface-1"
      }`}
    >
      <span className="text-[12px] font-medium text-fg-1">{block.title}</span>
      {block.detail && <span className="text-[11.5px] text-fg-2">{block.detail}</span>}
    </div>
  );
}

export function MessageBlocks({ blocks }: { blocks: MessageBlock[] }) {
  return (
    <>
      {blocks.map((block, i) => {
        switch (block.type) {
          case "plan":
            return <PlanCard key={i} block={block} />;
          case "contract":
            return <ContractCard key={i} block={block} />;
          case "agenttree":
            return <AgentTreeCard key={i} block={block} />;
          case "checkpoint":
            return <CheckpointCard key={i} block={block} />;
          case "event":
            return <EventBlock key={i} block={block} />;
          default:
            return null;
        }
      })}
    </>
  );
}
