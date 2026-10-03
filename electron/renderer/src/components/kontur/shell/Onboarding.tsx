"use client";

import { useState } from "react";
import { Check, FileCode2, MessageSquare, Users } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import type { WorkMode } from "@/lib/kontur/types";

/* First-run mode picker (default: chat, like Claude Code). All three modes
   hold chats; they differ in what they can do — chat is a plain LLM chat,
   cowork is a document/text/business agent, code folds in the whole dev
   workspace. Shown once, until ui.modeChosen is set. */
const MODES: { id: WorkMode; icon: React.ElementType }[] = [
  { id: "chat", icon: MessageSquare },
  { id: "cowork", icon: Users },
  { id: "code", icon: FileCode2 },
];

export function Onboarding() {
  const t = useT();
  const setWorkMode = useKontur((s) => s.setWorkMode);
  const setUi = useKontur((s) => s.setUi);
  const [selected, setSelected] = useState<WorkMode>("chat");

  const confirm = () => {
    setWorkMode(selected);
    setUi({ modeChosen: true });
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-app/95 p-6 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      aria-label={t("onboarding.title")}
    >
      <div className="w-full max-w-2xl animate-rise">
        <div className="mb-6 text-center">
          <h1 className="text-[22px] font-semibold tracking-tight text-fg-1">{t("onboarding.title")}</h1>
          <p className="mt-1.5 text-[13px] text-fg-3">{t("onboarding.subtitle")}</p>
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          {MODES.map(({ id, icon: Icon }) => {
            const active = selected === id;
            return (
              <button
                key={id}
                type="button"
                onClick={() => setSelected(id)}
                onDoubleClick={confirm}
                aria-pressed={active}
                className={`kc-focus-ring relative flex flex-col gap-2 rounded-lg border p-4 text-left transition-all duration-100 ${
                  active
                    ? "border-accent-border bg-accent-soft shadow-sm"
                    : "border-line bg-surface-1 hover:border-line-strong hover:bg-surface-2"
                }`}
              >
                <span
                  className={`flex h-9 w-9 items-center justify-center rounded-md ${
                    active ? "bg-accent text-on-accent" : "bg-surface-3 text-fg-2"
                  }`}
                >
                  <Icon size={17} />
                </span>
                <span className={`text-[14px] font-semibold ${active ? "text-accent" : "text-fg-1"}`}>
                  {t(`work.mode.${id}`)}
                </span>
                <span className="text-[11.5px] leading-snug text-fg-3">{t(`onboarding.${id}.desc`)}</span>
                {active && (
                  <span className="absolute right-2.5 top-2.5 flex h-4 w-4 items-center justify-center rounded-full bg-accent text-on-accent">
                    <Check size={11} />
                  </span>
                )}
              </button>
            );
          })}
        </div>

        <div className="mt-6 flex flex-col items-center gap-2">
          <button
            type="button"
            onClick={confirm}
            className="kc-focus-ring h-10 w-full max-w-xs rounded-md bg-accent text-[13px] font-semibold text-on-accent transition-opacity hover:opacity-90"
          >
            {t("onboarding.continue")}
          </button>
          <p className="text-[11px] text-fg-3">{t("onboarding.hint")}</p>
        </div>
      </div>
    </div>
  );
}
