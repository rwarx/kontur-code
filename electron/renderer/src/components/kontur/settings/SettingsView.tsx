"use client";

import { Fragment, useEffect, useRef, useState } from "react";
import type { ReactNode } from "react";
import { AlertTriangle, ArrowUpRight, Check, FolderOpen, Moon, Sun, Terminal } from "lucide-react";
import { cn } from "@/lib/utils";
import { APP_ZOOM_PRESETS, applyAppZoom, useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { workspace as workspaceApi } from "@/lib/kontur/backend";
import {
  isServerMode,
  loadServerSettings,
  reindexCanvas,
  saveServerSettings,
  syncWorkspaceFiles,
} from "@/lib/kontur/sync";
import { SKILLS } from "@/lib/kontur/data";
import type { Locale, ThemeMode } from "@/lib/kontur/types";
import { Switch } from "@/components/ui/switch";
import { Slider } from "@/components/ui/slider";
import {
  Divider,
  KcCard,
  KcGhostButton,
  Keycap,
  Overline,
} from "@/components/kontur/ui";
import { ProviderStudioLauncher } from "../chat/ProviderStudio";

/* ============================================================
   SETTINGS — one unified design-system page.
   Compact sections: Overline → hint → KcCard with dense rows.
   ============================================================ */

/* Fixed palette data for the two theme preview strips. Each card
   previews its own palette, so the "other" theme's colors must be
   literal — they are data, not live styling. */
const DARK_STRIP = ["#131417", "#1a1c1f", "#212327", "#292b30", "#2c2e33"];
const LIGHT_STRIP = ["#f3f4f6", "#f8f9fb", "#ffffff", "#eceef1", "#e9ebee"];
const DARK_ACCENT = "#ffffff";
const LIGHT_ACCENT = "#202429";

const LOCALE_LABELS: Record<Locale, string> = { en: "EN", ru: "Рус", de: "Deutsch" };

/* Live design tokens read from CSS variables at runtime. */
const TOKEN_VARS: { name: string; cssVar: string }[] = [
  { name: "bg", cssVar: "--kc-bg" },
  { name: "surface-1", cssVar: "--kc-surface-1" },
  { name: "surface-2", cssVar: "--kc-surface-2" },
  { name: "surface-3", cssVar: "--kc-surface-3" },
  { name: "sunken", cssVar: "--kc-surface-sunken" },
  { name: "line", cssVar: "--kc-line" },
  { name: "fg-1", cssVar: "--kc-fg-1" },
  { name: "fg-2", cssVar: "--kc-fg-2" },
  { name: "fg-3", cssVar: "--kc-fg-3" },
  { name: "accent", cssVar: "--kc-accent" },
  { name: "success", cssVar: "--kc-success" },
  { name: "warning", cssVar: "--kc-warning" },
  { name: "error", cssVar: "--kc-error" },
];

const SPACING_SCALE = [4, 8, 12, 16, 20, 24, 32];
const RADIUS_SCALE = [4, 6, 8, 10, 12];

const SHORTCUTS: { action: string; keys: string[] }[] = [
  { action: "shortcuts.newChat", keys: ["Ctrl", "N"] },
  { action: "shortcuts.commandPalette", keys: ["Ctrl", "Shift", "P"] },
  { action: "shortcuts.checkpointJump", keys: ["Ctrl", "Shift", "K"] },
  { action: "shortcuts.sidebar", keys: ["Ctrl", "B"] },
  { action: "shortcuts.canvas", keys: ["Ctrl", "G"] },
  { action: "shortcuts.voice", keys: ["Ctrl", "M"] },
  { action: "shortcuts.contextPanel", keys: ["Ctrl", "Alt", "I"] },
  { action: "shortcuts.settings", keys: ["Ctrl", ","] },
  { action: "shortcuts.stop", keys: ["Esc"] },
  { action: "shortcuts.back", keys: ["Alt", "←"] },
  { action: "shortcuts.forward", keys: ["Alt", "→"] },
];

/* ---------- command-allowlist helpers (WPF parity) ---------- */

/* Split the allowlist textarea into bare tokens on comma/semicolon/whitespace,
   mirroring the WPF SettingsViewModel.Tokenize. */
function tokenizeCommands(text: string): string[] {
  return text
    .split(/[,;\s]+/)
    .map((s) => s.trim())
    .filter(Boolean);
}

/* Tokens that can't name a bare program: anything carrying a path separator,
   drive colon, or shell/glob metacharacter. The backend allowlist matches by
   bare program name, so these would never match and are dropped. */
function droppedCommandTokens(tokens: string[]): string[] {
  return tokens.filter((tok) => /[\\/:*?"<>|]/.test(tok));
}

const clampInt = (n: number, lo: number, hi: number) =>
  Math.min(hi, Math.max(lo, Math.round(n)));

/* ---------- small layout helpers ---------- */

function Section({
  overline,
  hint,
  children,
}: {
  overline: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <section className="space-y-3">
      <div>
        <Overline>{overline}</Overline>
        {hint ? <p className="mt-1.5 text-[12px] leading-snug text-fg-3">{hint}</p> : null}
      </div>
      {children}
    </section>
  );
}

function Row({ label, children }: { label: ReactNode; children?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2.5 first:pt-0 last:pb-0">
      <span className="min-w-0 text-[13px] font-medium text-fg-1">{label}</span>
      {children}
    </div>
  );
}

function ToggleRow({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (v: boolean) => void;
}) {
  return (
    <Row label={label}>
      <Switch checked={checked} onCheckedChange={onChange} aria-label={label} />
    </Row>
  );
}

/* ---------- theme card with fixed palette preview ---------- */

function ThemeCard({
  mode,
  selected,
  onSelect,
}: {
  mode: ThemeMode;
  selected: boolean;
  onSelect: () => void;
}) {
  const t = useT();
  const dark = mode === "dark";
  const strip = dark ? DARK_STRIP : LIGHT_STRIP;
  const accent = dark ? DARK_ACCENT : LIGHT_ACCENT;
  const label = dark ? t("theme.dark") : t("theme.light");
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      className={cn(
        "kc-focus-ring flex-1 rounded-md border p-3 text-left transition-colors duration-100",
        selected
          ? "border-accent bg-accent-soft"
          : "border-line-faint bg-surface-1 hover:border-line hover:bg-surface-hover",
      )}
    >
      <div className="flex items-center gap-2">
        {dark ? (
          <Moon size={14} strokeWidth={1.75} className="text-fg-2" aria-hidden />
        ) : (
          <Sun size={14} strokeWidth={1.75} className="text-fg-2" aria-hidden />
        )}
        <span className="text-[13px] font-medium text-fg-1">{label}</span>
        {selected ? (
          <Check size={12} strokeWidth={2.25} className="ml-auto text-accent" aria-hidden />
        ) : null}
      </div>
      <div className="mt-3 flex h-4 overflow-hidden rounded-xs border border-line" aria-hidden>
        {strip.map((c) => (
          <span key={c} className="h-full flex-1" style={{ background: c }} />
        ))}
        <span className="h-full w-3.5" style={{ background: accent }} />
      </div>
    </button>
  );
}

/* ---------- surface ---------- */

export default function SettingsView() {
  const t = useT();
  const ui = useKontur((s) => s.ui);
  const setUi = useKontur((s) => s.setUi);
  const pushToast = useKontur((s) => s.pushToast);

  /* local state — these persist per session in the product */
  const [systemPrompt, setSystemPrompt] = useState(
    "You are Kontur Code, a desktop coding agent. Prefer minimal diffs, and ask before anything you cannot take back.",
  );
  const [temperature, setTemperature] = useState(0.7);
  const [maxTokens, setMaxTokens] = useState("8192");
  const [timeLimit, setTimeLimit] = useState("10");
  /* command execution gate (backend AgentSettings; default OFF) */
  const [allowCommands, setAllowCommands] = useState(false);
  const [allowedCommands, setAllowedCommands] = useState("");
  const [commandTimeout, setCommandTimeout] = useState("120");
  const [maxCmdOutput, setMaxCmdOutput] = useState("20000");
  /* network fetch gate (backend AgentSettings; default OFF, approval every call) */
  const [allowNetwork, setAllowNetwork] = useState(false);
  const [fetchTimeout, setFetchTimeout] = useState("30");
  const [maxFetch, setMaxFetch] = useState("20000");
  /* out-of-project file gate (backend AgentSettings; default OFF; reads ask once, writes every call) */
  const [allowExternalFiles, setAllowExternalFiles] = useState(false);
  const [wsName, setWsName] = useState<string | null>(null);
  const [wsRoot, setWsRoot] = useState<string | null>(null);
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /* Pending patches keyed by section, merged so several fields edited within the
     debounce window all persist. The backend PUT is a partial patch, so shipping
     { allowCommands, allowedCommands, … } together applies each key without
     clobbering the rest of the section. */
  const pendingSaves = useRef<Map<string, Record<string, unknown>>>(new Map());

  /* server-backed chat/agent/workspace settings (no-op offline) */
  useEffect(() => {
    if (!isServerMode()) return;
    let cancelled = false;
    void (async () => {
      const s = await loadServerSettings();
      if (!s || cancelled) return;
      if (typeof s.chat["systemPrompt"] === "string") setSystemPrompt(s.chat["systemPrompt"] as string);
      if (typeof s.chat["temperature"] === "number") setTemperature(s.chat["temperature"] as number);
      if (typeof s.chat["maxTokens"] === "number") setMaxTokens(String(s.chat["maxTokens"]));
      if (typeof s.agent["maxSteps"] === "number") setUi({ maxSteps: s.agent["maxSteps"] as number });
      if (typeof s.agent["maxDurationSeconds"] === "number") {
        const secs = s.agent["maxDurationSeconds"] as number;
        if (secs > 0) setTimeLimit(String(Math.round(secs / 60)));
      }
      if (typeof s.agent["allowCommands"] === "boolean") setAllowCommands(s.agent["allowCommands"] as boolean);
      if (Array.isArray(s.agent["allowedCommands"])) {
        setAllowedCommands((s.agent["allowedCommands"] as unknown[]).filter((x) => typeof x === "string").join(", "));
      }
      if (typeof s.agent["commandTimeoutSeconds"] === "number") setCommandTimeout(String(s.agent["commandTimeoutSeconds"]));
      if (typeof s.agent["maxCommandOutputCharacters"] === "number") setMaxCmdOutput(String(s.agent["maxCommandOutputCharacters"]));
      if (typeof s.agent["allowNetwork"] === "boolean") setAllowNetwork(s.agent["allowNetwork"] as boolean);
      if (typeof s.agent["fetchTimeoutSeconds"] === "number") setFetchTimeout(String(s.agent["fetchTimeoutSeconds"]));
      if (typeof s.agent["maxFetchResponseCharacters"] === "number") setMaxFetch(String(s.agent["maxFetchResponseCharacters"]));
      if (typeof s.agent["allowExternalFiles"] === "boolean") setAllowExternalFiles(s.agent["allowExternalFiles"] as boolean);
      try {
        const st = await workspaceApi.status();
        if (!cancelled && st.isOpen && st.root) {
          setWsRoot(st.root);
          setWsName(st.root.split(/[/\\]/).filter(Boolean).pop() ?? st.root);
        }
      } catch {
        /* workspace row stays empty */
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const flushSaves = () => {
    const batch = pendingSaves.current;
    pendingSaves.current = new Map();
    for (const [section, body] of batch) {
      void saveServerSettings(section, body).then((ok) => {
        if (!ok) pushToast(t("settings.saveFailed") ?? "Save failed", undefined, "destructive");
      });
    }
  };

  useEffect(
    () => () => {
      if (saveTimer.current) clearTimeout(saveTimer.current);
      // persist anything still pending when the panel unmounts
      flushSaves();
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  );

  const saveSoon = (section: string, body: Record<string, unknown>) => {
    if (!isServerMode()) return;
    const prev = pendingSaves.current.get(section) ?? {};
    pendingSaves.current.set(section, { ...prev, ...body });
    if (saveTimer.current) clearTimeout(saveTimer.current);
    saveTimer.current = setTimeout(flushSaves, 800);
  };

  const openWorkspace = () => {
    if (!isServerMode() || !window.kontur) {
      pushToast(t("settings.workspace.toast"));
      return;
    }
    void (async () => {
      const dir = await window.kontur!.pickFolder();
      if (!dir) return;
      try {
        const res = await workspaceApi.open(dir);
        setWsRoot(res.root);
        setWsName(res.root.split(/[/\\]/).filter(Boolean).pop() ?? res.root);
        await syncWorkspaceFiles().catch(() => undefined);
        await reindexCanvas().catch(() => undefined);
        pushToast(t("settings.workspace.opened", res.root) ?? res.root);
      } catch (err) {
        pushToast(t("settings.workspace.failed") ?? "Cannot open that folder", err instanceof Error ? err.message : undefined, "destructive");
      }
    })();
  };
  /* skill toggles are store-backed (persisted) and gate the agent's per-run
     skill auto-selection — shared with Workflows via ui.skillsEnabled. */
  const skills = ui.skillsEnabled;
  const setSkill = (id: string, on: boolean) =>
    setUi({ skillsEnabled: { ...skills, [id]: on } });

  /* live design tokens — read from the rendered element so they follow
     whichever theme class the shell has applied */
  const tokenRef = useRef<HTMLDivElement>(null);
  const [tokenValues, setTokenValues] = useState<Record<string, string>>({});

  useEffect(() => {
    const read = () => {
      const el = tokenRef.current ?? document.documentElement;
      const cs = getComputedStyle(el);
      const out: Record<string, string> = {};
      for (const { name, cssVar } of TOKEN_VARS) {
        out[name] = cs.getPropertyValue(cssVar).trim();
      }
      setTokenValues(out);
    };
    /* small delay so the theme class flip settles before reading */
    const id = setTimeout(read, 80);
    return () => clearTimeout(id);
  }, [ui.theme]);

  /* WPF-parity allowlist validation shown under the textarea */
  const cmdTokens = tokenizeCommands(allowedCommands);
  const cmdDropped = droppedCommandTokens(cmdTokens);
  const cmdProblem = !allowCommands
    ? null
    : cmdDropped.length > 0
      ? t("settings.commands.warnDropped", cmdDropped.join(", "))
      : cmdTokens.length === 0
        ? t("settings.commands.warnEmpty")
        : null;

  return (
    <div ref={tokenRef} className="h-full w-full overflow-y-auto">
      <div className="mx-auto max-w-[760px] space-y-8 px-6 py-6">
        {/* 1 — APPEARANCE */}
        <Section overline={t("settings.appearance")} hint={t("settings.theme.hint")}>
          <KcCard className="p-4">
            <div className="text-[13px] font-medium text-fg-1">{t("settings.theme")}</div>
            <div className="mt-2.5 flex flex-col gap-3 sm:flex-row">
              <ThemeCard
                mode="dark"
                selected={ui.theme === "dark"}
                onSelect={() => setUi({ theme: "dark" })}
              />
              <ThemeCard
                mode="light"
                selected={ui.theme === "light"}
                onSelect={() => setUi({ theme: "light" })}
              />
            </div>

            <Divider className="my-4" />

            <Row label={t("language")}>
              <div className="flex gap-1.5">
                {(Object.keys(LOCALE_LABELS) as Locale[]).map((loc) => (
                  <button
                    key={loc}
                    type="button"
                    onClick={() => setUi({ locale: loc })}
                    aria-pressed={ui.locale === loc}
                    className={cn(
                      "kc-focus-ring h-7 rounded-sm border px-3 text-[12px] font-medium transition-colors duration-100",
                      ui.locale === loc
                        ? "border-accent-border bg-accent-soft text-accent"
                        : "border-line-faint text-fg-2 hover:border-line hover:bg-surface-hover hover:text-fg-1",
                    )}
                  >
                    {LOCALE_LABELS[loc]}
                  </button>
                ))}
              </div>
            </Row>
          </KcCard>
        </Section>

        {/* 2 — INTERFACE */}
        <Section overline={t("settings.interface")}>
          <KcCard className="px-4 py-3">
            <ToggleRow
              label={t("settings.enterSend")}
              checked={ui.enterSend}
              onChange={(v) => setUi({ enterSend: v })}
            />
            <Divider />
            <ToggleRow
              label={t("settings.renderMarkdown")}
              checked={ui.renderMarkdown}
              onChange={(v) => setUi({ renderMarkdown: v })}
            />
            <Divider />
            <ToggleRow
              label={t("settings.autoscroll")}
              checked={ui.autoscroll}
              onChange={(v) => setUi({ autoscroll: v })}
            />
            <Divider />
            <Row label={t("settings.sidebarWidth")}>
              <div className="flex items-center gap-3">
                <Slider
                  className="w-36 sm:w-44"
                  min={200}
                  max={420}
                  step={2}
                  value={[ui.sidebarWidth]}
                  onValueChange={([v]) => setUi({ sidebarWidth: v })}
                  aria-label={t("settings.sidebarWidth")}
                />
                <span className="w-11 shrink-0 text-right font-mono text-[11px] text-fg-3">
                  {ui.sidebarWidth}px
                </span>
              </div>
            </Row>
            <Divider />
            <Row label={t("settings.appScale")}>
              <div className="flex flex-wrap items-center justify-end gap-1.5">
                {APP_ZOOM_PRESETS.map((z) => {
                  const active = Math.abs((ui.appZoom ?? 1) - z) < 0.001;
                  return (
                    <button
                      key={z}
                      type="button"
                      onClick={() => {
                        setUi({ appZoom: z });
                        applyAppZoom(z);
                      }}
                      aria-pressed={active}
                      className={cn(
                        "kc-focus-ring h-7 rounded-sm border px-2.5 text-[12px] font-medium tabular-nums transition-colors duration-100",
                        active
                          ? "border-accent-border bg-accent-soft text-accent"
                          : "border-line-faint text-fg-2 hover:border-line hover:bg-surface-hover hover:text-fg-1",
                      )}
                    >
                      {Math.round(z * 100)}%
                    </button>
                  );
                })}
              </div>
            </Row>
          </KcCard>
        </Section>

        {/* 3 — CHAT */}
        <Section overline={t("settings.chat")}>
          <KcCard className="p-4">
            <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
              <span className="text-[13px] font-medium text-fg-1">{t("settings.systemPrompt")}</span>
              <span className="text-[11px] text-fg-3">{t("settings.systemPrompt.hint")}</span>
            </div>
            <textarea
              value={systemPrompt}
              onChange={(e) => {
                setSystemPrompt(e.target.value);
                saveSoon("chat", { systemPrompt: e.target.value });
              }}
              rows={3}
              spellCheck={false}
              aria-label={t("settings.systemPrompt")}
              className="kc-focus-ring mt-2 w-full resize-y rounded-sm border border-line bg-sunken p-2.5 font-mono text-[12.5px] leading-relaxed text-fg-1"
            />

            <Divider className="my-4" />

            <Row label={t("settings.temperature")}>
              <div className="flex items-center gap-3">
                <Slider
                  className="w-36 sm:w-44"
                  min={0}
                  max={2}
                  step={0.1}
                  value={[temperature]}
                  onValueChange={([v]) => {
                    setTemperature(v);
                    saveSoon("chat", { temperature: v });
                  }}
                  aria-label={t("settings.temperature")}
                />
                <span className="w-11 shrink-0 text-right font-mono text-[11px] text-fg-3">
                  {temperature.toFixed(1)}
                </span>
              </div>
            </Row>
            <Divider />
            <Row label={t("settings.maxTokens")}>
              <input
                type="number"
                min={256}
                step={256}
                value={maxTokens}
                onChange={(e) => {
                  setMaxTokens(e.target.value);
                  const n = parseInt(e.target.value, 10);
                  if (Number.isFinite(n) && n > 0) saveSoon("chat", { maxTokens: n });
                }}
                aria-label={t("settings.maxTokens")}
                className="kc-focus-ring h-7 w-24 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
              />
            </Row>
          </KcCard>
        </Section>

        {/* 4 — AGENT */}
        <Section overline={t("settings.agent")}>
          <KcCard className="px-4 py-3">
            <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2.5 first:pt-0 last:pb-0">
              <div className="flex min-w-0 items-center gap-2.5">
                <span
                  className="flex h-7 w-7 shrink-0 items-center justify-center rounded-sm border border-line bg-surface-hover text-fg-2"
                  aria-hidden
                >
                  <FolderOpen size={14} strokeWidth={1.75} />
                </span>
                <div className="min-w-0">
                  <div className="text-[13px] font-medium leading-tight text-fg-1">
                    {wsName ?? t("settings.workspace.none")}
                  </div>
                  <div className="mt-0.5 truncate font-mono text-[10.5px] leading-tight text-fg-3">
                    {wsRoot ?? t("settings.workspace.noneHint")}
                  </div>
                </div>
              </div>
              <KcGhostButton className="ml-auto" onClick={openWorkspace}>
                {t("settings.workspace.openFolder")}
              </KcGhostButton>
            </div>

            <Divider />

            <Row label={t("settings.maxSteps")}>
              <div className="flex items-center gap-3">
                <Slider
                  className="w-36 sm:w-44"
                  min={10}
                  max={80}
                  step={1}
                  value={[ui.maxSteps]}
                  onValueChange={([v]) => {
                    const rounded = Math.round(v);
                    setUi({ maxSteps: rounded });
                    saveSoon("agent", { maxSteps: rounded });
                  }}
                  aria-label={t("settings.maxSteps")}
                />
                <span className="w-11 shrink-0 text-right font-mono text-[11px] text-fg-3">
                  {ui.maxSteps}
                </span>
              </div>
            </Row>
            <Divider />
            <Row label={t("settings.timeLimit")}>
              <div className="flex items-center gap-2">
                <input
                  type="number"
                  min={1}
                  step={1}
                  value={timeLimit}
                  onChange={(e) => {
                    setTimeLimit(e.target.value);
                    const n = parseInt(e.target.value, 10);
                    if (Number.isFinite(n) && n > 0) saveSoon("agent", { maxDurationSeconds: n * 60 });
                  }}
                  aria-label={t("settings.timeLimit")}
                  className="kc-focus-ring h-7 w-20 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
                />
                <span className="text-[11px] text-fg-3">{t("settings.minutes")}</span>
              </div>
            </Row>
          </KcCard>

          {/* commands — backend execution gate (AgentSettings.AllowCommands) */}
          <div className="space-y-3">
            <div>
              <Overline>{t("settings.commands")}</Overline>
              <p className="mt-1.5 text-[12px] leading-snug text-fg-3">{t("settings.commands.hint")}</p>
            </div>
            <KcCard className="px-4 py-3">
              <ToggleRow
                label={t("settings.commands.allow")}
                checked={allowCommands}
                onChange={(v) => {
                  setAllowCommands(v);
                  saveSoon("agent", { allowCommands: v });
                }}
              />
              {allowCommands ? (
                <div className="mb-1 flex items-start gap-2 rounded-sm border border-warning/30 bg-warning-soft px-2.5 py-2 text-[11.5px] leading-snug text-warning">
                  <AlertTriangle size={13} strokeWidth={2} className="mt-0.5 shrink-0" aria-hidden />
                  <span>{t("settings.commands.allowWarn")}</span>
                </div>
              ) : null}
              <Divider />
              <div className="py-2.5">
                <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                  <span className="text-[13px] font-medium text-fg-1">{t("settings.commands.allowed")}</span>
                  <span className="text-[11px] text-fg-3">{t("settings.commands.allowedHint")}</span>
                </div>
                <textarea
                  value={allowedCommands}
                  onChange={(e) => {
                    setAllowedCommands(e.target.value);
                    saveSoon("agent", { allowedCommands: tokenizeCommands(e.target.value) });
                  }}
                  rows={2}
                  spellCheck={false}
                  disabled={!allowCommands}
                  aria-label={t("settings.commands.allowed")}
                  className="kc-focus-ring mt-2 w-full resize-y rounded-sm border border-line bg-sunken p-2.5 font-mono text-[12px] leading-relaxed text-fg-1 disabled:cursor-not-allowed disabled:opacity-50"
                />
                {cmdProblem ? (
                  <div className="mt-2 flex items-start gap-2 text-[11.5px] leading-snug text-warning">
                    <AlertTriangle size={13} strokeWidth={2} className="mt-0.5 shrink-0" aria-hidden />
                    <span>{cmdProblem}</span>
                  </div>
                ) : null}
              </div>
              <Divider />
              <Row label={t("settings.commands.timeout")}>
                <div className="flex items-center gap-2">
                  <input
                    type="number"
                    min={5}
                    max={3600}
                    step={5}
                    value={commandTimeout}
                    onChange={(e) => {
                      setCommandTimeout(e.target.value);
                      const n = parseInt(e.target.value, 10);
                      if (Number.isFinite(n)) saveSoon("agent", { commandTimeoutSeconds: clampInt(n, 5, 3600) });
                    }}
                    aria-label={t("settings.commands.timeout")}
                    className="kc-focus-ring h-7 w-20 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
                  />
                  <span className="text-[11px] text-fg-3">{t("settings.commands.sec")}</span>
                </div>
              </Row>
              <Divider />
              <Row label={t("settings.commands.maxOutput")}>
                <div className="flex items-center gap-2">
                  <input
                    type="number"
                    min={1000}
                    max={200000}
                    step={1000}
                    value={maxCmdOutput}
                    onChange={(e) => {
                      setMaxCmdOutput(e.target.value);
                      const n = parseInt(e.target.value, 10);
                      if (Number.isFinite(n)) saveSoon("agent", { maxCommandOutputCharacters: clampInt(n, 1000, 200000) });
                    }}
                    aria-label={t("settings.commands.maxOutput")}
                    className="kc-focus-ring h-7 w-24 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
                  />
                  <span className="text-[11px] text-fg-3">{t("settings.commands.chars")}</span>
                </div>
              </Row>
            </KcCard>
          </div>

          {/* network — backend fetch gate (AgentSettings.AllowNetwork; approval every call) */}
          <div className="space-y-3">
            <div>
              <Overline>{t("settings.network")}</Overline>
              <p className="mt-1.5 text-[12px] leading-snug text-fg-3">{t("settings.network.hint")}</p>
            </div>
            <KcCard className="px-4 py-3">
              <ToggleRow
                label={t("settings.network.allow")}
                checked={allowNetwork}
                onChange={(v) => {
                  setAllowNetwork(v);
                  saveSoon("agent", { allowNetwork: v });
                }}
              />
              {allowNetwork ? (
                <div className="mb-1 flex items-start gap-2 rounded-sm border border-warning/30 bg-warning-soft px-2.5 py-2 text-[11.5px] leading-snug text-warning">
                  <AlertTriangle size={13} strokeWidth={2} className="mt-0.5 shrink-0" aria-hidden />
                  <span>{t("settings.network.allowWarn")}</span>
                </div>
              ) : null}
              <Divider />
              <Row label={t("settings.network.timeout")}>
                <div className="flex items-center gap-2">
                  <input
                    type="number"
                    min={5}
                    max={300}
                    step={5}
                    value={fetchTimeout}
                    onChange={(e) => {
                      setFetchTimeout(e.target.value);
                      const n = parseInt(e.target.value, 10);
                      if (Number.isFinite(n)) saveSoon("agent", { fetchTimeoutSeconds: clampInt(n, 5, 300) });
                    }}
                    aria-label={t("settings.network.timeout")}
                    className="kc-focus-ring h-7 w-20 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
                  />
                  <span className="text-[11px] text-fg-3">{t("settings.commands.sec")}</span>
                </div>
              </Row>
              <Divider />
              <Row label={t("settings.network.maxResponse")}>
                <div className="flex items-center gap-2">
                  <input
                    type="number"
                    min={1000}
                    max={200000}
                    step={1000}
                    value={maxFetch}
                    onChange={(e) => {
                      setMaxFetch(e.target.value);
                      const n = parseInt(e.target.value, 10);
                      if (Number.isFinite(n)) saveSoon("agent", { maxFetchResponseCharacters: clampInt(n, 1000, 200000) });
                    }}
                    aria-label={t("settings.network.maxResponse")}
                    className="kc-focus-ring h-7 w-24 rounded-sm border border-line bg-sunken px-2.5 text-right font-mono text-[12px] text-fg-1"
                  />
                  <span className="text-[11px] text-fg-3">{t("settings.commands.chars")}</span>
                </div>
              </Row>
            </KcCard>
          </div>

          {/* external files — backend gate (AgentSettings.AllowExternalFiles; reads ask once, writes every call) */}
          <div className="space-y-3">
            <div>
              <Overline>{t("settings.external")}</Overline>
              <p className="mt-1.5 text-[12px] leading-snug text-fg-3">{t("settings.external.hint")}</p>
            </div>
            <KcCard className="px-4 py-3">
              <ToggleRow
                label={t("settings.external.allow")}
                checked={allowExternalFiles}
                onChange={(v) => {
                  setAllowExternalFiles(v);
                  saveSoon("agent", { allowExternalFiles: v });
                }}
              />
              {allowExternalFiles ? (
                <div className="mb-1 flex items-start gap-2 rounded-sm border border-warning/30 bg-warning-soft px-2.5 py-2 text-[11.5px] leading-snug text-warning">
                  <AlertTriangle size={13} strokeWidth={2} className="mt-0.5 shrink-0" aria-hidden />
                  <span>{t("settings.external.allowWarn")}</span>
                </div>
              ) : null}
            </KcCard>
          </div>

          <div className="space-y-3">
            <div>
              <Overline>{t("settings.skills")}</Overline>
              <p className="mt-1.5 text-[12px] leading-snug text-fg-3">{t("settings.skills.hint")}</p>
            </div>
            <KcCard className="px-4 py-3">
              {SKILLS.map((skill, i) => (
                <Fragment key={skill.id}>
                  {i > 0 && <Divider />}
                  <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 py-2.5 first:pt-0 last:pb-0">
                    <div className="flex min-w-0 items-center gap-2.5">
                      <span className="shrink-0 rounded-xs border border-line bg-sunken px-1.5 py-0.5 font-mono text-[10.5px] leading-none text-fg-2">
                        {skill.name}
                      </span>
                      <span className="min-w-0 truncate text-[12px] text-fg-2">
                        {skill.description}
                      </span>
                    </div>
                    <Switch
                      checked={skills?.[skill.id] ?? skill.enabled}
                      onCheckedChange={(v) => setSkill(skill.id, v)}
                      aria-label={skill.name}
                    />
                  </div>
                </Fragment>
              ))}
            </KcCard>
          </div>
        </Section>

        {/* 5 — PROVIDERS (opencode-style studio launcher) */}
        <Section overline={t("settings.providers")}>
          <ProviderStudioLauncher />
        </Section>

        {/* 6 — SHORTCUTS */}
        <Section overline={t("settings.shortcuts")}>
          <KcCard className="px-4 py-3">
            {SHORTCUTS.map((sc, i) => (
              <Fragment key={sc.action}>
                {i > 0 && <Divider />}
                <div className="flex items-center justify-between gap-4 py-2 first:pt-0 last:pb-0">
                  <span className="min-w-0 truncate text-[13px] text-fg-2">{t(sc.action)}</span>
                  <span className="flex shrink-0 items-center gap-1">
                    {sc.keys.map((k) => (
                      <Keycap key={k}>{k}</Keycap>
                    ))}
                  </span>
                </div>
              </Fragment>
            ))}
          </KcCard>
        </Section>

        {/* 7 — DESIGN TOKENS */}
        <Section overline={t("settings.design")} hint={t("settings.tokens.hint")}>
          <KcCard className="space-y-5 p-4">
            <div>
              <Overline>{t("settings.tokens.colors")}</Overline>
              <div className="mt-2.5 grid grid-cols-2 gap-2.5 sm:grid-cols-3 md:grid-cols-4">
                {TOKEN_VARS.map(({ name }) => (
                  <div key={name} className="flex items-center gap-2.5">
                    <span
                      className="h-7 w-7 shrink-0 rounded-xs border border-line"
                      style={{ background: tokenValues[name] || "transparent" }}
                      aria-hidden
                    />
                    <div className="min-w-0">
                      <div className="font-mono text-[11px] leading-tight text-fg-2">{name}</div>
                      <div className="truncate font-mono text-[10.5px] leading-tight text-fg-3">
                        {tokenValues[name] || "—"}
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            </div>

            <Divider />

            <div className="flex flex-wrap gap-x-10 gap-y-5">
              <div>
                <Overline>{t("settings.tokens.spacing")}</Overline>
                <div className="mt-2.5 flex items-end gap-4">
                  {SPACING_SCALE.map((s) => (
                    <div key={s} className="flex flex-col items-center gap-1.5">
                      <span className="rounded-xs bg-fg-3" style={{ width: s, height: 10 }} aria-hidden />
                      <span className="font-mono text-[10px] text-fg-3">{s}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div>
                <Overline>{t("settings.tokens.radius")}</Overline>
                <div className="mt-2.5 flex items-end gap-4">
                  {RADIUS_SCALE.map((r) => (
                    <div key={r} className="flex flex-col items-center gap-1.5">
                      <span
                        className="h-8 w-8 border border-line-strong bg-surface-hover"
                        style={{ borderRadius: r }}
                        aria-hidden
                      />
                      <span className="font-mono text-[10px] text-fg-3">{r}</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>

            <Divider />

            <div>
              <Overline>{t("settings.tokens.typography")}</Overline>
              <div className="mt-2.5 space-y-2">
                <div className="flex items-baseline gap-4">
                  <span className="w-10 shrink-0 font-mono text-[10.5px] text-fg-3">13px</span>
                  <span className="text-[13px] text-fg-1">
                    {t("settings.typo.interface")} — {t("app.tagline")}
                  </span>
                </div>
                <div className="flex items-baseline gap-4">
                  <span className="w-10 shrink-0 font-mono text-[10.5px] text-fg-3">12px</span>
                  <span className="text-[12px] text-fg-2">
                    {t("settings.typo.caption")} — {t("app.tagline")}
                  </span>
                </div>
                <div className="flex items-baseline gap-4">
                  <span className="w-10 shrink-0 font-mono text-[10.5px] text-fg-3">11px</span>
                  <span className="text-[11px] text-fg-3">
                    {t("settings.typo.metadata")} — {t("app.tagline")}
                  </span>
                </div>
                <div className="flex items-baseline gap-4">
                  <span className="w-10 shrink-0 font-mono text-[10.5px] text-fg-3">12.5px</span>
                  <span className="font-mono text-[12.5px] text-fg-1">
                    {t("settings.typo.mono")} — TokenService.Renew
                  </span>
                </div>
              </div>
            </div>
          </KcCard>
        </Section>

        {/* 8 — ABOUT */}
        <Section overline={t("settings.about")}>
          <KcCard className="p-4">
            <div className="flex items-center gap-3">
              <span
                className="flex h-8 w-8 shrink-0 items-center justify-center rounded-sm bg-accent text-on-accent"
                aria-hidden
              >
                <Terminal size={15} strokeWidth={2} />
              </span>
              <div className="min-w-0">
                <div className="text-[13.5px] font-semibold leading-tight text-fg-1">
                  {t("app.name")}
                </div>
                <div className="mt-0.5 text-[11.5px] leading-tight text-fg-3">
                  {t("settings.version")}
                </div>
              </div>
            </div>

            <Divider className="my-3.5" />

            <p className="text-[12px] leading-relaxed text-fg-2">{t("settings.about.line")}</p>
            <p className="mt-1.5 text-[12px] leading-relaxed text-fg-3">{t("settings.durability")}</p>

            <a
              href="https://github.com/rwarx/kontur-code"
              target="_blank"
              rel="noreferrer"
              className="mt-3 inline-flex items-center gap-1 text-[12px] font-medium text-accent hover:underline"
            >
              {t("settings.about.repo")}
              <ArrowUpRight size={12} strokeWidth={2} aria-hidden />
            </a>
          </KcCard>
        </Section>
      </div>
    </div>
  );
}
