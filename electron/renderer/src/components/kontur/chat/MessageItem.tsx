"use client";

import { useState } from "react";
import { AlertTriangle, Bot, Brain, Check, ChevronDown, Copy, Paperclip, Pencil, RefreshCw, Sparkles, Trash2, X, Zap } from "lucide-react";
import type { Message } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useModel } from "@/lib/kontur/useModel";
import { regenerate } from "@/lib/kontur/scenario";
import { conversations } from "@/lib/kontur/backend";
import { isServerId } from "@/lib/kontur/sync";
import { Markdown } from "./Markdown";
import { ToolCallItem } from "./ToolCallItem";
import { MessageBlocks } from "./blocks";

function UserMessage({ message }: { message: Message }) {
  const t = useT();
  const [copied, setCopied] = useState(false);
  const runnerBusy = useKontur((s) => s.runner.status === "running" || s.runner.status === "awaiting-approval");

  return (
    <div className="flex animate-msg-in justify-end py-2">
      <div className="flex max-w-[min(620px,92%)] flex-col items-end gap-1">
        {message.attachments && message.attachments.length > 0 && (
          <div className="flex flex-wrap justify-end gap-1.5">
            {message.attachments.map((a) => (
              <span
                key={a.id}
                className="flex items-center gap-1.5 rounded-sm border border-line-faint bg-surface-1 px-2 py-1 text-[11px] text-fg-2"
              >
                <Paperclip size={10} className="text-fg-3" />
                {a.name}
                <span className="font-mono text-[9.5px] text-fg-3">{a.sizeKb}KB</span>
              </span>
            ))}
          </div>
        )}
        <div className="group relative overflow-hidden rounded-lg rounded-br-sm border border-line-faint bg-surface-2 px-3.5 py-2.5 transition-colors duration-150 hover:border-line hover:bg-surface-hover/50">
          <span
            className="absolute inset-y-0 left-0 w-[2px] origin-top scale-y-0 rounded-full bg-accent transition-transform duration-200 group-hover:scale-y-100"
            aria-hidden
          />
          <p className="whitespace-pre-wrap text-[13px] leading-[1.6] text-fg-1">
            {message.content}
          </p>
          <div className="absolute -left-[74px] top-1 flex items-center gap-0.5 opacity-0 transition-opacity duration-100 group-hover:opacity-100">
            <button
              type="button"
              onClick={() => {
                void navigator.clipboard?.writeText(message.content);
                setCopied(true);
                setTimeout(() => setCopied(false), 1200);
              }}
              className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
              aria-label={t("common.copy")}
              title={t("common.copy")}
            >
              {copied ? <Check size={11} className="text-success" /> : <Copy size={11} />}
            </button>
            <button
              type="button"
              disabled={runnerBusy}
              onClick={() => useKontur.getState().setDraft(message.content)}
              className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1 disabled:opacity-40"
              aria-label={t("common.edit")}
              title={t("common.edit")}
            >
              <Pencil size={11} />
            </button>
          </div>
        </div>
        <span className="pr-1 text-[10px] text-fg-3">
          {new Date(message.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}
        </span>
      </div>
    </div>
  );
}

function ReasoningBlock({ text, streaming }: { text: string; streaming?: boolean }) {
  const t = useT();
  // Collapsed by default: the reasoning trace is context, not the answer. It stays
  // available for anyone who wants to see how the model got there.
  const [open, setOpen] = useState(false);

  return (
    <div className="animate-rise mb-2 overflow-hidden rounded-md border border-line-faint bg-surface-1">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 px-2.5 py-1.5 text-left transition-colors hover:bg-surface-hover"
      >
        <Brain size={12} className={`shrink-0 text-accent ${streaming ? "animate-pulse" : ""}`} aria-hidden />
        <span className="shrink-0 text-[11px] font-medium text-fg-2">{t("chat.reasoning")}</span>
        <span className="min-w-0 flex-1" />
        <ChevronDown
          size={12}
          className={`shrink-0 text-fg-3 transition-transform duration-150 ${open ? "rotate-180" : ""}`}
          aria-hidden
        />
      </button>
      {open && (
        <div className="animate-fade border-t border-line-faint p-2">
          <div className="max-h-[280px] overflow-auto whitespace-pre-wrap rounded-sm bg-sunken p-2.5 text-[11.5px] leading-[1.6] text-fg-2">
            {text}
          </div>
        </div>
      )}
    </div>
  );
}

function AssistantMessage({ message }: { message: Message }) {
  const t = useT();
  const [copied, setCopied] = useState(false);
  const runnerBusy = useKontur((s) => s.runner.status === "running" || s.runner.status === "awaiting-approval");
  const deleteMessage = useKontur((s) => s.deleteMessage);
  const model = useModel(message.modelId ?? "");

  return (
    <div className="group/msg animate-msg-in py-2">
      {/* header line */}
      <div className="mb-1.5 flex items-center gap-2">
        <span className="flex h-[18px] w-[18px] items-center justify-center rounded-xs bg-accent-soft text-accent">
          {message.streaming ? (
            <Sparkles size={10} className="animate-pulse-dot" />
          ) : (
            <Bot size={10} />
          )}
        </span>
        <span className="text-[11px] font-semibold uppercase tracking-[0.06em] text-fg-2">
          {model?.name ?? "Assistant"}
        </span>
        {message.live && (
          <span
            className="flex items-center gap-1 rounded-full border border-accent-border bg-accent-soft px-1.5 py-px font-mono text-[9px] font-semibold uppercase tracking-[0.08em] text-accent"
            title={t("chat.live.title")}
          >
            <span className="h-1 w-1 rounded-full bg-accent" aria-hidden />
            {t("chat.live.badge")}
          </span>
        )}
        {message.skills && message.skills.length > 0 && (
          <span
            className="animate-rise flex items-center gap-1 rounded-full border border-line-faint bg-sunken px-1.5 py-px font-mono text-[9px] tracking-[0.04em] text-fg-3"
            title={t("chat.skills.tooltip")}
          >
            <Zap size={9} className="shrink-0 text-warning" aria-hidden />
            {message.skills.join(" · ")}
          </span>
        )}
        {message.streaming && (
          <span className="text-[11px] text-fg-3">{t("chat.thinking")}</span>
        )}
        {message.usage && (
          <span className="ml-auto font-mono text-[10px] text-fg-3 opacity-0 transition-opacity group-hover/msg:opacity-100">
            {message.usage.inputTokens.toLocaleString("en-US")} in ·{" "}
            {message.usage.outputTokens.toLocaleString("en-US")} out ·{" "}
            {(message.usage.ms / 1000).toFixed(0)}s
          </span>
        )}
      </div>

      {/* reasoning / chain-of-thought — collapsed by default, streamed live from the provider */}
      {message.reasoning && message.reasoning.trim().length > 0 && (
        <ReasoningBlock text={message.reasoning} streaming={message.streaming} />
      )}

      {/* content */}
      {message.content ? (
        <div className={message.streaming ? "kc-caret" : undefined}>
          <Markdown text={message.content} />
        </div>
      ) : (
        message.streaming && (
          <div className="flex items-center gap-2 py-1">
            <span className="flex items-center gap-1">
              <span className="h-1.5 w-1.5 animate-pulse-dot rounded-full bg-accent" />
              <span className="h-1.5 w-1.5 animate-pulse-dot rounded-full bg-accent [animation-delay:200ms]" />
              <span className="h-1.5 w-1.5 animate-pulse-dot rounded-full bg-accent [animation-delay:400ms]" />
            </span>
            <span className="kc-shimmer h-1 w-24 rounded-full" aria-hidden />
          </div>
        )
      )}

      {/* run failure — surfaced inline so a failed send never reads as an empty reply */}
      {message.error && (
        <div className="mt-1.5 flex items-start gap-2 rounded-md border border-error/40 bg-error-soft px-3 py-2">
          <AlertTriangle size={13} className="mt-0.5 shrink-0 text-error" aria-hidden />
          <div className="min-w-0 flex-1">
            <p className="whitespace-pre-wrap text-[12px] leading-[1.5] text-error">{message.error}</p>
            <button
              type="button"
              onClick={regenerate}
              disabled={runnerBusy}
              className="kc-focus-ring mt-1.5 flex h-6 items-center gap-1 rounded-xs border border-error/40 px-2 text-[11px] font-medium text-error transition-colors hover:bg-error-soft disabled:opacity-40"
              aria-label={t("chat.retry")}
              title={t("chat.retry")}
            >
              <RefreshCw size={11} />
              {t("chat.retry")}
            </button>
          </div>
        </div>
      )}

      {/* blocks */}
      {message.blocks && message.blocks.length > 0 && (
        <MessageBlocks blocks={message.blocks} />
      )}

      {/* tool calls */}
      {message.toolCalls.length > 0 && (
        <div className="mt-2 flex flex-col gap-1.5">
          {message.toolCalls.map((call) => (
            <ToolCallItem key={call.id} call={call} />
          ))}
        </div>
      )}

      {/* hover actions */}
      {!message.streaming && (
        <div className="mt-1 flex items-center gap-0.5 opacity-0 transition-opacity duration-100 group-hover/msg:opacity-100">
          <button
            type="button"
            onClick={() => {
              void navigator.clipboard?.writeText(message.content);
              setCopied(true);
              setTimeout(() => setCopied(false), 1200);
            }}
            className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
            aria-label={t("common.copy")}
            title={t("common.copy")}
          >
            {copied ? <Check size={11} className="text-success" /> : <Copy size={11} />}
          </button>
          <button
            type="button"
            onClick={regenerate}
            disabled={runnerBusy}
            className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1 disabled:opacity-40"
            aria-label={t("chat.regenerate")}
            title={t("chat.regenerate")}
          >
            <RefreshCw size={11} />
          </button>
          <button
            type="button"
            onClick={() => {
              // Optimistic local removal; also drop it on the server so a
              // session reload doesn't resurrect the message.
              if (isServerId(message.id)) {
                void conversations.deleteMessage(message.id).catch(() => undefined);
              }
              deleteMessage(message.id);
            }}
            className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-error-soft hover:text-error"
            aria-label={t("common.delete")}
            title={t("common.delete")}
          >
            <Trash2 size={11} />
          </button>
        </div>
      )}
    </div>
  );
}

export function MessageItem({ message }: { message: Message }) {
  if (message.folded) return null;
  return message.role === "user" ? <UserMessage message={message} /> : <AssistantMessage message={message} />;
}

export function FoldedNotice({ count }: { count: number }) {
  const t = useT();
  return (
    <div className="flex items-center gap-2 rounded-md border border-dashed border-line px-3 py-2 text-[11.5px] text-fg-3">
      <X size={0} className="hidden" aria-hidden />
      <span>{t("chat.folded", count)}</span>
    </div>
  );
}
