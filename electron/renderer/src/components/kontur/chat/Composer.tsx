"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  ArrowUp,
  Check,
  ChevronDown,
  CornerDownLeft,
  FoldVertical,
  ListChecks,
  MessageSquare,
  Mic,
  MicOff,
  Paperclip,
  Pencil,
  Play,
  Square,
  Terminal,
  Wrench,
  X,
  Zap,
} from "lucide-react";
import { newId, useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { sendMessage, stopGeneration } from "@/lib/kontur/scenario";
import { useModel } from "@/lib/kontur/useModel";
import type { WorkMethod, WorkMode, Locale } from "@/lib/kontur/types";
import { KcToolButton } from "@/components/kontur/ui";
import { ModelPicker } from "./ModelPicker";
import { cn } from "@/lib/utils";

/* autonomy selector (Plan / Ask before changes / Edit automatically / Full access).
   Sets ui.workMethod; the engine agentMode is derived in the store. */
const METHODS: { id: WorkMethod; labelKey: string; hintKey: string; icon: React.ElementType }[] = [
  { id: "plan", labelKey: "work.method.plan", hintKey: "work.method.plan.hint", icon: ListChecks },
  { id: "ask", labelKey: "work.method.ask", hintKey: "work.method.ask.hint", icon: Wrench },
  { id: "auto", labelKey: "work.method.auto", hintKey: "work.method.auto.hint", icon: Pencil },
  { id: "full", labelKey: "work.method.full", hintKey: "work.method.full.hint", icon: Zap },
];

const DICTATED = "Check how the refresh token is issued and fix the expiry";

/* ---------- Web Speech API (typed minimal shims) ---------- */
interface SpeechResultChunk {
  isFinal: boolean;
  0: { transcript: string };
}
interface SpeechResultEvent {
  resultIndex: number;
  results: { length: number } & Record<number, SpeechResultChunk>;
}
interface SpeechRecognitionLike {
  lang: string;
  interimResults: boolean;
  continuous: boolean;
  start: () => void;
  stop: () => void;
  onresult: ((e: SpeechResultEvent) => void) | null;
  onend: (() => void) | null;
  onerror: ((e: { error?: string }) => void) | null;
}
type SpeechCtor = new () => SpeechRecognitionLike;

function getSpeechCtor(): SpeechCtor | null {
  if (typeof window === "undefined") return null;
  const w = window as unknown as {
    SpeechRecognition?: SpeechCtor;
    webkitSpeechRecognition?: SpeechCtor;
  };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
}

const SPEECH_LANG: Record<Locale, string> = { en: "en-US", ru: "ru-RU", de: "de-DE" };

export function Composer() {
  const t = useT();
  const ui = useKontur((s) => s.ui);
  const setWorkMode = useKontur((s) => s.setWorkMode);
  const setWorkMethod = useKontur((s) => s.setWorkMethod);
  const draft = useKontur((s) => s.draft);
  const setDraft = useKontur((s) => s.setDraft);
  const draftAttachments = useKontur((s) => s.draftAttachments);
  const addDraftAttachment = useKontur((s) => s.addDraftAttachment);
  const removeDraftAttachment = useKontur((s) => s.removeDraftAttachment);
  const recording = useKontur((s) => s.recording);
  const setRecording = useKontur((s) => s.setRecording);
  const runnerStatus = useKontur((s) => s.runner.status);
  const pushToast = useKontur((s) => s.pushToast);
  const approval = useKontur((s) => s.approval);

  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const [modelOpen, setModelOpen] = useState(false);
  const [dictating, setDictating] = useState(false);
  const [interim, setInterim] = useState("");
  const [liveSpeech, setLiveSpeech] = useState(false);
  const [slashIdx, setSlashIdx] = useState(0);
  const [slashDismissed, setSlashDismissed] = useState<string | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const modelRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const recognitionRef = useRef<SpeechRecognitionLike | null>(null);
  const dictatingRef = useRef(false);
  const baseDraftRef = useRef("");
  const finalRef = useRef("");
  const simTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const generating = runnerStatus === "running" || runnerStatus === "awaiting-approval";
  const activeMethod = METHODS.find((m) => m.id === ui.workMethod) ?? METHODS[1];
  const agentOn = ui.workMode !== "chat";
  const model = useModel(ui.selectedModelId);

  /* ---------- slash commands (typing "/" at the start) ---------- */

  const compactSession = useKontur((s) => s.compactSession);

  const slashCommands = useMemo(
    () => [
      {
        id: "chat",
        icon: MessageSquare,
        label: t("work.mode.chat"),
        hint: t("work.mode.chat.hint"),
        run: () => {
          setWorkMode("chat");
          pushToast(t("slash.toast.mode", t("work.mode.chat")));
        },
      },
      {
        id: "plan",
        icon: ListChecks,
        label: t("work.method.plan"),
        hint: t("work.method.plan.hint"),
        run: () => {
          setWorkMode("cowork");
          setWorkMethod("plan");
          pushToast(t("slash.toast.mode", t("work.method.plan")));
        },
      },
      {
        id: "ask",
        icon: Wrench,
        label: t("work.method.ask"),
        hint: t("work.method.ask.hint"),
        run: () => {
          setWorkMode("cowork");
          setWorkMethod("ask");
          pushToast(t("slash.toast.mode", t("work.method.ask")));
        },
      },
      {
        id: "auto",
        icon: Pencil,
        label: t("work.method.auto"),
        hint: t("work.method.auto.hint"),
        run: () => {
          setWorkMode("cowork");
          setWorkMethod("auto");
          pushToast(t("slash.toast.mode", t("work.method.auto")));
        },
      },
      {
        id: "full",
        icon: Zap,
        label: t("work.method.full"),
        hint: t("work.method.full.hint"),
        run: () => {
          setWorkMode("cowork");
          setWorkMethod("full");
          pushToast(t("slash.toast.mode", t("work.method.full")));
        },
      },
      {
        id: "demo",
        icon: Play,
        label: t("slash.cmd.demo"),
        hint: t("slash.cmd.demo.hint"),
        run: () => setDraft("run demo"),
      },
      {
        id: "compact",
        icon: FoldVertical,
        label: t("slash.cmd.compact"),
        hint: t("slash.cmd.compact.hint"),
        run: () => compactSession(),
      },
    ],
    [t, setWorkMode, setWorkMethod, pushToast, setDraft, compactSession],
  );

  const slashQuery =
    draft.startsWith("/") && !draft.slice(1).includes(" ") && !draft.includes("\n")
      ? draft.slice(1).toLowerCase()
      : null;
  const slashItems = useMemo(
    () =>
      slashQuery === null
        ? []
        : slashCommands.filter(
            (c) => c.id.startsWith(slashQuery) || c.id.includes(slashQuery),
          ),
    [slashQuery, slashCommands],
  );
  const slashOpen = slashQuery !== null && slashItems.length > 0 && slashDismissed !== slashQuery;
  const slashActive = slashOpen ? (slashItems[Math.min(slashIdx, slashItems.length - 1)] ?? null) : null;

  const runSlash = (cmd: (typeof slashCommands)[number]) => {
    cmd.run();
    /* mode/compact commands clear the draft; demo keeps its inserted text */
    if (cmd.id !== "demo") setDraft("");
    setSlashIdx(0);
    setSlashDismissed(null);
    if (textareaRef.current) textareaRef.current.style.height = "auto";
    textareaRef.current?.focus();
  };

  useEffect(() => {
    if (!modeMenuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setModeMenuOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [modeMenuOpen]);

  useEffect(() => {
    if (!modelOpen) return;
    const onDown = (e: MouseEvent) => {
      if (modelRef.current && !modelRef.current.contains(e.target as Node)) setModelOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [modelOpen]);

  /* ---------- dictation (real Web Speech when available, simulated otherwise) ---------- */

  const clearSim = () => {
    if (simTimerRef.current) {
      clearInterval(simTimerRef.current);
      simTimerRef.current = null;
    }
  };

  /** simulated fallback playback — used when Web Speech is missing or hard-fails */
  const runSimulated = () => {
    clearSim();
    const full = DICTATED;
    let i = 0;
    const current = useKontur.getState().draft;
    dictatingRef.current = true;
    setDictating(true);
    setLiveSpeech(false);
    setRecording(true);
    simTimerRef.current = setInterval(() => {
      i += 2;
      setDraft(current ? `${current} ${full.slice(0, i)}` : full.slice(0, i));
      setInterim(full.slice(Math.min(i, full.length), Math.min(i + 14, full.length)));
      if (i >= full.length) {
        clearSim();
        dictatingRef.current = false;
        setDictating(false);
        setRecording(false);
        setInterim("");
      }
    }, 45);
  };

  const stopDictation = () => {
    clearSim();
    recognitionRef.current?.stop();
    recognitionRef.current = null;
    dictatingRef.current = false;
    setLiveSpeech(false);
    setDictating(false);
    setRecording(false);
    setInterim("");
  };

  const startDictation = () => {
    if (dictatingRef.current) return;
    const Ctor = getSpeechCtor();
    if (!Ctor) {
      /* no Web Speech API at all */
      pushToast(t("chat.voice.listening"), t("chat.voice.unsupported"));
      runSimulated();
      return;
    }
    const rec = new Ctor();
    recognitionRef.current = rec;
    rec.lang = SPEECH_LANG[ui.locale] ?? "en-US";
    rec.interimResults = true;
    rec.continuous = false;
    baseDraftRef.current = useKontur.getState().draft;
    finalRef.current = "";
    rec.onresult = (event) => {
      let finalChunk = "";
      let interimChunk = "";
      for (let i = event.resultIndex; i < event.results.length; i++) {
        const result = event.results[i];
        if (result.isFinal) finalChunk += result[0].transcript;
        else interimChunk += result[0].transcript;
      }
      if (finalChunk) {
        finalRef.current = `${finalRef.current}${finalChunk}`.trim();
        const next = baseDraftRef.current
          ? `${baseDraftRef.current} ${finalRef.current}`
          : finalRef.current;
        setDraft(next);
      }
      setInterim(interimChunk);
    };
    rec.onend = () => {
      /* natural end of utterance — only reset if this instance is still active */
      if (recognitionRef.current !== rec) return;
      recognitionRef.current = null;
      dictatingRef.current = false;
      setDictating(false);
      setLiveSpeech(false);
      setRecording(false);
      setInterim("");
    };
    rec.onerror = (e) => {
      const hard = [
        "not-allowed",
        "service-not-allowed",
        "audio-capture",
        "no-speech",
        "network",
      ].includes(e.error ?? "");
      /* detach so the follow-up onend no-ops */
      if (recognitionRef.current === rec) recognitionRef.current = null;
      if (hard) {
        /* Web Speech present but unusable (headless, no mic, blocked) → simulate */
        pushToast(t("chat.voice.listening"), t("chat.voice.unsupported"));
        runSimulated();
      } else {
        dictatingRef.current = false;
        setDictating(false);
        setLiveSpeech(false);
        setRecording(false);
        setInterim("");
      }
    };
    try {
      rec.start();
      dictatingRef.current = true;
      setDictating(true);
      setLiveSpeech(true);
      setRecording(true);
    } catch {
      recognitionRef.current = null;
      runSimulated();
    }
  };

  /* Ctrl+M via global event */
  useEffect(() => {
    const onStart = () => startDictation();
    window.addEventListener("kontur:dictate", onStart);
    return () => window.removeEventListener("kontur:dictate", onStart);
  }, [ui.locale]);

  /* stop everything on unmount */
  useEffect(() => {
    return () => {
      clearSim();
      recognitionRef.current?.stop();
      // Clear the flag this component owns. Without it, switching away from Chat while
      // recording left `recording` true in the store with no component left to clear it,
      // and every later Ctrl+M returned at the `if (store.recording) return` guard —
      // dictation bricked for the rest of the session.
      if (useKontur.getState().recording) {
        useKontur.getState().setRecording(false);
      }
    };
  }, []);

  const submit = () => {
    if (!draft.trim() || generating) return;
    sendMessage(draft);
    if (textareaRef.current) textareaRef.current.style.height = "auto";
  };

  /* Paperclip → read picked files as text and stage them on this turn.
     Kept text-only and capped so a stray binary can't bloat the request. */
  const onPickFiles = async (list: FileList | null) => {
    if (!list || list.length === 0) return;
    const MAX_CHARS = 200_000;
    for (const file of Array.from(list)) {
      try {
        const raw = await file.text();
        addDraftAttachment({
          id: newId("att"),
          name: file.name,
          sizeKb: Math.max(1, Math.round(file.size / 1024)),
          mimeType: file.type || "text/plain",
          textContent: raw.length > MAX_CHARS ? raw.slice(0, MAX_CHARS) : raw,
        });
      } catch {
        pushToast(t("composer.attach"), file.name, "destructive");
      }
    }
  };

  const dictationLive = dictating && liveSpeech;

  return (
    <div className="px-3 pb-3 sm:px-4" data-kontur-composer>
      <div className="mx-auto max-w-[820px]">
        <div
          className={`rounded-lg border bg-surface-2 shadow-subtle transition-[border-color,box-shadow] duration-150 ${
            approval
              ? "border-warning/50"
              : dictationLive
                ? "border-accent/50 shadow-[0_0_0_3px_var(--kc-accent-soft)]"
                : "border-line focus-within:border-line-strong focus-within:shadow-[0_0_0_3px_var(--kc-accent-soft)]"
          }`}
        >
          {/* interim transcript ribbon */}
          {(dictating || interim) && (
            <div className="animate-fade border-b border-line-faint bg-accent-soft/40 px-3.5 py-1.5" aria-live="polite">
              <span className="flex items-center gap-2 text-[11.5px] text-fg-2">
                <span className="h-1.5 w-1.5 shrink-0 animate-pulse-dot rounded-full bg-accent" />
                <span className="shrink-0 font-medium text-accent">{t("chat.voice.interim")}</span>
                <span className="truncate italic text-fg-2">{interim || "…"}</span>
                {dictationLive && (
                  <span className="ml-auto hidden shrink-0 font-mono text-[9.5px] uppercase tracking-wider text-fg-3 sm:block">
                    {t("chat.voice.live")}
                  </span>
                )}
              </span>
            </div>
          )}

          {/* textarea + slash command menu */}
          <div className="relative">
            {slashOpen && (
              <div
                id="kc-slash-menu"
                role="listbox"
                aria-label={t("slash.title")}
                className="absolute bottom-[calc(100%+8px)] left-0 z-40 w-[320px] animate-settle overflow-hidden rounded-lg border border-line bg-surface-3 p-1.5 shadow-overlay"
              >
                <div className="flex items-center gap-1.5 px-2 pb-1 pt-0.5">
                  <Terminal size={9} className="text-accent" aria-hidden />
                  <span className="kc-overline">{t("slash.title")}</span>
                  <span className="ml-auto font-mono text-[9px] text-fg-3">↑↓ · enter · esc</span>
                </div>
                {slashItems.map((cmd, i) => {
                  const Icon = cmd.icon;
                  const active = i === Math.min(slashIdx, slashItems.length - 1);
                  return (
                    <button
                      key={cmd.id}
                      type="button"
                      role="option"
                      aria-selected={active}
                      onMouseDown={(e) => e.preventDefault()}
                      onMouseEnter={() => setSlashIdx(i)}
                      onClick={() => runSlash(cmd)}
                      className={cn(
                        "kc-focus-ring flex w-full items-center gap-2.5 rounded-sm px-2 py-1.5 text-left transition-colors",
                        active ? "bg-accent-soft" : "hover:bg-surface-hover",
                      )}
                    >
                      <span
                        className={cn(
                          "flex h-5 w-[54px] shrink-0 items-center justify-center rounded-xs border font-mono text-[10.5px]",
                          active
                            ? "border-accent-border bg-surface-1 text-accent"
                            : "border-line-faint bg-sunken text-fg-2",
                        )}
                      >
                        /{cmd.id}
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className={cn("block truncate text-[12px] font-medium", active ? "text-accent" : "text-fg-1")}>
                          {cmd.label}
                        </span>
                        <span className="block truncate text-[10.5px] text-fg-3">{cmd.hint}</span>
                      </span>
                      {active && <CornerDownLeft size={11} className="shrink-0 text-accent" aria-hidden />}
                    </button>
                  );
                })}
              </div>
            )}
            <textarea
              ref={textareaRef}
              value={draft}
              rows={1}
              onChange={(e) => {
                setDraft(e.target.value);
                if (!e.target.value.startsWith("/")) setSlashDismissed(null);
                e.target.style.height = "auto";
                e.target.style.height = `${Math.min(e.target.scrollHeight, 200)}px`;
              }}
              onKeyDown={(e) => {
                if (slashOpen && slashItems.length > 0) {
                  if (e.key === "ArrowDown") {
                    e.preventDefault();
                    setSlashIdx((i) => (i + 1) % slashItems.length);
                    return;
                  }
                  if (e.key === "ArrowUp") {
                    e.preventDefault();
                    setSlashIdx((i) => (i - 1 + slashItems.length) % slashItems.length);
                    return;
                  }
                  if (e.key === "Tab") {
                    e.preventDefault();
                    setDraft(`/${slashActive ? slashActive.id : slashItems[0].id} `);
                    setSlashDismissed(null);
                    return;
                  }
                  if (e.key === "Enter" && !e.shiftKey) {
                    e.preventDefault();
                    if (slashActive) runSlash(slashActive);
                    return;
                  }
                  if (e.key === "Escape") {
                    e.stopPropagation();
                    setSlashDismissed(slashQuery);
                    return;
                  }
                }
                if (e.key === "Enter" && !e.shiftKey && ui.enterSend) {
                  e.preventDefault();
                  submit();
                }
                if (e.key === "Escape" && dictating) {
                  e.stopPropagation();
                  stopDictation();
                }
              }}
              placeholder={
                dictating
                  ? t("chat.voice.listening")
                  : t("composer.placeholder", agentOn ? `· ${t(`work.method.${ui.workMethod}`)}` : "")
              }
              aria-label={t("composer.placeholder", "")}
              aria-autocomplete="list"
              aria-controls="kc-slash-menu"
              className="max-h-[200px] min-h-[44px] w-full resize-none bg-transparent px-3.5 py-3 text-[13.5px] leading-[1.55] text-fg-1 outline-none placeholder:text-fg-3"
            />
          </div>

          {/* staged attachments */}
          {draftAttachments.length > 0 && (
            <div className="flex flex-wrap gap-1.5 border-t border-line-faint px-3 py-2">
              {draftAttachments.map((a) => (
                <span
                  key={a.id}
                  className="flex items-center gap-1.5 rounded-sm border border-line-faint bg-surface-1 px-2 py-1 text-[11px] text-fg-2"
                >
                  <Paperclip size={10} className="shrink-0 text-fg-3" />
                  <span className="max-w-[160px] truncate">{a.name}</span>
                  <span className="font-mono text-[9.5px] text-fg-3">{a.sizeKb}KB</span>
                  <button
                    type="button"
                    onClick={() => removeDraftAttachment(a.id)}
                    aria-label={t("common.delete")}
                    title={t("common.delete")}
                    className="flex h-4 w-4 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
                  >
                    <X size={10} />
                  </button>
                </span>
              ))}
            </div>
          )}

          {/* footer */}
          <div className="flex items-center gap-1 border-t border-line-faint px-2 py-1.5">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              className="hidden"
              onChange={(e) => {
                void onPickFiles(e.target.files);
                e.target.value = ""; // allow re-picking the same file
              }}
            />
            <KcToolButton
              onClick={() => fileInputRef.current?.click()}
              aria-label={t("composer.attach")}
              title={t("composer.attach")}
            >
              <Paperclip size={14} />
            </KcToolButton>

            {/* work-method selector — autonomy while coworking (hidden in plain Chat) */}
            {agentOn && (
            <div className="relative" ref={menuRef}>
              <button
                type="button"
                onClick={() => setModeMenuOpen((v) => !v)}
                className="kc-focus-ring flex h-7 items-center gap-1.5 rounded-sm bg-accent-soft px-2 text-[12px] font-medium text-accent transition-colors hover:bg-accent-soft"
                aria-haspopup="menu"
                aria-expanded={modeMenuOpen}
                title={t("agent.tooltip")}
              >
                <activeMethod.icon size={13} />
                <span className="hidden sm:inline">
                  {`${t("work.method")} · ${t(`work.method.${ui.workMethod}`)}`}
                </span>
                <ChevronDown size={11} className="text-fg-3" />
              </button>
              {modeMenuOpen && (
                <div
                  role="menu"
                  className="absolute bottom-[calc(100%+6px)] left-0 z-40 w-[290px] animate-settle overflow-hidden rounded-lg border border-line bg-surface-3 p-1.5 shadow-overlay"
                >
                  {METHODS.map((method) => {
                    const selected = ui.workMethod === method.id;
                    const Icon = method.icon;
                    return (
                      <button
                        key={method.id}
                        type="button"
                        role="menuitemradio"
                        aria-checked={selected}
                        onClick={() => {
                          setWorkMethod(method.id);
                          setModeMenuOpen(false);
                        }}
                        className={`kc-focus-ring flex w-full items-start gap-2.5 rounded-sm px-2.5 py-2 text-left transition-colors ${
                          selected ? "bg-accent-soft" : "hover:bg-surface-hover"
                        }`}
                      >
                        {selected ? (
                          <Check size={13} className="mt-[2px] shrink-0 text-accent" />
                        ) : (
                          <Icon size={13} className="mt-[2px] shrink-0 text-fg-3" />
                        )}
                        <span className="min-w-0">
                          <span
                            className={`block text-[12.5px] font-medium ${selected ? "text-accent" : "text-fg-1"}`}
                          >
                            {t(method.labelKey)}
                          </span>
                          <span className="block text-[11px] leading-snug text-fg-3">{t(method.hintKey)}</span>
                        </span>
                      </button>
                    );
                  })}
                </div>
              )}
            </div>
            )}

            {/* right cluster — model · mic · send */}
            <div className="ml-auto flex items-center gap-1">
              {/* model pill */}
              <div className="relative" ref={modelRef}>
                <button
                  type="button"
                  onClick={() => setModelOpen((v) => !v)}
                  className="kc-focus-ring hidden h-7 items-center gap-1.5 rounded-sm px-2 text-[11.5px] text-fg-2 transition-colors hover:bg-surface-hover hover:text-fg-1 md:flex"
                  aria-label={t("composer.selectModel")}
                  title={t("composer.selectModel")}
                >
                  <span className="max-w-[140px] truncate font-medium text-fg-1">
                    {model?.name ?? t("status.noModel")}
                  </span>
                  <ChevronDown size={11} className="text-fg-3" />
                </button>
                {modelOpen && (
                  <div className="absolute bottom-[calc(100%+6px)] right-0 z-40 animate-settle">
                    <ModelPicker onClose={() => setModelOpen(false)} />
                  </div>
                )}
              </div>

              {dictating ? (
                <button
                  type="button"
                  onClick={stopDictation}
                  className="kc-focus-ring flex h-7 items-center gap-1.5 rounded-sm bg-error-soft px-2 text-[11.5px] font-medium text-error transition-colors hover:bg-error-soft"
                  aria-label={t("chat.voice.stop")}
                  title={t("chat.voice.stop")}
                >
                  <MicOff size={13} className="animate-pulse-dot" />
                  <span className="hidden sm:inline">{t("chat.voice.stop")}</span>
                </button>
              ) : (
                <button
                  type="button"
                  onClick={startDictation}
                  className={`kc-focus-ring flex h-7 items-center justify-center rounded-sm px-2 transition-colors ${
                    recording
                      ? "bg-accent-soft text-accent"
                      : "text-fg-3 hover:bg-surface-hover hover:text-fg-1"
                  }`}
                  aria-label={t("composer.voice")}
                  title={`${t("composer.voice")} · Ctrl+M`}
                >
                  <Mic size={14} className={recording ? "animate-pulse-dot" : undefined} />
                </button>
              )}

              {generating ? (
                <button
                  type="button"
                  onClick={stopGeneration}
                  className="kc-focus-ring flex h-8 w-8 items-center justify-center rounded-full bg-surface-3 text-fg-1 transition-colors hover:bg-surface-hover"
                  aria-label={t("chat.stop")}
                  title={t("chat.stop")}
                >
                  <Square size={12} className="fill-current" />
                </button>
              ) : (
                <button
                  type="button"
                  onClick={submit}
                  disabled={!draft.trim()}
                  className="kc-focus-ring flex h-8 w-8 items-center justify-center rounded-full bg-accent text-on-accent transition-all duration-100 hover:bg-accent-hover active:translate-y-px disabled:pointer-events-none disabled:opacity-40"
                  aria-label={t("composer.send")}
                  title={t("composer.send")}
                >
                  <ArrowUp size={16} strokeWidth={2.5} />
                </button>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

/* Ctrl+M handler exposed for the global keyboard layer */
export function triggerDictation() {
  const store = useKontur.getState();
  if (store.recording) return;

  // Only mark the store as recording if someone can hear about it. The listener lives in
  // Composer, so on any other surface the event goes nowhere — and setting the flag anyway left
  // `recording` stuck true, which made this function a no-op from then on. Either the surface is
  // showing Chat, or there is nothing to dictate into and the press should do nothing at all.
  if (!document.querySelector("[data-kontur-composer]")) return;

  store.setRecording(true);
  window.dispatchEvent(new CustomEvent("kontur:dictate"));
}
