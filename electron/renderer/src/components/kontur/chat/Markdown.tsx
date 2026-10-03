"use client";

import { useMemo, useState } from "react";
import { Check, Copy } from "lucide-react";

/* ============================================================
   Lightweight markdown renderer tuned for chat answers:
   headings, bold, inline code, fenced code, lists.
   ============================================================ */

const KEYWORDS = new Set([
  "using", "namespace", "public", "sealed", "class", "record", "return", "if", "else",
  "var", "new", "null", "private", "readonly", "string", "bool", "void", "int", "true",
  "false", "async", "await", "is", "not", "in", "for", "foreach", "while", "throw",
  "try", "catch", "static", "this", "base", "override", "virtual", "interface", "enum",
]);

function highlightLine(line: string, keyPrefix: string) {
  const parts: React.ReactNode[] = [];
  const regex =
    /(\/\/.*$)|("(?:[^"\\]|\\.)*")|(\b\d+(?:\.\d+)?[a-z]*\b)|([A-Za-z_][A-Za-z0-9_]*)|(\s+)|(.)/g;
  let match: RegExpExecArray | null;
  let i = 0;
  let plain = "";
  const flush = () => {
    if (plain) {
      parts.push(<span key={`${keyPrefix}-p${i++}`}>{plain}</span>);
      plain = "";
    }
  };
  while ((match = regex.exec(line))) {
    const [full, comment, str, num, word] = match;
    if (comment) {
      flush();
      parts.push(
        <span key={`${keyPrefix}-c${i++}`} style={{ color: "var(--kc-syntax-comment)" }}>
          {comment}
        </span>,
      );
    } else if (str) {
      flush();
      parts.push(
        <span key={`${keyPrefix}-s${i++}`} style={{ color: "var(--kc-syntax-str)" }}>
          {str}</span>,
      );
    } else if (num) {
      flush();
      parts.push(
        <span key={`${keyPrefix}-n${i++}`} style={{ color: "var(--kc-syntax-num)" }}>
          {num}
        </span>,
      );
    } else if (word) {
      const isCall = line[regex.lastIndex] === "(";
      if (KEYWORDS.has(word)) {
        flush();
        parts.push(
          <span key={`${keyPrefix}-k${i++}`} style={{ color: "var(--kc-syntax-kw)" }}>
            {word}
          </span>,
        );
      } else if (isCall) {
        flush();
        parts.push(
          <span key={`${keyPrefix}-f${i++}`} style={{ color: "var(--kc-syntax-fn)" }}>
            {word}
          </span>,
        );
      } else if (/^[A-Z]/.test(word)) {
        flush();
        parts.push(
          <span key={`${keyPrefix}-t${i++}`} style={{ color: "var(--kc-syntax-type)" }}>
            {word}
          </span>,
        );
      } else {
        plain += full;
      }
    } else {
      plain += full;
    }
  }
  flush();
  return parts;
}

/* Exported for the Code (read-only) surface */
export function highlightCodeLine(line: string, keyPrefix: string) {
  return highlightLine(line, keyPrefix);
}

export function CodeBlock({ code, language }: { code: string; language?: string }) {
  const [copied, setCopied] = useState(false);
  const lines = useMemo(() => code.replace(/\n$/, "").split("\n"), [code]);
  return (
    <div className="group/code my-2 overflow-hidden rounded-md border border-line-faint bg-sunken">
      <div className="flex items-center justify-between border-b border-line-faint px-3 py-1.5">
        <span className="font-mono text-[10.5px] uppercase tracking-wider text-fg-3">
          {language || "code"}
        </span>
        <button
          type="button"
          onClick={() => {
            void navigator.clipboard?.writeText(code);
            setCopied(true);
            setTimeout(() => setCopied(false), 1400);
          }}
          className="flex h-6 w-6 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
          aria-label="Copy code"
        >
          {copied ? <Check size={12} className="text-success" /> : <Copy size={12} />}
        </button>
      </div>
      <pre className="overflow-x-auto px-3 py-2.5 font-mono text-[12.5px] leading-[1.55] text-fg-1">
        {lines.map((line, i) => (
          <div key={i} className="min-h-[1.55em] whitespace-pre">
            {highlightLine(line, `l${i}`)}
          </div>
        ))}
      </pre>
    </div>
  );
}

function InlineText({ text }: { text: string }) {
  const parts = useMemo(() => {
    const out: React.ReactNode[] = [];
    const regex = /(\*\*[^*]+\*\*)|(`[^`]+`)/g;
    let last = 0;
    let match: RegExpExecArray | null;
    let i = 0;
    while ((match = regex.exec(text))) {
      if (match.index > last) out.push(text.slice(last, match.index));
      if (match[1]) {
        out.push(
          <strong key={`b${i++}`} className="font-semibold text-fg-1">
            {match[1].slice(2, -2)}
          </strong>,
        );
      } else if (match[2]) {
        out.push(
          <code
            key={`c${i++}`}
            className="rounded-[4px] border border-line-faint bg-sunken px-1 py-[1px] font-mono text-[0.86em] text-fg-1"
          >
            {match[2].slice(1, -1)}
          </code>,
        );
      }
      last = regex.lastIndex;
    }
    if (last < text.length) out.push(text.slice(last));
    return out;
  }, [text]);
  return <>{parts}</>;
}

type MdBlock =
  | { type: "heading"; level: number; text: string }
  | { type: "code"; language: string; code: string }
  | { type: "list"; items: string[] }
  | { type: "paragraph"; text: string };

function parseMarkdown(src: string): MdBlock[] {
  const blocks: MdBlock[] = [];
  const fenced = src.split(/```/);
  fenced.forEach((chunk, idx) => {
    if (idx % 2 === 1) {
      const nl = chunk.indexOf("\n");
      const language = nl > 0 ? chunk.slice(0, nl).trim() : "";
      const code = nl > 0 ? chunk.slice(nl + 1) : chunk;
      blocks.push({ type: "code", language, code });
      return;
    }
    const paragraphs = chunk.split(/\n{2,}/);
    for (const p of paragraphs) {
      const trimmed = p.replace(/^\n+|\n+$/g, "");
      if (!trimmed.trim()) continue;
      const heading = trimmed.match(/^(#{1,4})\s+(.*)$/);
      if (heading && trimmed.startsWith("#")) {
        blocks.push({ type: "heading", level: heading[1].length, text: heading[2] });
        continue;
      }
      const lines = trimmed.split("\n");
      if (lines.every((l) => /^\s*[-*]\s+/.test(l)) && lines.length > 0) {
        blocks.push({ type: "list", items: lines.map((l) => l.replace(/^\s*[-*]\s+/, "")) });
        continue;
      }
      blocks.push({ type: "paragraph", text: trimmed });
    }
  });
  return blocks;
}

export function Markdown({ text }: { text: string }) {
  const blocks = useMemo(() => parseMarkdown(text), [text]);
  return (
    <div className="text-[13.5px] leading-[1.62] text-fg-1">
      {blocks.map((b, i) => {
        if (b.type === "heading") {
          const size = b.level <= 2 ? "text-[15px]" : "text-[13.5px]";
          return (
            <div key={i} className={`mt-3 mb-1 font-semibold ${i === 0 ? "mt-0" : ""} ${size}`}>
              <InlineText text={b.text} />
            </div>
          );
        }
        if (b.type === "code") {
          return <CodeBlock key={i} code={b.code} language={b.language} />;
        }
        if (b.type === "list") {
          return (
            <ul key={i} className="my-1.5 space-y-1">
              {b.items.map((item, j) => (
                <li key={j} className="flex gap-2">
                  {item.startsWith("✓") || item.startsWith("✔") ? (
                    <span className="mt-[3px] shrink-0 text-success" aria-hidden>
                      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3">
                        <path d="M20 6 9 17l-5-5" />
                      </svg>
                    </span>
                  ) : (
                    <span className="mt-[9px] h-[3px] w-[3px] shrink-0 rounded-full bg-fg-3" aria-hidden />
                  )}
                  <span>
                    <InlineText text={item.replace(/^[✓✔]\s*/, "")} />
                  </span>
                </li>
              ))}
            </ul>
          );
        }
        return (
          <p key={i} className="my-1.5 first:mt-0 last:mb-0">
            <InlineText text={b.text} />
          </p>
        );
      })}
    </div>
  );
}
