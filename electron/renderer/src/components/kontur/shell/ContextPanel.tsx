"use client";

import { useMemo, useState } from "react";
import {
  ArrowLeftRight,
  Bot,
  Compass,
  Eye,
  FileText,
  FolderOpen,
  Layers,
  Link2,
  Lock,
  PenLine,
  Pin,
  Plus,
  Sparkles,
  StickyNote,
  Trash2,
  X,
} from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { useModel } from "@/lib/kontur/useModel";
import { DRAWABLE_EDGE_KINDS, EDIT_NOTE_EVENT, EDGE_KIND_ICONS, dispatchEditEdgeNote } from "@/components/kontur/canvas/canvas-utils";
import { KcGhostButton, Overline, StatusDot } from "@/components/kontur/ui";
import { cn } from "@/lib/utils";

const KIND_ICON: Record<string, React.ElementType> = {
  file: FileText,
  folder: FolderOpen,
  module: Layers,
  service: Compass,
  interface: ArrowLeftRight,
  data: Bot,
  view: Eye,
  test: Bot,
  plan: Pin,
  task: Pin,
  agent: Bot,
  model: Sparkles,
  external: Compass,
  note: FileText,
};

export function ContextPanel() {
  const t = useT();
  const ui = useKontur((s) => s.ui);
  const canvas = useKontur((s) => s.canvas);
  const files = useKontur((s) => s.files);
  const workspaceRoot = useKontur((s) => s.workspaceRoot);
  const contextFiles = useKontur((s) => s.contextFiles);
  const removeContextFile = useKontur((s) => s.removeContextFile);
  const pinContextFile = useKontur((s) => s.pinContextFile);
  const addContextFile = useKontur((s) => s.addContextFile);
  const compactSession = useKontur((s) => s.compactSession);
  const runner = useKontur((s) => s.runner);
  const session = useKontur((s) => s.sessions.find((x) => x.id === s.currentSessionId));
  const openCodeTab = useKontur((s) => s.openCodeTab);
  const setSurface = useKontur((s) => s.setSurface);
  const pushToast = useKontur((s) => s.pushToast);
  const selectNode = useKontur((s) => s.selectNode);
  const updateEdgeKind = useKontur((s) => s.updateEdgeKind);
  const setCanvas = useKontur((s) => s.setCanvas);
  const pushUndoSnapshot = useKontur((s) => s.pushUndoSnapshot);
  const [adding, setAdding] = useState(false);

  const model = useModel(ui.selectedModelId);
  const limitK = (model?.contextK ?? 128) * 1000;
  const used = session?.contextTokens ?? 0;
  const usagePct = Math.min(100, Math.round((used / limitK) * 100));
  const nearFull = usagePct >= 80;

  const node = useMemo(
    () => canvas.nodes.find((n) => n.id === canvas.primaryId) ?? null,
    [canvas.nodes, canvas.primaryId],
  );
  const selectedEdge = useMemo(
    () => canvas.edges.find((e) => e.id === canvas.selectedEdgeId) ?? null,
    [canvas.edges, canvas.selectedEdgeId],
  );
  const edgeFrom = useMemo(
    () => (selectedEdge ? canvas.nodes.find((n) => n.id === selectedEdge.from) ?? null : null),
    [canvas.nodes, selectedEdge],
  );
  const edgeTo = useMemo(
    () => (selectedEdge ? canvas.nodes.find((n) => n.id === selectedEdge.to) ?? null : null),
    [canvas.nodes, selectedEdge],
  );
  const relations = useMemo(
    () =>
      node
        ? canvas.edges
            .filter((e) => e.from === node.id || e.to === node.id)
            .map((e) => {
              const otherId = e.from === node.id ? e.to : e.from;
              const other = canvas.nodes.find((n) => n.id === otherId);
              return { edge: e, other, dir: e.from === node.id ? "→" : "←" };
            })
            .filter((r) => r.other)
        : [],
    [canvas.edges, canvas.nodes, node],
  );

  const aiActive = runner.status === "running" || runner.status === "awaiting-approval";

  return (
    <aside className="flex h-full w-full flex-col overflow-y-auto bg-surface-1" aria-label={t("context.title")}>
      {/* header */}
      <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-line-faint bg-surface-2 px-3 py-2">
        <Sparkles size={13} className="text-accent" />
        <span className="text-[12.5px] font-semibold text-fg-1">{t("context.title")}</span>
        <span className="ml-auto font-mono text-[10px] text-fg-3">
          {contextFiles.length} {t("files.title").toLowerCase()}
        </span>
      </div>

      <div className="flex flex-col gap-4 p-3">
        {/* AI activity */}
        <section className={aiActive ? "order-first" : "hidden"}>
          <Overline className="mb-1.5">{t("context.ai")}</Overline>
          <div className="rounded-md border border-line-faint bg-surface-2 p-2.5">
            <div className="flex items-center gap-2">
              <StatusDot color={runner.status === "awaiting-approval" ? "warning" : "warning"} pulse size={7} />
              <span className="text-[12.5px] font-medium text-fg-1">
                {runner.status === "awaiting-approval"
                  ? t("main.ai.waitingApproval")
                  : runner.label || t("main.ai.working")}
              </span>
            </div>
            <p className="mt-1 text-[11px] text-fg-3">{model?.name}</p>
          </div>
        </section>

        {/* connection inspector — an edge is selected */}
        {selectedEdge && edgeFrom && edgeTo ? (
          <section>
            <Overline className="mb-1.5">{t("edge.inspector.title")}</Overline>
            <div className="rounded-md border border-line-faint bg-surface-2 p-3">
              <div className="flex items-center gap-1.5">
                <Link2 size={13} className="shrink-0 text-accent" aria-hidden />
                <button
                  type="button"
                  onClick={() => selectNode(edgeFrom.id)}
                  className="kc-focus-ring min-w-0 truncate rounded-xs px-1 py-px text-[12.5px] font-semibold text-fg-1 transition-colors hover:bg-surface-hover"
                  title={edgeFrom.title}
                >
                  {edgeFrom.title}
                </button>
                <span className="shrink-0 text-fg-3">→</span>
                <button
                  type="button"
                  onClick={() => selectNode(edgeTo.id)}
                  className="kc-focus-ring min-w-0 truncate rounded-xs px-1 py-px text-[12.5px] font-semibold text-fg-1 transition-colors hover:bg-surface-hover"
                  title={edgeTo.title}
                >
                  {edgeTo.title}
                </button>
              </div>

              {/* connection label — read-only preview + jump-to-canvas edit */}
              {selectedEdge.note ? (
                <button
                  type="button"
                  onClick={() => {
                    setSurface("canvas");
                    dispatchEditEdgeNote(selectedEdge.id);
                  }}
                  className="kc-focus-ring group/en mt-2.5 flex w-full items-start gap-1.5 rounded-sm border border-warning/30 bg-warning-soft/40 p-2 text-left transition-colors duration-100 hover:border-warning/60"
                  title={t("canvas.edgenote.editorTitle")}
                >
                  <StickyNote size={11} className="mt-0.5 shrink-0 text-warning" aria-hidden />
                  <span className="min-w-0 flex-1 text-[11.5px] leading-snug text-fg-2">
                    {selectedEdge.note}
                  </span>
                  <PenLine
                    size={11}
                    className="mt-0.5 shrink-0 text-fg-3 transition-colors group-hover/en:text-warning"
                    aria-hidden
                  />
                </button>
              ) : (
                <button
                  type="button"
                  onClick={() => {
                    setSurface("canvas");
                    dispatchEditEdgeNote(selectedEdge.id);
                  }}
                  className="kc-focus-ring mt-2.5 flex h-7 w-full items-center justify-center gap-1.5 rounded-sm border border-dashed border-line text-[11.5px] font-medium text-fg-3 transition-colors duration-100 hover:border-warning/50 hover:text-warning"
                >
                  <StickyNote size={11} aria-hidden />
                  {t("canvas.edgemenu.note.add")}
                </button>
              )}

              <div className="mt-3">
                <Overline className="mb-1">{t("edge.inspector.retype")}</Overline>
                {DRAWABLE_EDGE_KINDS.includes(selectedEdge.kind) ? (
                  <div className="grid grid-cols-2 gap-1">
                    {DRAWABLE_EDGE_KINDS.map((kind) => {
                      const Icon = EDGE_KIND_ICONS[kind];
                      const active = selectedEdge.kind === kind;
                      return (
                        <button
                          key={kind}
                          type="button"
                          onClick={() => updateEdgeKind(selectedEdge.id, kind, { undoable: true })}
                          aria-pressed={active}
                          title={t(`edgekind.desc.${kind}`)}
                          className={cn(
                            "kc-focus-ring flex items-center gap-1.5 rounded-xs border px-2 py-1.5 text-left transition-colors duration-100",
                            active
                              ? "border-accent-border bg-accent-soft text-accent"
                              : "border-line-faint bg-surface-1 text-fg-2 hover:border-line-strong hover:text-fg-1",
                          )}
                        >
                          <Icon size={11} className="shrink-0" aria-hidden />
                          <span className="truncate text-[11.5px] font-medium">{t(`edgekind.${kind}`)}</span>
                        </button>
                      );
                    })}
                  </div>
                ) : (
                  <p className="rounded-xs border border-dashed border-line px-2 py-1.5 text-[11px] leading-snug text-fg-3">
                    {t("edge.inspector.structural")}
                  </p>
                )}
              </div>

              <button
                type="button"
                onClick={() => {
                  pushUndoSnapshot();
                  setCanvas({
                    edges: canvas.edges.filter((x) => x.id !== selectedEdge.id),
                    selectedEdgeId: null,
                  });
                  pushToast(t("edge.inspector.deleted"), `${edgeFrom.title} → ${edgeTo.title}`);
                }}
                className="kc-focus-ring mt-3 flex h-7 w-full items-center justify-center gap-1.5 rounded-sm border border-line bg-surface-1 text-[12px] font-medium text-fg-2 transition-colors hover:border-error/50 hover:bg-error-soft hover:text-error"
              >
                <Trash2 size={11} />
                {t("edge.inspector.delete")}
              </button>
            </div>
          </section>
        ) : node ? (
          <section>
            <Overline className="mb-1.5">{t("context.node")}</Overline>
            <div className="rounded-md border border-line-faint bg-surface-2 p-3">
              <div className="flex items-start gap-2.5">
                <span
                  className="mt-0.5 h-8 w-1 shrink-0 rounded-full"
                  style={{ background: `var(--nk-${node.kind})` }}
                  aria-hidden
                />
                <div className="min-w-0">
                  <p className="truncate text-[13px] font-semibold text-fg-1">{node.title}</p>
                  <p className="mt-0.5 text-[11px] text-accent">{t(`kind.${node.kind}`)}</p>
                  {node.meta && <p className="mt-0.5 font-mono text-[10.5px] text-fg-3">{node.meta}</p>}
                </div>
              </div>
              {node.path && (
                <button
                  type="button"
                  onClick={() => {
                    openCodeTab(node.path!);
                    setSurface("code");
                  }}
                  className="mt-2.5 flex h-7 w-full items-center justify-center gap-1.5 rounded-sm border border-line bg-surface-1 text-[12px] font-medium text-fg-2 transition-colors hover:border-line-strong hover:text-fg-1"
                >
                  <FileText size={11} />
                  {t("context.openInCode")}
                </button>
              )}
              {node.note ? (
                <div className="mt-2.5 rounded-sm border border-line-faint bg-sunken p-2">
                  <div className="flex items-center gap-1.5">
                    <StickyNote size={10} className="shrink-0 text-accent" aria-hidden />
                    <span className="kc-overline">{t("canvas.note.label")}</span>
                  </div>
                  <p className="mt-1 line-clamp-4 whitespace-pre-wrap text-[11px] leading-relaxed text-fg-2">
                    {node.note}
                  </p>
                </div>
              ) : null}
              <button
                type="button"
                onClick={() => {
                  setSurface("canvas");
                  requestAnimationFrame(() =>
                    window.dispatchEvent(
                      new CustomEvent<{ id: string }>(EDIT_NOTE_EVENT, {
                        detail: { id: node.id },
                      }),
                    ),
                  );
                }}
                className="kc-focus-ring mt-2 flex h-7 w-full items-center justify-center gap-1.5 rounded-sm border border-line bg-surface-1 text-[12px] font-medium text-fg-2 transition-colors hover:border-accent-border hover:text-accent"
              >
                <PenLine size={11} />
                {node.note ? t("canvas.note.edit") : t("canvas.note.label")}
              </button>
              {relations.length > 0 && (
                <div className="mt-3">
                  <Overline className="mb-1">{t("context.relations")}</Overline>
                  <div className="flex flex-col gap-0.5">
                    {relations.slice(0, 8).map(({ edge, other, dir }) => (
                      <button
                        key={edge.id}
                        type="button"
                        onClick={() => selectNode(other!.id)}
                        className="flex items-center gap-1.5 rounded-xs px-1.5 py-1 text-left transition-colors hover:bg-surface-hover"
                      >
                        <span className="font-mono text-[10px] text-fg-3">{dir}</span>
                        <span className="truncate text-[11.5px] text-fg-2">{other!.title}</span>
                        <span className="ml-auto font-mono text-[9.5px] uppercase text-fg-3/70">
                          {edge.kind}
                        </span>
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </section>
        ) : (
          <section>
            <Overline className="mb-1.5">{t("context.workspace")}</Overline>
            <div className="rounded-md border border-line-faint bg-surface-2 p-3">
              <div className="flex items-center gap-2">
                <FolderOpen size={13} className="shrink-0 text-fg-3" />
                <span className="truncate text-[12.5px] font-medium text-fg-1">
                  {workspaceRoot
                    ? (workspaceRoot.split(/[/\\]/).filter(Boolean).pop() ?? workspaceRoot)
                    : t("main.noWorkspace")}
                </span>
              </div>
              <p className="mt-1.5 font-mono text-[10.5px] text-fg-3">
                {t("status.counts", canvas.nodes.length, canvas.edges.length)}
              </p>
              <p className="mt-1 font-mono text-[10.5px] text-fg-3">
                {t("context.filesModified", files.length, files.filter((f) => f.modified).length)}
              </p>
            </div>
          </section>
        )}

        {/* context budget */}
        <section>
          <Overline className="mb-1.5">{t("context.usage")}</Overline>
          <div className="rounded-md border border-line-faint bg-surface-2 p-3">
            <div className="flex items-baseline justify-between">
              <span className="font-mono text-[12px] text-fg-1">
                {(used / 1000).toFixed(1)}k
                <span className="text-fg-3"> / {Math.round(limitK / 1000)}k</span>
              </span>
              <span
                className={`font-mono text-[11px] ${nearFull ? "text-warning" : "text-fg-3"}`}
              >
                {usagePct}%
              </span>
            </div>
            <div className="mt-2 h-1 overflow-hidden rounded-full bg-sunken">
              <div
                className={`h-full rounded-full transition-all duration-500 ${
                  nearFull
                    ? "animate-pulse-dot bg-gradient-to-r from-warning/70 to-warning"
                    : "bg-gradient-to-r from-accent/70 to-accent"
                }`}
                style={{ width: `${usagePct}%` }}
              />
            </div>
            <div className="mt-2 flex items-center justify-between">
              <span className="text-[10.5px] text-fg-3">
                {t("context.cost")} · $
                {((used / 1_000_000) * (model?.pricePrompt ?? 3)).toFixed(3)}
              </span>
              <KcGhostButton onClick={compactSession} className="h-6 px-2 text-[11.5px]">
                {t("context.compact")}
              </KcGhostButton>
            </div>
            {nearFull && (
              <p className="mt-2 rounded-xs bg-warning-soft px-2 py-1.5 text-[11px] leading-snug text-warning">
                {t("context.warning")}
              </p>
            )}
          </div>
        </section>

        {/* context files */}
        <section>
          <Overline className="mb-1.5 flex items-center justify-between">
            <span>
              {t("context.title")} · {contextFiles.reduce((a, c) => a + c.tokens, 0).toLocaleString("en-US")} tok
            </span>
            <button
              type="button"
              onClick={() => setAdding((v) => !v)}
              className="flex h-5 w-5 items-center justify-center rounded-xs text-fg-3 transition-colors hover:bg-surface-hover hover:text-fg-1"
              aria-label={t("context.addFiles")}
            >
              {adding ? <X size={11} /> : <Plus size={11} />}
            </button>
          </Overline>

          {adding && (
            <div className="mb-2 max-h-36 overflow-y-auto rounded-md border border-line-faint bg-surface-2 p-1">
              {files
                .filter((f) => !contextFiles.some((c) => c.path === f.path))
                .map((f) => (
                  <button
                    key={f.path}
                    type="button"
                    onClick={() => {
                      addContextFile({ path: f.path, tokens: 1200, reason: "relevant" });
                      pushToast(t("context.added"), f.path.split("/").pop());
                    }}
                    className="flex w-full items-center gap-1.5 rounded-xs px-1.5 py-1 text-left transition-colors hover:bg-surface-hover"
                  >
                    <FileText size={10} className="shrink-0 text-fg-3" />
                    <span className="truncate font-mono text-[10.5px] text-fg-2">
                      {f.path.split("/").slice(-2).join("/")}
                    </span>
                  </button>
                ))}
            </div>
          )}

          <div className="flex flex-col gap-2">
            {(["pinned", "relevant"] as const).map((reason) => {
              const group = contextFiles.filter((c) => c.reason === reason);
              if (group.length === 0) return null;
              return (
                <div key={reason}>
                  <p className="mb-1 flex items-center gap-1 text-[10px] font-semibold uppercase tracking-wider text-fg-3/70">
                    {reason === "pinned" ? <Pin size={9} /> : <Sparkles size={9} />}
                    {t(reason === "pinned" ? "context.pinned" : "context.relevant")}
                  </p>
                  <div className="flex flex-col gap-0.5">
                    {group.map((c) => (
                      <div
                        key={c.path}
                        className="group flex items-center gap-1.5 rounded-xs px-1.5 py-1 transition-colors hover:bg-surface-hover"
                      >
                        <FileText size={10} className="shrink-0 text-fg-3" />
                        <button
                          type="button"
                          onClick={() => {
                            openCodeTab(c.path);
                            setSurface("code");
                          }}
                          className="min-w-0 flex-1 truncate text-left font-mono text-[10.5px] text-fg-2 hover:text-fg-1"
                          title={c.path}
                        >
                          {c.path.split("/").slice(-2).join("/")}
                        </button>
                        <span className="font-mono text-[9.5px] text-fg-3/70">
                          {(c.tokens / 1000).toFixed(1)}k
                        </span>
                        <span className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100">
                          <button
                            type="button"
                            onClick={() => pinContextFile(c.path)}
                            className={`flex h-4 w-4 items-center justify-center rounded-[3px] ${c.reason === "pinned" ? "text-accent" : "text-fg-3 hover:text-fg-1"}`}
                            aria-label={c.reason === "pinned" ? t("context.unpin") : t("context.pin")}
                            title={c.reason === "pinned" ? t("context.unpin") : t("context.pin")}
                          >
                            <Lock size={9} />
                          </button>
                          <button
                            type="button"
                            onClick={() => removeContextFile(c.path)}
                            className="flex h-4 w-4 items-center justify-center rounded-[3px] text-fg-3 hover:text-error"
                            aria-label={t("context.remove")}
                            title={t("context.remove")}
                          >
                            <X size={9} />
                          </button>
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              );
            })}
            {contextFiles.length === 0 && (
              <p className="rounded-md border border-dashed border-line px-3 py-4 text-center text-[11.5px] text-fg-3">
                {t("context.addFiles")}
              </p>
            )}
          </div>
        </section>

        {/* ask AI */}
        <section>
          <Overline className="mb-1.5">{t("context.askAi")}</Overline>
          <div className="flex flex-col gap-1.5">
            {[
              { label: t("chat.suggest.explain"), prompt: "Explain the architecture of AuthFlow" },
              { label: t("chat.suggest.tokens"), prompt: "Find all token usages" },
            ].map((item) => (
              <KcGhostButton
                key={item.label}
                className="h-7 justify-start border border-line bg-surface-2 font-medium text-fg-2 hover:bg-surface-hover hover:text-fg-1"
                onClick={() => {
                  setSurface("chat");
                  useKontur.getState().setDraft(item.prompt);
                }}
              >
                <Bot size={12} className="text-accent" />
                <span className="truncate">{item.label}</span>
              </KcGhostButton>
            ))}
          </div>
        </section>
      </div>
    </aside>
  );
}
