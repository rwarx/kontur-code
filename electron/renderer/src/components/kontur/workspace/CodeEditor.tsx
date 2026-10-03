"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { EditorState, Prec, StateEffect, StateField, type Extension } from "@codemirror/state";
import {
  EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter,
  highlightSpecialChars, drawSelection, dropCursor, rectangularSelection, crosshairCursor,
  Decoration, WidgetType,
} from "@codemirror/view";
import {
  syntaxHighlighting, HighlightStyle, indentOnInput, bracketMatching, foldGutter,
  foldKeymap, StreamLanguage,
} from "@codemirror/language";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { searchKeymap, highlightSelectionMatches } from "@codemirror/search";
import {
  autocompletion, completionKeymap, closeBrackets, closeBracketsKeymap,
} from "@codemirror/autocomplete";
import { tags } from "@lezer/highlight";
import { javascript } from "@codemirror/lang-javascript";
import { json } from "@codemirror/lang-json";
import { html } from "@codemirror/lang-html";
import { css } from "@codemirror/lang-css";
import { markdown } from "@codemirror/lang-markdown";
import { python } from "@codemirror/lang-python";
import { xml } from "@codemirror/lang-xml";
import { rust } from "@codemirror/lang-rust";
import { cpp } from "@codemirror/lang-cpp";
import { java } from "@codemirror/lang-java";
import { csharp } from "@codemirror/legacy-modes/mode/clike";
import { go } from "@codemirror/legacy-modes/mode/go";
import { yaml } from "@codemirror/legacy-modes/mode/yaml";
import { toml } from "@codemirror/legacy-modes/mode/toml";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { Ban, Check, CheckCheck, CornerDownLeft, Loader2, RotateCcw, Sparkles, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { ai } from "@/lib/kontur/backend";
import { isServerMode, splitCompositeModel } from "@/lib/kontur/sync";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { applyHunkSelection, computeHunks, hunkDiffLines } from "@/lib/kontur/diff";
import { DiffView } from "../chat/DiffView";
import { KcGhostButton, KcPrimaryButton } from "@/components/kontur/ui";

/* Syntax palette bound to the app's --kc-syntax-* tokens so highlighting
   tracks the light/dark theme with the rest of the UI. */
const kcHighlight = HighlightStyle.define([
  { tag: [tags.keyword, tags.controlKeyword, tags.operatorKeyword, tags.modifier, tags.definitionKeyword, tags.moduleKeyword], color: "var(--kc-syntax-kw)" },
  { tag: [tags.typeName, tags.className, tags.namespace, tags.tagName], color: "var(--kc-syntax-type)" },
  { tag: [tags.string, tags.special(tags.string), tags.regexp, tags.character, tags.attributeValue], color: "var(--kc-syntax-str)" },
  { tag: [tags.number, tags.integer, tags.float, tags.bool, tags.null, tags.atom, tags.unit], color: "var(--kc-syntax-num)" },
  { tag: [tags.comment, tags.lineComment, tags.blockComment, tags.docComment], color: "var(--kc-syntax-comment)", fontStyle: "italic" },
  { tag: [tags.function(tags.variableName), tags.function(tags.propertyName), tags.macroName], color: "var(--kc-syntax-fn)" },
  { tag: [tags.operator, tags.punctuation, tags.bracket, tags.derefOperator], color: "var(--kc-syntax-op)" },
  { tag: [tags.propertyName, tags.attributeName], color: "var(--kc-fg-1)" },
  { tag: [tags.heading], color: "var(--kc-syntax-kw)", fontWeight: "600" },
  { tag: [tags.strong], fontWeight: "600" },
  { tag: [tags.emphasis], fontStyle: "italic" },
  { tag: [tags.link, tags.url], color: "var(--kc-syntax-fn)", textDecoration: "underline" },
  { tag: [tags.invalid], color: "var(--kc-error)" },
]);

/* Editor chrome bound to the same tokens (transparent bg → inherits bg-app). */
const kcTheme = EditorView.theme({
  "&": { color: "var(--kc-fg-1)", backgroundColor: "transparent", height: "100%" },
  ".cm-scroller": { fontFamily: "var(--font-mono)", fontSize: "12.5px", lineHeight: "1.55", overflow: "auto" },
  ".cm-content": { caretColor: "var(--kc-accent)", padding: "8px 0" },
  ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--kc-accent)", borderLeftWidth: "2px" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection": { backgroundColor: "var(--kc-accent-soft)" },
  ".cm-panels": { backgroundColor: "var(--kc-surface-2)", color: "var(--kc-fg-1)", borderColor: "var(--kc-line)" },
  ".cm-searchMatch": { backgroundColor: "var(--kc-warning-soft)", outline: "1px solid var(--kc-warning)" },
  ".cm-searchMatch.cm-searchMatch-selected": { backgroundColor: "var(--kc-accent-soft)" },
  ".cm-selectionMatch": { backgroundColor: "var(--kc-accent-soft)" },
  "&.cm-focused .cm-matchingBracket": { backgroundColor: "var(--kc-accent-soft)", outline: "1px solid var(--kc-accent-border)" },
  ".cm-gutters": { backgroundColor: "var(--kc-bg)", color: "var(--kc-fg-3)", border: "none", borderRight: "1px solid var(--kc-line-faint)" },
  ".cm-activeLineGutter": { backgroundColor: "color-mix(in srgb, var(--kc-surface-hover) 55%, transparent)", color: "var(--kc-fg-2)" },
  ".cm-activeLine": { backgroundColor: "color-mix(in srgb, var(--kc-surface-hover) 30%, transparent)" },
  ".cm-lineNumbers .cm-gutterElement": { padding: "0 8px 0 14px", minWidth: "34px" },
  ".cm-foldPlaceholder": { backgroundColor: "var(--kc-surface-2)", border: "1px solid var(--kc-line)", color: "var(--kc-fg-2)" },
  ".cm-tooltip": { backgroundColor: "var(--kc-surface-3)", border: "1px solid var(--kc-line)", color: "var(--kc-fg-1)", borderRadius: "6px" },
  ".cm-tooltip.cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: "var(--kc-accent-soft)", color: "var(--kc-fg-1)" },
  ".cm-ghost-text": { color: "var(--kc-fg-3)", opacity: "0.5", fontStyle: "italic" },
});

/* Map a file path to the right language extension(s). Unknown → no language
   (plain text still gets the full editing UX, just no token colouring). */
function languageForPath(path: string): Extension[] {
  const p = path.toLowerCase();
  const ext = p.includes(".") ? p.slice(p.lastIndexOf(".") + 1) : "";
  switch (ext) {
    case "ts":
    case "mts":
    case "cts":
      return [javascript({ typescript: true })];
    case "tsx":
      return [javascript({ typescript: true, jsx: true })];
    case "js":
    case "mjs":
    case "cjs":
      return [javascript()];
    case "jsx":
      return [javascript({ jsx: true })];
    case "json":
      return [json()];
    case "html":
    case "htm":
      return [html()];
    case "css":
    case "scss":
    case "less":
      return [css()];
    case "md":
    case "markdown":
      return [markdown()];
    case "py":
      return [python()];
    case "xml":
    case "csproj":
    case "props":
    case "targets":
    case "svg":
      return [xml()];
    case "rs":
      return [rust()];
    case "c":
    case "h":
    case "cc":
    case "cpp":
    case "hpp":
    case "cxx":
      return [cpp()];
    case "java":
      return [java()];
    case "cs":
      return [StreamLanguage.define(csharp)];
    case "go":
      return [StreamLanguage.define(go)];
    case "yaml":
    case "yml":
      return [StreamLanguage.define(yaml)];
    case "toml":
      return [StreamLanguage.define(toml)];
    case "sh":
    case "bash":
    case "zsh":
      return [StreamLanguage.define(shell)];
    default:
      return [];
  }
}

/* Baseline editing extensions shared by every language. */
function baseExtensions(): Extension[] {
  return [
    lineNumbers(),
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    foldGutter(),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    syntaxHighlighting(kcHighlight, { fallback: true }),
    bracketMatching(),
    closeBrackets(),
    autocompletion(),
    rectangularSelection(),
    crosshairCursor(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    keymap.of([
      ...closeBracketsKeymap,
      ...defaultKeymap,
      ...searchKeymap,
      ...historyKeymap,
      ...foldKeymap,
      ...completionKeymap,
      indentWithTab,
    ]),
    kcTheme,
    EditorView.lineWrapping,
  ];
}

/* ============================================================
   Ghost-text inline completion (Tier 1 #7). A StateField holds
   the pending suggestion + its anchor; a widget decoration paints
   it after the caret. Tab accepts; Escape or any edit/selection
   change clears it. Requests are debounced and server-gated.
   ============================================================ */
interface GhostValue {
  text: string;
  from: number;
}

const setGhostEffect = StateEffect.define<GhostValue | null>();

class GhostWidget extends WidgetType {
  constructor(readonly text: string) {
    super();
  }
  eq(other: GhostWidget) {
    return other.text === this.text;
  }
  toDOM() {
    const span = document.createElement("span");
    span.className = "cm-ghost-text";
    span.textContent = this.text;
    return span;
  }
  ignoreEvent() {
    return false;
  }
}

const ghostField = StateField.define<GhostValue | null>({
  create() {
    return null;
  },
  update(value, tr) {
    for (const e of tr.effects) if (e.is(setGhostEffect)) return e.value;
    if (value && (tr.docChanged || tr.selection)) return null;
    return value;
  },
  provide: (f) =>
    EditorView.decorations.from(f, (value) =>
      value
        ? Decoration.set([
            Decoration.widget({ widget: new GhostWidget(value.text), side: 1 }).range(value.from),
          ])
        : Decoration.none,
    ),
});

function currentGhost(view: EditorView): GhostValue | null {
  return view.state.field(ghostField, false) ?? null;
}

function acceptGhost(view: EditorView): boolean {
  const ghost = currentGhost(view);
  if (!ghost) return false;
  view.dispatch({
    changes: { from: ghost.from, insert: ghost.text },
    selection: { anchor: ghost.from + ghost.text.length },
    effects: setGhostEffect.of(null),
    userEvent: "input.complete",
  });
  return true;
}

function dismissGhost(view: EditorView): boolean {
  if (!currentGhost(view)) return false;
  view.dispatch({ effects: setGhostEffect.of(null) });
  return true;
}

/* Strip a fenced ```lang … ``` wrapper if the model added one; otherwise just
   trim trailing whitespace (leading indentation is meaningful for insertion). */
function stripCodeFences(s: string): string {
  const trimmed = s.trim();
  const m = trimmed.match(/^```[^\n]*\n([\s\S]*?)\n?```$/);
  return m ? m[1] : s.replace(/\s+$/, "");
}

const GHOST_SYSTEM =
  "You are an inline code completion engine. Continue the code at the ⟨CURSOR⟩ marker. " +
  "Output ONLY the raw text to insert at the cursor — no explanations, no Markdown fences, " +
  "and never repeat the code that precedes the cursor. Keep it short: finish the current line " +
  "or a small block. Output nothing if no sensible completion exists.";

const INLINE_SYSTEM =
  "You are a precise coding assistant embedded in an editor. The user selected a region of code " +
  "and gave an instruction. Rewrite ONLY that region and output the replacement verbatim — no " +
  "explanations and no Markdown code fences. Preserve the surrounding indentation style.";

function buildInlinePrompt(region: string, instruction: string, path: string): string {
  return `File: ${path}\nInstruction: ${instruction}\n\nSelected code:\n${region}`;
}

interface InlineState {
  from: number;
  to: number;
  original: string;
  top: number;
}

type InlinePhase = "input" | "loading" | "review" | "error";

interface CodeEditorProps {
  path: string;
  content: string;
  onChange: (next: string) => void;
  className?: string;
  /** enable LLM ghost-text inline completion (Tier 1 #7) */
  ghostText?: boolean;
}

/* Per-hunk accept/reject review for an inline AI edit. Defaults to all hunks
   accepted; Apply is ALWAYS enabled (unlike the checkpoint HunkReview) so the
   full suggestion can be applied even when it equals the current selection. */
function InlineDiffReview({
  original,
  suggestion,
  onApply,
  onTweak,
  onCancel,
}: {
  original: string;
  suggestion: string;
  onApply: (merged: string) => void;
  onTweak: () => void;
  onCancel: () => void;
}) {
  const t = useT();
  const hunks = useMemo(() => computeHunks(original, suggestion), [original, suggestion]);
  const [accepted, setAccepted] = useState<Set<number>>(() => new Set(hunks.map((h) => h.index)));

  useEffect(() => {
    setAccepted(new Set(hunks.map((h) => h.index)));
  }, [hunks]);

  const noChange = hunks.length === 0 || suggestion === original;
  const merged = noChange ? original : applyHunkSelection(original, suggestion, accepted);

  const toggle = (i: number) =>
    setAccepted((prev) => {
      const next = new Set(prev);
      if (next.has(i)) next.delete(i);
      else next.add(i);
      return next;
    });

  if (noChange) {
    return (
      <div className="space-y-2.5">
        <p className="rounded-sm border border-line-faint bg-surface-1 px-2.5 py-2 text-[11.5px] text-fg-3">
          {t("code.inlineAi.noChange")}
        </p>
        <div className="flex items-center justify-end gap-1.5">
          <KcGhostButton onClick={onTweak}>
            <RotateCcw size={12} />
            {t("code.inlineAi.tweak")}
          </KcGhostButton>
          <KcGhostButton onClick={onCancel}>{t("common.close")}</KcGhostButton>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-2.5">
      <div className="flex items-center gap-1.5">
        <button
          type="button"
          onClick={() => setAccepted(new Set(hunks.map((h) => h.index)))}
          className="flex h-6 items-center gap-1 rounded-xs border border-line bg-surface-1 px-2 text-[11px] font-medium text-fg-2 transition-colors hover:text-success"
        >
          <CheckCheck size={12} />
          {t("code.inlineAi.acceptAll")}
        </button>
        <button
          type="button"
          onClick={() => setAccepted(new Set())}
          className="flex h-6 items-center gap-1 rounded-xs border border-line bg-surface-1 px-2 text-[11px] font-medium text-fg-2 transition-colors hover:text-error"
        >
          <Ban size={12} />
          {t("code.inlineAi.rejectAll")}
        </button>
        <span className="ml-auto font-mono text-[10px] text-fg-3">
          {t("code.inlineAi.hunkCount", accepted.size, hunks.length)}
        </span>
      </div>

      <div className="max-h-[240px] space-y-2 overflow-y-auto">
        {hunks.map((h) => {
          const on = accepted.has(h.index);
          return (
            <div
              key={h.index}
              className={cn(
                "overflow-hidden rounded-sm border transition-colors",
                on ? "border-accent/40" : "border-line-faint opacity-60",
              )}
            >
              <div className="flex items-center gap-1.5 border-b border-line-faint bg-surface-1 px-2 py-1">
                <span className="font-mono text-[10px] text-fg-3">#{h.index + 1}</span>
                <button
                  type="button"
                  onClick={() => toggle(h.index)}
                  className={cn(
                    "ml-auto flex h-5 items-center gap-1 rounded-xs px-1.5 text-[10.5px] font-medium transition-colors",
                    on ? "bg-success-soft text-success" : "text-fg-3 hover:text-fg-1",
                  )}
                >
                  {on ? <Check size={11} /> : <X size={11} />}
                  {on ? t("code.inlineAi.accept") : t("code.inlineAi.reject")}
                </button>
              </div>
              <DiffView lines={hunkDiffLines(h)} maxHeight={100000} />
            </div>
          );
        })}
      </div>

      <div className="flex items-center justify-end gap-1.5">
        <KcGhostButton onClick={onTweak}>
          <RotateCcw size={12} />
          {t("code.inlineAi.tweak")}
        </KcGhostButton>
        <KcPrimaryButton onClick={() => onApply(merged)}>
          <Check size={12} />
          {t("code.inlineAi.apply")}
        </KcPrimaryButton>
      </div>
    </div>
  );
}

/* Editable CodeMirror 6 surface. Mounted once per file (keyed by path upstream);
   local edits flow out via onChange, external content changes reconcile in
   without stomping the caret. Cursor-style inline AI edits (Mod-K) and
   ghost-text completion layer on top. */
export function CodeEditor({ path, content, onChange, className, ghostText }: CodeEditorProps) {
  const t = useT();
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewRef = useRef<EditorView | null>(null);
  const onChangeRef = useRef(onChange);
  const syncingRef = useRef(false);
  const ghostEnabledRef = useRef(!!ghostText);
  const openInlineRef = useRef<(s: InlineState) => void>(() => {});
  const acRef = useRef<AbortController | null>(null);

  const [inline, setInline] = useState<InlineState | null>(null);
  const [phase, setPhase] = useState<InlinePhase>("input");
  const [instruction, setInstruction] = useState("");
  const [suggestion, setSuggestion] = useState("");
  const [errorMsg, setErrorMsg] = useState("");

  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  /* the CM keydown handler is built once (keyed by path) and reaches inline-edit
     opening through this stable ref so it always calls the latest React setter */
  useEffect(() => {
    openInlineRef.current = (s: InlineState) => {
      setInline(s);
      setPhase("input");
      setInstruction("");
      setSuggestion("");
      setErrorMsg("");
    };
  }, []);

  /* mirror the ghost-text toggle into a ref the editor closures read live;
     clear any shown suggestion the moment it is turned off */
  useEffect(() => {
    ghostEnabledRef.current = !!ghostText;
    if (!ghostText && viewRef.current) dismissGhost(viewRef.current);
  }, [ghostText]);

  const closeInline = () => {
    acRef.current?.abort();
    acRef.current = null;
    setInline(null);
    setPhase("input");
    setInstruction("");
    setSuggestion("");
    setErrorMsg("");
    viewRef.current?.focus();
  };

  const runInline = async () => {
    if (!viewRef.current || !inline) return;
    const instr = instruction.trim();
    if (!instr) return;
    if (!isServerMode()) {
      setErrorMsg(t("code.inlineAi.needServer"));
      setPhase("error");
      return;
    }
    const { providerId, modelId } = splitCompositeModel(useKontur.getState().ui.selectedModelId);
    if (!providerId || !modelId) {
      setErrorMsg(t("code.inlineAi.noModel"));
      setPhase("error");
      return;
    }
    acRef.current?.abort();
    const ac = new AbortController();
    acRef.current = ac;
    setPhase("loading");
    try {
      const res = await ai.complete(
        {
          providerId,
          modelId,
          system: INLINE_SYSTEM,
          prompt: buildInlinePrompt(inline.original, instr, path),
          temperature: 0.2,
          maxTokens: 2048,
        },
        ac.signal,
      );
      if (ac.signal.aborted) return;
      setSuggestion(stripCodeFences(res.text));
      setPhase("review");
    } catch (err) {
      if (ac.signal.aborted || (err instanceof DOMException && err.name === "AbortError")) return;
      setErrorMsg(err instanceof Error ? err.message : t("code.inlineAi.needServer"));
      setPhase("error");
    }
  };

  const applyInline = (merged: string) => {
    const view = viewRef.current;
    if (!view || !inline) return;
    const docLen = view.state.doc.length;
    const from = Math.min(inline.from, docLen);
    const to = Math.min(inline.to, docLen);
    view.dispatch({
      changes: { from, to, insert: merged },
      selection: { anchor: from, head: from + merged.length },
      userEvent: "input.aiedit",
    });
    closeInline();
  };

  /* Create the view once, tied to this file's initial content + language. */
  useEffect(() => {
    if (!hostRef.current) return;

    let ghostTimer: ReturnType<typeof setTimeout> | null = null;
    let ghostAc: AbortController | null = null;
    let reqSeq = 0;

    const requestGhost = async (view: EditorView) => {
      if (!ghostEnabledRef.current || !isServerMode()) return;
      const sel = view.state.selection.main;
      if (!sel.empty) return;
      const pos = sel.head;
      const docLen = view.state.doc.length;
      const prefix = view.state.doc.sliceString(Math.max(0, pos - 4000), pos);
      const suffix = view.state.doc.sliceString(pos, Math.min(docLen, pos + 1500));
      if (!prefix.trim()) return;
      const { providerId, modelId } = splitCompositeModel(useKontur.getState().ui.selectedModelId);
      if (!providerId || !modelId) return;
      ghostAc?.abort();
      const ac = new AbortController();
      ghostAc = ac;
      const seq = ++reqSeq;
      try {
        const res = await ai.complete(
          { providerId, modelId, system: GHOST_SYSTEM, prompt: `${prefix}⟨CURSOR⟩${suffix}`, temperature: 0.1, maxTokens: 96 },
          ac.signal,
        );
        if (ac.signal.aborted || seq !== reqSeq) return;
        /* only paint if the caret + doc are exactly where we requested from */
        const now = view.state.selection.main;
        if (!now.empty || now.head !== pos || view.state.doc.length !== docLen) return;
        let text = stripCodeFences(res.text);
        if (!text) return;
        if (text.length > 400) text = text.slice(0, 400);
        view.dispatch({ effects: setGhostEffect.of({ text, from: pos }) });
      } catch {
        /* aborted or provider refusal — ghost-text stays silent */
      }
    };

    const scheduleGhost = (view: EditorView) => {
      if (ghostTimer) clearTimeout(ghostTimer);
      ghostTimer = setTimeout(() => void requestGhost(view), 450);
    };

    const updateListener = EditorView.updateListener.of((u) => {
      if (u.docChanged && !syncingRef.current) onChangeRef.current(u.state.doc.toString());
      const justCompleted = u.transactions.some((tr) => tr.isUserEvent("input.complete"));
      if (
        (u.docChanged || u.selectionSet) &&
        !justCompleted &&
        ghostEnabledRef.current &&
        isServerMode() &&
        u.state.selection.main.empty
      ) {
        scheduleGhost(u.view);
      }
    });

    /* highest-precedence DOM handler so Mod-K / Tab / Escape win over the
       default keymaps and the app's global keyboard layer */
    const inlineKeys = Prec.highest(
      EditorView.domEventHandlers({
        keydown: (e, view) => {
          const mod = e.ctrlKey || e.metaKey;
          if (mod && (e.key === "k" || e.key === "K")) {
            e.preventDefault();
            e.stopPropagation();
            const sel = view.state.selection.main;
            let from = sel.from;
            let to = sel.to;
            if (from === to) {
              const line = view.state.doc.lineAt(from);
              from = line.from;
              to = line.to;
            }
            const coords = view.coordsAtPos(from);
            const hostTop = hostRef.current?.getBoundingClientRect().top ?? 0;
            openInlineRef.current({
              from,
              to,
              original: view.state.sliceDoc(from, to),
              top: coords ? coords.bottom - hostTop + 6 : 12,
            });
            return true;
          }
          if (e.key === "Tab" && currentGhost(view)) {
            e.preventDefault();
            e.stopPropagation();
            return acceptGhost(view);
          }
          if (e.key === "Escape" && currentGhost(view)) {
            e.stopPropagation();
            return dismissGhost(view);
          }
          return false;
        },
      }),
    );

    const view = new EditorView({
      parent: hostRef.current,
      state: EditorState.create({
        doc: content,
        extensions: [...baseExtensions(), ...languageForPath(path), ghostField, inlineKeys, updateListener],
      }),
    });
    viewRef.current = view;
    return () => {
      if (ghostTimer) clearTimeout(ghostTimer);
      ghostAc?.abort();
      view.destroy();
      viewRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path]);

  /* Reconcile external content changes (e.g. an agent write) without clobbering
     in-progress local editing or the caret. */
  useEffect(() => {
    const view = viewRef.current;
    if (!view) return;
    const current = view.state.doc.toString();
    if (current === content) return;
    syncingRef.current = true;
    view.dispatch({ changes: { from: 0, to: current.length, insert: content } });
    syncingRef.current = false;
  }, [content]);

  const selLineCount = inline ? (inline.original.length ? inline.original.split("\n").length : 0) : 0;

  return (
    <div className={cn("relative h-full w-full", className)}>
      <div ref={hostRef} className="h-full w-full overflow-hidden" />
      {inline && (
        <div
          className="animate-rise absolute left-1/2 z-20 w-[min(600px,94%)] -translate-x-1/2 overflow-hidden rounded-lg border border-line bg-surface-2 shadow-overlay"
          style={{ top: inline.top }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <div className="flex items-center gap-2 border-b border-line-faint bg-surface-3/60 px-3 py-2">
            <Sparkles size={13} className="text-accent" />
            <span className="text-[12px] font-semibold text-fg-1">{t("code.inlineAi.title")}</span>
            <span className="font-mono text-[10px] text-fg-3">{t("code.inlineAi.selLines", selLineCount)}</span>
            <button
              type="button"
              onClick={closeInline}
              aria-label={t("common.close")}
              className="ml-auto flex h-5 w-5 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
            >
              <X size={13} />
            </button>
          </div>
          <div className="p-3">
            {phase === "review" ? (
              <InlineDiffReview
                original={inline.original}
                suggestion={suggestion}
                onApply={applyInline}
                onTweak={() => setPhase("input")}
                onCancel={closeInline}
              />
            ) : (
              <div className="space-y-2">
                <textarea
                  autoFocus
                  value={instruction}
                  disabled={phase === "loading"}
                  onChange={(e) => setInstruction(e.target.value)}
                  onKeyDown={(e) => {
                    e.stopPropagation();
                    if (e.key === "Enter" && !e.shiftKey) {
                      e.preventDefault();
                      void runInline();
                    } else if (e.key === "Escape") {
                      e.preventDefault();
                      closeInline();
                    }
                  }}
                  rows={2}
                  placeholder={t("code.inlineAi.placeholder")}
                  className="w-full resize-none rounded-sm border border-line bg-sunken px-2.5 py-2 text-[12.5px] text-fg-1 outline-none placeholder:text-fg-3 focus:border-accent-border"
                />
                {phase === "error" && <p className="text-[11.5px] text-error">{errorMsg}</p>}
                <div className="flex items-center justify-between gap-2">
                  <span className="text-[10.5px] text-fg-3">{t("code.inlineAi.hint")}</span>
                  <KcPrimaryButton onClick={() => void runInline()} disabled={phase === "loading" || !instruction.trim()}>
                    {phase === "loading" ? <Loader2 size={12} className="animate-spin" /> : <CornerDownLeft size={12} />}
                    {t("code.inlineAi.generate")}
                  </KcPrimaryButton>
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
