"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Archive,
  Check,
  Download,
  FileCode2,
  FileJson,
  FileText,
  Folder,
  Loader2,
  Package,
  RefreshCw,
  Search,
  Terminal,
  X,
} from "lucide-react";
import { useT } from "@/lib/kontur/useT";
import { KcToolButton, Overline } from "@/components/kontur/ui";

/* manifest shape returned by GET /api/project */
interface ManifestFile {
  path: string;
  bytes: number;
  lines: number;
}
interface Manifest {
  generatedAt: string;
  files: ManifestFile[];
  totals: { files: number; bytes: number; lines: number; groups: number };
}

function kb(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

function fileIcon(p: string) {
  if (p.endsWith(".json")) return FileJson;
  if (p.endsWith(".ts") || p.endsWith(".tsx") || p.endsWith(".prisma")) return FileCode2;
  return FileText;
}

function triggerDownload(url: string) {
  const a = document.createElement("a");
  a.href = url;
  a.rel = "noopener";
  document.body.appendChild(a);
  a.click();
  a.remove();
}

export function ProjectExportDialog({ onClose }: { onClose: () => void }) {
  const t = useT();

  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [error, setError] = useState(false);
  const [filter, setFilter] = useState("");
  const [downloaded, setDownloaded] = useState<null | "zip" | "single">(null);
  const filterRef = useRef<HTMLInputElement>(null);

  /* fetch the manifest on mount (fresh tree, no cache) */
  useEffect(() => {
    let alive = true;
    fetch("/api/project", { cache: "no-store" })
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
      .then((m: Manifest) => alive && setManifest(m))
      .catch(() => alive && setError(true));
    return () => {
      alive = false;
    };
  }, []);

  /* Escape closes — capture phase so the global runner chain never sees it */
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        onClose();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [onClose]);

  /* NB: deliberately NO autofocus on the filter input — it sits below the
   * download cards; focusing it would scroll the body and hide the primary
   * actions on short viewports (found via agent-browser hit-target QA). */

  /* group files by their first path segment for the tree */
  const groups = useMemo(() => {
    if (!manifest) return [] as { name: string; files: ManifestFile[] }[];
    const q = filter.trim().toLowerCase();
    const map = new Map<string, ManifestFile[]>();
    for (const f of manifest.files) {
      if (q && !f.path.toLowerCase().includes(q)) continue;
      const top = f.path.includes("/") ? f.path.split("/")[0] : "(root)";
      if (!map.has(top)) map.set(top, []);
      map.get(top)!.push(f);
    }
    return [...map.entries()]
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([name, files]) => ({ name, files }));
  }, [manifest, filter]);

  const stat = (label: string, value: string) => (
    <div className="flex min-w-[86px] flex-col gap-0.5 rounded-md border border-line-faint bg-sunken px-3 py-2">
      <span className="font-mono text-[10px] uppercase tracking-[0.1em] text-fg-3">{label}</span>
      <span className="font-mono text-[14px] font-semibold tabular-nums text-fg-1">{value}</span>
    </div>
  );

  const downloadCard = (kind: "zip" | "single") => {
    const isZip = kind === "zip";
    const href = isZip ? "/api/project?format=zip" : "/api/project?format=single";
    const done = downloaded === kind;
    return (
      <div className="group/card flex flex-col gap-3 rounded-lg border border-line bg-surface-2 p-4 transition-all duration-150 hover:border-accent-border hover:bg-surface-hover">
        <div className="flex items-start gap-3">
          <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md border border-line bg-surface-1 text-accent transition-colors group-hover/card:border-accent-border group-hover/card:bg-accent-soft">
            {isZip ? <Archive size={16} /> : <FileText size={16} />}
          </div>
          <div className="min-w-0">
            <div className="text-[13px] font-semibold text-fg-1">
              {isZip ? t("project.zip.title") : t("project.single.title")}
            </div>
            <p className="mt-1 text-[11.5px] leading-relaxed text-fg-3">
              {isZip
                ? t("project.zip.desc")
                : t("project.single.desc", manifest?.totals.files ?? 0)}
            </p>
          </div>
        </div>
        <button
          type="button"
          data-download={kind}
          onClick={() => {
            triggerDownload(href);
            setDownloaded(kind);
            window.setTimeout(() => setDownloaded((cur) => (cur === kind ? null : cur)), 2600);
          }}
          className="kc-focus-ring mt-auto flex h-8 items-center justify-center gap-2 rounded-md border border-accent-border bg-accent-soft px-3 text-[12px] font-semibold text-accent transition-all duration-150 hover:bg-accent hover:text-on-accent active:scale-[0.99]"
        >
          {done ? <Check size={14} /> : <Download size={14} />}
          {done ? "OK" : isZip ? t("project.zip.cta") : t("project.single.cta")}
        </button>
      </div>
    );
  };

  return (
    <div
      className="fixed inset-0 z-[75] flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-label={t("project.title")}
    >
      <div className="absolute inset-0 bg-scrim backdrop-blur-[2px]" onClick={onClose} aria-hidden />

      <div className="relative flex max-h-[88vh] w-full max-w-[760px] animate-rise flex-col overflow-hidden rounded-xl border border-line bg-surface-1 shadow-overlay">
        {/* header */}
        <div className="flex shrink-0 items-center gap-3 border-b border-line-faint px-5 py-3.5">
          <div className="flex h-8 w-8 items-center justify-center rounded-md border border-line bg-surface-2">
            <Package size={15} className="text-accent" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-[14px] font-semibold text-fg-1">{t("project.title")}</div>
            <div className="text-[11px] text-fg-3">{t("project.subtitle")}</div>
          </div>
          <KcToolButton onClick={onClose} aria-label={t("common.close")} title={t("common.close")}>
            <X size={14} />
          </KcToolButton>
        </div>

        {/* body */}
        <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">
          {!manifest && !error && (
            <div className="flex flex-col items-center gap-3 py-16 text-center">
              <Loader2 size={22} className="animate-spin text-accent" />
              <div className="text-[13px] text-fg-2">{t("project.loading")}</div>
            </div>
          )}

          {error && (
            <div className="flex flex-col items-center gap-3 py-16 text-center">
              <div className="flex h-10 w-10 items-center justify-center rounded-md border border-error-border bg-error-soft text-error">
                <X size={18} />
              </div>
              <div className="text-[13px] text-fg-1">{t("project.error")}</div>
              <button
                type="button"
                onClick={() => {
                  setError(false);
                  fetch("/api/project", { cache: "no-store" })
                    .then((r) => (r.ok ? r.json() : Promise.reject(new Error(String(r.status)))))
                    .then((m: Manifest) => setManifest(m))
                    .catch(() => setError(true));
                }}
                className="kc-focus-ring flex h-8 items-center gap-2 rounded-md border border-line bg-surface-2 px-3 text-[12px] font-medium text-fg-1 hover:bg-surface-hover"
              >
                <RefreshCw size={13} />
                {t("project.retry")}
              </button>
            </div>
          )}

          {manifest && (
            <>
              {/* stats row */}
              <div className="mb-4 flex flex-wrap gap-2">
                {stat(t("project.stat.files"), String(manifest.totals.files))}
                {stat(t("project.stat.size"), kb(manifest.totals.bytes))}
                {stat(t("project.stat.lines"), manifest.totals.lines.toLocaleString("ru-RU"))}
                {stat(t("project.stat.groups"), String(manifest.totals.groups))}
              </div>

              {/* download cards */}
              <div className="mb-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
                {downloadCard("zip")}
                {downloadCard("single")}
              </div>

              {/* run instructions */}
              <div className="mb-4 rounded-lg border border-line-faint bg-sunken px-4 py-3">
                <Overline className="mb-2">{t("project.run.title")}</Overline>
                <div className="flex flex-col gap-1.5 font-mono text-[12px]">
                  <div className="flex items-center gap-2 text-fg-1">
                    <Terminal size={12} className="shrink-0 text-fg-3" aria-hidden />
                    <span className="text-success">bun install</span>
                  </div>
                  <div className="flex items-center gap-2 text-fg-1">
                    <Terminal size={12} className="shrink-0 text-fg-3" aria-hidden />
                    <span className="text-success">bun run dev</span>
                    <span className="text-fg-3">→</span>
                    <span className="text-accent">http://localhost:3000</span>
                  </div>
                </div>
                <div className="mt-2 flex items-center gap-1.5 text-[11px] text-fg-3">
                  <Check size={11} className="shrink-0 text-success" aria-hidden />
                  {t("project.readmeNote")}
                </div>
              </div>

              {/* file tree */}
              <div className="overflow-hidden rounded-lg border border-line-faint">
                <div className="flex items-center gap-2 border-b border-line-faint bg-surface-2 px-3 py-2">
                  <Search size={12} className="shrink-0 text-fg-3" aria-hidden />
                  <input
                    ref={filterRef}
                    value={filter}
                    onChange={(e) => setFilter(e.target.value)}
                    onFocus={(e) => e.target.scrollIntoView({ block: "nearest" })}
                    placeholder={t("project.tree.filter")}
                    className="h-6 w-full bg-transparent text-[12px] text-fg-1 placeholder:text-fg-3 focus:outline-none"
                    aria-label={t("project.tree.title")}
                  />
                  {filter && (
                    <button
                      type="button"
                      onClick={() => setFilter("")}
                      className="kc-focus-ring shrink-0 rounded-sm p-0.5 text-fg-3 hover:text-fg-1"
                      aria-label={t("common.close")}
                    >
                      <X size={11} />
                    </button>
                  )}
                </div>

                <div className="max-h-72 overflow-y-auto">
                  {groups.length === 0 && (
                    <div className="px-3 py-8 text-center text-[12px] text-fg-3">{t("project.tree.empty")}</div>
                  )}
                  {groups.map((g) => (
                    <div key={g.name}>
                      <div className="sticky top-0 z-[1] flex items-center gap-2 border-b border-line-faint bg-surface-1/95 px-3 py-1.5 backdrop-blur-sm">
                        <Folder size={11} className="shrink-0 text-accent" aria-hidden />
                        <span className="font-mono text-[11px] font-semibold text-fg-1">
                          {g.name === "(root)" ? "/" : `${g.name}/`}
                        </span>
                        <span className="ml-auto font-mono text-[10px] tabular-nums text-fg-3">
                          {g.files.length}
                        </span>
                      </div>
                      <ul>
                        {g.files.map((f) => {
                          const Icon = fileIcon(f.path);
                          return (
                            <li
                              key={f.path}
                              className="flex items-center gap-2 px-3 py-1 transition-colors hover:bg-surface-hover"
                              title={f.path}
                            >
                              <Icon size={11} className="shrink-0 text-fg-3" aria-hidden />
                              <span className="min-w-0 flex-1 truncate font-mono text-[11px] text-fg-2">
                                {f.path.slice(g.name === "(root)" ? 0 : g.name.length + 1)}
                              </span>
                              <span className="shrink-0 font-mono text-[10px] tabular-nums text-fg-3">
                                {f.lines}
                              </span>
                              <span className="w-14 shrink-0 text-right font-mono text-[10px] tabular-nums text-fg-3">
                                {kb(f.bytes)}
                              </span>
                            </li>
                          );
                        })}
                      </ul>
                    </div>
                  ))}
                </div>
              </div>
            </>
          )}
        </div>

        {/* footer */}
        {manifest && (
          <div className="flex shrink-0 items-center justify-between gap-3 border-t border-line-faint bg-surface-2/50 px-5 py-2.5">
            <span className="truncate font-mono text-[10.5px] text-fg-3">
              kontur-code.zip · kontur-code-source.md
            </span>
            <span className="hidden items-center gap-1.5 sm:flex">
              <span className="kc-keycap">Ctrl</span>
              <span className="kc-keycap">Shift</span>
              <span className="kc-keycap">D</span>
            </span>
          </div>
        )}
      </div>
    </div>
  );
}
