"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowDown, CalendarDays, FileSearch, FlaskConical, FolderOpen, ListChecks, Map as MapIcon, Plus } from "lucide-react";
import type { Message } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { sendMessage } from "@/lib/kontur/scenario";
import { isServerMode, openWorkspaceDialog } from "@/lib/kontur/sync";
import { MessageItem, FoldedNotice } from "./MessageItem";
import { ApprovalGate } from "./ApprovalGate";
import { Composer } from "./Composer";

const SUGGESTIONS = [
  { icon: FileSearch, key: "chat.suggest.tokens", text: "Find every refresh-token usage and explain the flow" },
  { icon: FlaskConical, key: "chat.suggest.tests", text: "Write tests for TokenService" },
  { icon: ListChecks, key: "chat.suggest.plan", text: "Draft a migration plan for the token store" },
  { icon: MapIcon, key: "chat.suggest.explain", text: "Explain the architecture of AuthFlow" },
];

const TIME_GAP_MS = 10 * 60 * 1000; /* dividers appear at ≥10-minute gaps */

interface DividerInfo {
  ts: number;
  day: boolean;
}

function startOfDay(ts: number): number {
  const d = new Date(ts);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

/** Centered hairline divider with a time chip — rhythm for long transcripts. */
function TimeDivider({ ts }: { ts: number }) {
  const label = new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return (
    <div className="animate-fade my-3 flex items-center gap-3" role="separator" aria-label={label}>
      <span className="h-px flex-1 bg-line-faint" aria-hidden />
      <span className="rounded-full border border-line-faint bg-surface-1 px-2 py-px font-mono text-[9.5px] tracking-[0.06em] text-fg-3">
        {label}
      </span>
      <span className="h-px flex-1 bg-line-faint" aria-hidden />
    </div>
  );
}

/** Calendar-day boundary — stronger chip with the localized date. */
function DayDivider({ ts, label }: { ts: number; label: string }) {
  const date = new Date(ts).toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" });
  return (
    <div className="animate-fade my-4 flex items-center gap-3" role="separator" aria-label={label + " · " + date}>
      <span className="h-px flex-1 bg-line" aria-hidden />
      <span className="flex items-center gap-1.5 rounded-full border border-line bg-surface-2 px-2.5 py-0.5 text-[10px] font-medium text-fg-2">
        <CalendarDays size={10} className="text-accent" aria-hidden />
        <span className="font-mono tracking-[0.04em]">{date}</span>
      </span>
      <span className="h-px flex-1 bg-line" aria-hidden />
    </div>
  );
}

function StartScreen() {
  const t = useT();
  /* project comes from the shared store.workspaceRoot (item 5 single source of
     truth) — populated by syncWorkspaceFiles on boot and by openWorkspaceDialog. */
  const project = useKontur((s) => s.workspaceRoot);
  const workMode = useKontur((s) => s.ui.workMode);
  const pushToast = useKontur((s) => s.pushToast);
  const projectName = project ? (project.split(/[/\\]/).filter(Boolean).pop() ?? project) : null;
  const greeting = (() => {
    const h = new Date().getHours();
    if (h < 12) return t("chat.greeting.morning");
    if (h < 18) return t("chat.greeting.afternoon");
    return t("chat.greeting.evening");
  })();
  const openWorkspace = () => {
    if (!isServerMode() || !window.kontur) {
      pushToast(t("settings.workspace.toast"));
      return;
    }
    void openWorkspaceDialog()
      .then((root) => { if (root) pushToast(t("palette.openWorkspace"), root); })
      .catch((err) =>
        pushToast(t("palette.openWorkspace"), err instanceof Error ? err.message : undefined, "destructive"),
      );
  };
  return (
    <div className="relative flex h-full flex-col items-center justify-center overflow-hidden px-6">
      <div className="relative z-10 mb-6 flex flex-col items-center text-center">
        {/* spark mark */}
        <span className="mb-4 animate-rise text-accent" aria-hidden>
          <svg width="38" height="38" viewBox="0 0 24 24" fill="currentColor">
            <path d="M12 1.5c.35 5.4 4.7 9.75 10.5 10.5-5.8.75-10.15 5.1-10.5 10.5-.35-5.4-4.7-9.75-10.5-10.5C7.3 11.25 11.65 6.9 12 1.5Z" />
          </svg>
        </span>
        <h1 className="animate-rise text-[26px] font-semibold tracking-[-0.015em] text-fg-1">{greeting}</h1>
        <p className="mt-1.5 animate-rise text-[13px] text-fg-3">{t("chat.empty.subtitle")}</p>
      </div>
      {/* project button — only in code mode; chat & cowork need no project */}
      {isServerMode() && workMode === "code" && (
        <button
          type="button"
          onClick={openWorkspace}
          className="kc-focus-ring relative z-10 mb-3 flex h-7 animate-rise items-center gap-1.5 rounded-full border border-line bg-surface-1 px-3 text-[11.5px] text-fg-2 transition-colors hover:border-line-strong hover:bg-surface-2 hover:text-fg-1"
          title={project ?? t("palette.openWorkspace")}
        >
          <FolderOpen size={12} className="text-fg-3" />
          <span className="max-w-[240px] truncate font-mono">{projectName ?? t("sidebar.openFolder")}</span>
        </button>
      )}
      <div className="relative z-10 w-full max-w-[720px] animate-rise">
        <Composer />
      </div>
      <div className="relative z-10 mt-1 flex w-full max-w-[720px] flex-wrap justify-center gap-1.5 px-3">
        {SUGGESTIONS.map((s, i) => {
          const Icon = s.icon;
          return (
            <button
              key={s.key}
              type="button"
              onClick={() => sendMessage(s.text)}
              style={{ animationDelay: `${80 + i * 40}ms` }}
              className="kc-focus-ring kc-lift group flex animate-rise items-center gap-1.5 rounded-full border border-line-faint bg-surface-1 px-3 py-1.5 text-[11.5px] text-fg-2 hover:border-line hover:bg-surface-2 hover:text-fg-1"
              title={s.text}
            >
              <Icon size={12} className="text-fg-3 transition-colors group-hover:text-accent" />
              {t(s.key)}
            </button>
          );
        })}
      </div>
    </div>
  );
}

export function ChatView() {
  const t = useT();
  const session = useKontur((s) => s.sessions.find((x) => x.id === s.currentSessionId));
  const approval = useKontur((s) => s.approval);
  const runnerStatus = useKontur((s) => s.runner.status);
  const newSession = useKontur((s) => s.newSession);
  const messages = session?.messages ?? [];
  const scrollRef = useRef<HTMLDivElement>(null);
  const [scrolledAway, setScrolledAway] = useState(false);
  const [scrolledTop, setScrolledTop] = useState(false);

  const lastSignature = useMemo(() => {
    const last = messages[messages.length - 1];
    if (!last) return 0;
    return messages.length + last.content.length + last.toolCalls.length + (last.blocks?.length ?? 0);
  }, [messages]);

  const generating = runnerStatus === "running" || runnerStatus === "awaiting-approval";

  /* auto-scroll when near bottom */
  useEffect(() => {
    const el = scrollRef.current;
    if (!el || scrolledAway || !useKontur.getState().ui.autoscroll) return;
    el.scrollTop = el.scrollHeight;
  }, [lastSignature, scrolledAway, approval]);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    el.scrollTop = el.scrollHeight;
  }, [session?.id]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    setScrolledAway(el.scrollHeight - el.scrollTop - el.clientHeight > 120);
    setScrolledTop(el.scrollTop > 8);
  };

  const foldedCount = messages.filter((m) => m.folded).length;

  /* time-gap / calendar-day dividers between messages */
  const dividers = useMemo(() => {
    const map = new Map<string, DividerInfo>();
    for (let i = 1; i < messages.length; i++) {
      const prev = messages[i - 1].createdAt;
      const cur = messages[i].createdAt;
      if (startOfDay(cur) !== startOfDay(prev)) {
        map.set(messages[i].id, { ts: cur, day: true });
      } else if (cur - prev >= TIME_GAP_MS) {
        map.set(messages[i].id, { ts: cur, day: false });
      }
    }
    return map;
  }, [messages]);

  return (
    <div className="relative flex h-full flex-col">
      {/* new chat — relocated here from the global header corner (Turn 3). Lives
         in the chat's own top-right corner and stays wired to newSession(). */}
      <button
        type="button"
        onClick={() => newSession()}
        className="kc-focus-ring absolute right-3 top-3 z-20 flex h-7 w-7 items-center justify-center rounded-full border border-line bg-surface-2/85 text-fg-2 shadow-subtle backdrop-blur transition-colors hover:-translate-y-0.5 hover:border-line-strong hover:bg-surface-3 hover:text-fg-1"
        aria-label={t("work.newChat")}
        title={t("work.newChat")}
      >
        <Plus size={15} />
      </button>
      {/* transcript (own sub-wrapper so the bottom fade tracks the composer edge) */}
      {messages.length === 0 ? (
        <StartScreen />
      ) : (
        <>
      <div className="relative min-h-0 flex-1">
        <div
          ref={scrollRef}
          onScroll={onScroll}
          className="h-full overflow-y-auto"
          role="log"
          aria-label={t("nav.chat")}
        >
            <div className="mx-auto max-w-[820px] px-4 py-4 sm:px-6">
              {foldedCount > 0 && (
                <div className="mb-3">
                  <FoldedNotice count={foldedCount} />
                </div>
              )}
              {messages.map((m: Message) => {
                const d = dividers.get(m.id);
                return (
                  <div key={m.id}>
                    {d && (d.day ? <DayDivider ts={d.ts} label={t("chat.day.label")} /> : <TimeDivider ts={d.ts} />)}
                    <MessageItem message={m} />
                  </div>
                );
              })}
              <div className="h-2" />
            </div>
        </div>

        {/* bottom edge fade — reads as depth under the composer */}
        <div
          className={`pointer-events-none absolute inset-x-0 bottom-0 z-[5] h-6 bg-gradient-to-t from-app to-transparent transition-opacity duration-200 ${
            scrolledAway ? "opacity-100" : "opacity-0"
          }`}
          aria-hidden
        />
      </div>

      {/* scroll-position fade at the top edge of the transcript */}
      <div
        className={`pointer-events-none absolute inset-x-0 top-0 z-[5] h-6 bg-gradient-to-b from-app to-transparent transition-opacity duration-200 ${
          scrolledTop ? "opacity-100" : "opacity-0"
        }`}
        aria-hidden
      />

      {/* jump to latest */}
      {scrolledAway && messages.length > 0 && (
        <button
          type="button"
          onClick={() => {
            const el = scrollRef.current;
            if (el) el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
            setScrolledAway(false);
          }}
          className="animate-rise absolute bottom-[130px] left-1/2 z-10 flex h-7 -translate-x-1/2 items-center gap-1.5 rounded-full border border-line bg-surface-3 px-3 text-[11.5px] font-medium text-fg-2 shadow-overlay transition-all hover:-translate-y-0.5 hover:border-line-strong hover:text-fg-1 hover:shadow-subtle"
        >
          <ArrowDown size={11} />
          {t("chat.jumpToLatest")}
        </button>
      )}

      {/* approval gate + composer */}
      <div className="shrink-0">
        {approval && (
          <div className="mx-auto max-w-[820px] px-3 pb-2 pt-1 sm:px-4">
            <ApprovalGate approval={approval} />
          </div>
        )}
        <Composer />
      </div>
        </>
      )}
    </div>
  );
}
