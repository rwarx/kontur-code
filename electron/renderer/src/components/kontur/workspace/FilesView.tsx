"use client";

import { useMemo, useState } from "react";
import { Check, ChevronRight, FileArchive, FileCode, FileCog, FileJson, FileText, FileType2, FolderOpen, GitCompare, X } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { DemoFile } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { DEMO_FILES } from "@/lib/kontur/data";
import { computeUnifiedDiff, diffStats } from "@/lib/kontur/diff";
import { KcBadge, KcPrimaryButton, KcToolButton, Overline } from "@/components/kontur/ui";
import { openBundleImport } from "@/components/kontur/shell/BundleImport";
import { DiffView } from "@/components/kontur/chat/DiffView";
import { cn } from "@/lib/utils";
import { buildFileTree, formatSize, type FileTreeNode } from "./helpers";
import { CodePane } from "./CodePane";

/* ============================================================
   FILES — workspace tree + read-only preview of a selection.
   ============================================================ */

const PREVIEW_LINES = 80;

/* language-aware icon per extension — the tree reads at a glance */
const EXT_ICONS: Record<string, { icon: LucideIcon; tone: string }> = {
  ".cs": { icon: FileCode, tone: "text-[#8dc189]" },
  ".csproj": { icon: FileCog, tone: "text-[#c586c0]" },
  ".sln": { icon: FileCog, tone: "text-[#c586c0]" },
  ".json": { icon: FileJson, tone: "text-warning" },
  ".md": { icon: FileText, tone: "text-fg-2" },
};

function fileIconFor(name: string): { icon: LucideIcon; tone: string } {
  const dot = name.lastIndexOf(".");
  const ext = dot > 0 ? name.slice(dot).toLowerCase() : "";
  return EXT_ICONS[ext] ?? { icon: FileType2, tone: "text-fg-2" };
}

function TreeRow({
  node,
  depth,
  selected,
  collapsed,
  onToggleFolder,
  onSelectFile,
}: {
  node: FileTreeNode;
  depth: number;
  selected: string | null;
  collapsed: Set<string>;
  onToggleFolder: (path: string) => void;
  onSelectFile: (path: string) => void;
}) {
  const pad = { paddingLeft: 8 + depth * 14 };

  if (node.dir) {
    const open = !collapsed.has(node.path);
    return (
      <>
        <button
          type="button"
          style={pad}
          onClick={() => onToggleFolder(node.path)}
          aria-expanded={open}
          className="kc-focus-ring flex w-full items-center gap-1.5 rounded-xs px-2 py-1 pr-2 text-left transition-colors duration-100 hover:bg-surface-hover"
        >
          <ChevronRight
            size={12}
            className={cn("shrink-0 text-fg-3 transition-transform duration-100", open && "rotate-90")}
          />
          <span className="truncate text-[12.5px] font-medium text-fg-1">{node.name}</span>
        </button>
        {open &&
          node.children.map((child) => (
            <TreeRow
              key={child.path}
              node={child}
              depth={depth + 1}
              selected={selected}
              collapsed={collapsed}
              onToggleFolder={onToggleFolder}
              onSelectFile={onSelectFile}
            />
          ))}
      </>
    );
  }

  const isSelected = selected === node.path;
  const modified = !!node.file?.modified;
  const { icon: FileIcon, tone } = fileIconFor(node.name);
  return (
    <button
      type="button"
      style={pad}
      onClick={() => onSelectFile(node.path)}
      aria-current={isSelected ? "true" : undefined}
      className={cn(
        "kc-focus-ring group/file flex w-full items-center gap-1.5 rounded-xs px-2 py-1 pr-2 text-left transition-colors duration-100 hover:bg-surface-hover",
        isSelected && "bg-accent-soft hover:bg-accent-soft",
      )}
    >
      <FileIcon
        size={12}
        className={cn("shrink-0 transition-colors", modified ? "text-accent" : tone)}
      />
      <span className={cn("truncate text-[12.5px]", isSelected ? "text-accent" : "text-fg-1")}>
        {node.name}
      </span>
      {modified ? (
        <span className="ml-auto h-1.5 w-1.5 shrink-0 rounded-full bg-accent" aria-label="modified" />
      ) : (
        <span
          className="ml-auto shrink-0 translate-x-1 text-fg-3 opacity-0 transition-all duration-100 group-hover/file:translate-x-0 group-hover/file:opacity-100"
          aria-hidden
        >
          <ChevronRight size={11} />
        </span>
      )}
    </button>
  );
}

export default function FilesView() {
  const t = useT();
  const files = useKontur((s) => s.files);
  const openCodeTab = useKontur((s) => s.openCodeTab);
  const setSurface = useKontur((s) => s.setSurface);

  const [selected, setSelected] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  /* path whose diff is currently shown — auto-disengages when selection moves */
  const [diffPath, setDiffPath] = useState<string | null>(null);

  const tree = useMemo(() => buildFileTree(files), [files]);
  const file = selected ? (files.find((f) => f.path === selected) ?? null) : null;
  const baseline = useMemo(
    () => (file ? (DEMO_FILES.find((f) => f.path === file.path)?.content ?? null) : null),
    [file],
  );
  const fileDiff = useMemo(
    () =>
      file && baseline != null && diffPath === file.path
        ? computeUnifiedDiff(baseline, file.content)
        : [],
    [file, baseline, diffPath],
  );
  const diffOn = file != null && diffPath === file.path && baseline != null;
  const previewStats = useMemo(
    () => (file && baseline != null ? diffStats(computeUnifiedDiff(baseline, file.content)) : { add: 0, del: 0 }),
    [file, baseline],
  );

  const toggleFolder = (path: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });

  const allLines = file ? file.content.replace(/\n$/, "").split("\n") : [];
  const truncated = allLines.length > PREVIEW_LINES;
  const preview = truncated ? allLines.slice(0, PREVIEW_LINES).join("\n") : (file?.content ?? "");

  const fileName = file ? file.path.slice(file.path.lastIndexOf("/") + 1) : "";

  return (
    <div className="flex h-full w-full flex-col md:flex-row">
      {/* ---------------- tree ---------------- */}
      <aside className="max-h-64 shrink-0 overflow-y-auto border-b border-line-faint md:max-h-none md:w-[300px] md:border-b-0">
        <div className="flex items-center justify-between px-3 py-2">
          <Overline>{t("files.title")}</Overline>
          <span className="font-mono text-[10px] text-fg-3">{t("files.count", files.length)}</span>
        </div>
        <div className="p-2">
          {tree.map((node) => (
            <TreeRow
              key={node.path}
              node={node}
              depth={0}
              selected={selected}
              collapsed={collapsed}
              onToggleFolder={toggleFolder}
              onSelectFile={setSelected}
            />
          ))}
        </div>
      </aside>

      {/* ---------------- details ---------------- */}
      <section className="flex min-h-0 min-w-0 flex-1 flex-col md:border-l md:border-line-faint">
        {!file ? (
          <div className="flex flex-1 items-center justify-center p-6">
            <div className="flex flex-col items-center gap-2.5 text-center">
              <span className="flex h-10 w-10 items-center justify-center rounded-md border border-line-faint bg-sunken text-fg-3">
                <FolderOpen size={16} />
              </span>
              <p className="text-[12.5px] text-fg-3">{t("files.selectHint")}</p>
              <p className="font-mono text-[10px] text-fg-3/70">{t("files.count", files.length)}</p>

              {/* bundle import/export discoverability */}
              <div className="mt-3 w-[300px] max-w-full rounded-md border border-dashed border-line bg-sunken p-3 text-left">
                <div className="flex items-center gap-1.5">
                  <FileArchive size={12} className="shrink-0 text-accent" aria-hidden />
                  <span className="kc-overline text-accent">{t("files.hint.title")}</span>
                </div>
                <p className="mt-1.5 text-[11px] leading-relaxed text-fg-3">
                  {t("files.hint.import")}
                </p>
                <div className="mt-2.5 flex items-center gap-2">
                  <KcPrimaryButton onClick={openBundleImport} className="h-7 px-2.5 text-[11.5px]">
                    {t("files.hint.button")}
                  </KcPrimaryButton>
                  <span className="text-[10px] leading-tight text-fg-3/80">{t("files.hint.export")}</span>
                </div>
              </div>
            </div>
          </div>
        ) : (
          <>
            <header className="flex flex-wrap items-center gap-x-3 gap-y-1.5 border-b border-line-faint px-4 py-2.5">
              <FileText size={14} className={file.modified ? "text-accent" : "text-fg-2"} />
              <span className="min-w-0 truncate text-[14px] font-semibold text-fg-1">{fileName}</span>
              <span className="shrink-0 rounded-xs border border-line-faint bg-sunken px-1.5 py-px font-mono text-[9.5px] uppercase tracking-wider text-fg-3">
                {file.language}
              </span>
              {file.modified && <KcBadge tone="warning">{t("files.modified")}</KcBadge>}
              <span className="min-w-0 truncate font-mono text-[11px] text-fg-3">{file.path}</span>
              <span className="shrink-0 font-mono text-[10.5px] text-fg-3">{formatSize(file.content)}</span>
              {baseline != null && (
                <span className="shrink-0">
                  <button
                    type="button"
                    onClick={() => setDiffPath(diffOn ? null : file.path)}
                    aria-pressed={diffOn}
                    title={t("files.diff.toggleHint")}
                    className={cn(
                      "kc-focus-ring flex h-6 items-center gap-1.5 rounded-xs border px-2 font-mono text-[10.5px] transition-colors duration-100",
                      diffOn
                        ? "border-accent-border bg-accent-soft text-accent"
                        : "border-line text-fg-2 hover:border-line-strong hover:text-fg-1",
                    )}
                  >
                    <GitCompare size={11} />
                    {t("files.diff.toggle")}
                    <span className="flex items-center gap-1">
                      <span className="text-success">+{previewStats.add}</span>
                      <span className="text-error">−{previewStats.del}</span>
                    </span>
                  </button>
                </span>
              )}
              <span className="ml-auto shrink-0">
                <KcPrimaryButton
                  onClick={() => {
                    openCodeTab(file.path);
                    setSurface("code");
                  }}
                >
                  <FileCode size={13} />
                  {t("files.openInCode")}
                </KcPrimaryButton>
              </span>
            </header>

            <div className="flex min-h-0 flex-1 flex-col">
              {diffOn ? (
                <div className="flex min-h-0 flex-1 flex-col overflow-auto p-4">
                  <div className="mx-auto flex w-full max-w-[860px] flex-col gap-3">
                    <div className="flex items-center gap-2">
                      <Overline>{t("files.diff.title")}</Overline>
                      <span className="font-mono text-[10.5px] text-fg-3">
                        {t("files.diff.direction")}
                      </span>
                      <KcToolButton
                        onClick={() => setDiffPath(null)}
                        aria-label={t("common.close")}
                        title={t("common.close")}
                        className="ml-auto"
                      >
                        <X size={13} />
                      </KcToolButton>
                    </div>
                    {fileDiff.length === 0 ? (
                      <div className="flex min-h-[200px] flex-col items-center justify-center gap-2.5 rounded-md border border-line-faint bg-surface-1 p-6 text-center">
                        <span className="flex h-9 w-9 items-center justify-center rounded-md bg-success-soft text-success">
                          <Check size={16} />
                        </span>
                        <p className="text-[13px] font-medium text-fg-1">{t("files.diff.noChanges")}</p>
                        <p className="max-w-[380px] text-[11.5px] text-fg-3">{t("files.diff.noChangesHint")}</p>
                      </div>
                    ) : (
                      <DiffView lines={fileDiff} collapsedDefault={false} maxHeight={1_000_000} />
                    )}
                  </div>
                </div>
              ) : (
                <>
                  <div className="min-h-0 flex-1">
                    <CodePane content={preview} />
                  </div>
                  {truncated && (
                    <div className="shrink-0 border-t border-line-faint px-4 py-1.5 text-center font-mono text-[10.5px] text-fg-3">
                      {t("code.linesOf", PREVIEW_LINES, allLines.length)}
                    </div>
                  )}
                </>
              )}
            </div>
          </>
        )}
      </section>
    </div>
  );
}
