"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import {
  Activity,
  Frame,
  ListTodo,
  FileCode2,
  Files,
  GitBranch,
  GitFork,
  MoreHorizontal,
  MessageSquare,
  Moon,
  Pencil,
  Pin,
  PinOff,
  Search,
  Settings,
  Store,
  Sun,
  Trash2,
  Waypoints,
  Workflow,
  X,
} from "lucide-react";
import { sessionModeOf, useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import type { Session, Surface } from "@/lib/kontur/types";
import { MODE_SURFACES } from "@/lib/kontur/modes";
import { KcGhostButton, KcToolButton, Overline } from "@/components/kontur/ui";

function relativeTime(ts: number, t: (k: string, ...a: (string | number)[]) => string) {
  const diff = Date.now() - ts;
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return t("time.justNow");
  if (mins < 60) return t("time.minutesAgo", mins);
  const hours = Math.floor(mins / 60);
  if (hours < 24) return t("time.hoursAgo", hours);
  return t("time.yesterday");
}

const NAV: { id: Surface; icon: React.ElementType; key: string }[] = [
  { id: "chat", icon: MessageSquare, key: "nav.chat" },
  { id: "canvas", icon: Frame, key: "nav.canvas" },
  { id: "graph", icon: Waypoints, key: "nav.graph" },
  { id: "files", icon: Files, key: "nav.files" },
  { id: "code", icon: FileCode2, key: "nav.code" },
  { id: "git", icon: GitBranch, key: "nav.git" },
  { id: "trajectory", icon: Activity, key: "nav.trajectory" },
  { id: "tasks", icon: ListTodo, key: "nav.tasks" },
];

function NavItem({
  surface,
  icon: Icon,
  label,
  collapsed,
  badge,
  onClick,
}: {
  surface: Surface;
  icon: React.ElementType;
  label: string;
  collapsed: boolean;
  badge?: number;
  onClick: () => void;
}) {
  const active = useKontur((s) => s.surface) === surface;
  return (
    <button
      type="button"
      onClick={onClick}
      title={collapsed ? label : undefined}
      aria-current={active ? "page" : undefined}
      className={`kc-focus-ring group relative flex h-[30px] w-full items-center gap-2.5 rounded-sm px-2 text-left transition-colors duration-100 ${
        active ? "bg-accent-soft text-accent" : "text-fg-2 hover:bg-surface-hover hover:text-fg-1"
      } ${collapsed ? "justify-center px-0" : ""}`}
    >
      <span
        className={`absolute left-0 top-1/2 h-[14px] w-[2px] -translate-y-1/2 rounded-full bg-accent transition-all duration-150 ${
          active ? "scale-y-100 opacity-100" : "scale-y-0 opacity-0"
        }`}
        aria-hidden
      />
      <Icon size={14} className={active ? "text-accent" : "text-fg-3 group-hover:text-fg-2"} />
      {!collapsed && <span className="truncate text-[12.5px] font-medium">{label}</span>}
      {!collapsed && badge !== undefined && badge > 0 && (
        <span className="ml-auto rounded-full bg-accent-soft px-1.5 py-px font-mono text-[10px] text-accent">
          {badge}
        </span>
      )}
    </button>
  );
}

function SessionRow({
  session,
  active,
  onOpen,
}: {
  session: Session;
  active: boolean;
  onOpen: () => void;
}) {
  const t = useT();
  const renameSession = useKontur((s) => s.renameSession);
  const setSessionPinned = useKontur((s) => s.setSessionPinned);
  const deleteSession = useKontur((s) => s.deleteSession);
  const forkSession = useKontur((s) => s.forkSession);
  const [menuOpen, setMenuOpen] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(session.title);
  const rowRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (rowRef.current && !rowRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    window.addEventListener("mousedown", onDown);
    return () => window.removeEventListener("mousedown", onDown);
  }, [menuOpen]);

  if (editing) {
    return (
      <div className="px-1 py-0.5">
        <input
          autoFocus
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              renameSession(session.id, draft.trim() || session.title);
              setEditing(false);
            }
            if (e.key === "Escape") setEditing(false);
          }}
          onBlur={() => {
            renameSession(session.id, draft.trim() || session.title);
            setEditing(false);
          }}
          className="w-full rounded-xs border border-accent-border bg-sunken px-2 py-1 text-[12px] text-fg-1 outline-none"
          aria-label="Chat name"
          maxLength={80}
        />
      </div>
    );
  }

  return (
    <div ref={rowRef} className="group relative">
      {/* active indicator — same visual language as the NavItem bar */}
      <span
        className={`absolute left-0 top-1/2 h-[16px] w-[2px] -translate-y-1/2 rounded-full bg-accent transition-all duration-150 ${
          active ? "scale-y-100 opacity-100" : "scale-y-0 opacity-0"
        }`}
        aria-hidden
      />
      <button
        type="button"
        onClick={onOpen}
        className={`kc-focus-ring flex h-8 w-full items-center gap-1.5 rounded-sm px-2 text-left transition-colors duration-100 ${
          active ? "bg-accent-soft" : "hover:bg-surface-hover"
        }`}
      >
        {session.pinned && <Pin size={10} className="shrink-0 text-warning" />}
        <span
          title={session.preview ? `${session.title} · ${session.preview}` : session.title}
          className={`min-w-0 flex-1 truncate text-[12px] font-medium ${active ? "text-accent" : "text-fg-1"}`}
        >
          {session.title}
        </span>
        <span className="shrink-0 whitespace-nowrap text-[10px] tabular-nums text-fg-3 transition-opacity duration-100 group-hover:opacity-0">
          {relativeTime(session.updatedAt, t)}
        </span>
        <span
          role="button"
          tabIndex={-1}
          onClick={(e) => {
            e.stopPropagation();
            setMenuOpen((v) => !v);
          }}
          className={`absolute right-1.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-xs text-fg-3 transition-opacity ${
            menuOpen ? "opacity-100" : "opacity-0 group-hover:opacity-100"
          } hover:bg-surface-3 hover:text-fg-1`}
          aria-label="Session actions"
        >
          <MoreHorizontal size={12} />
        </span>
      </button>

      {menuOpen && (
        <div className="absolute right-0 top-0 z-30 w-40 animate-settle rounded-md border border-line bg-surface-3 p-1 shadow-overlay">
          {[
            {
              icon: Pencil,
              label: t("sidebar.rename"),
              action: () => {
                setDraft(session.title);
                setEditing(true);
              },
            },
            {
              icon: session.pinned ? PinOff : Pin,
              label: session.pinned ? t("sidebar.unpin") : t("sidebar.pin"),
              action: () => setSessionPinned(session.id, !session.pinned),
            },
            {
              icon: GitFork,
              label: t("sidebar.fork"),
              action: () => forkSession(session.id),
            },
            {
              icon: Trash2,
              label: t("common.delete"),
              action: () => deleteSession(session.id),
              danger: true,
            },
          ].map(({ icon: Icon, label, action, danger }) => (
            <button
              key={label}
              type="button"
              onClick={() => {
                setMenuOpen(false);
                action();
              }}
              className={`flex h-7 w-full items-center gap-2 rounded-xs px-2 text-[12px] transition-colors hover:bg-surface-hover ${
                danger ? "text-error" : "text-fg-2 hover:text-fg-1"
              }`}
            >
              <Icon size={12} />
              {label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function Sidebar() {
  const t = useT();
  const ui = useKontur((s) => s.ui);
  const setSurface = useKontur((s) => s.setSurface);
  const allSessions = useKontur((s) => s.sessions);
  /* Chats are partitioned by work mode: the list shows only the chats that belong
     to the mode you're in, and switching Chat/Cowork/Code swaps the whole list. */
  const sessions = useMemo(
    () => allSessions.filter((s) => sessionModeOf(s) === ui.workMode),
    [allSessions, ui.workMode],
  );
  const currentSessionId = useKontur((s) => s.currentSessionId);
  const currentSession = useKontur((s) => s.sessions.find((x) => x.id === s.currentSessionId));
  const selectSession = useKontur((s) => s.selectSession);
  const setUi = useKontur((s) => s.setUi);
  const togglePalette = useKontur((s) => s.togglePalette);
  const sidebarOpenMobile = useKontur((s) => s.sidebarOpenMobile);
  const setSidebarOpenMobile = useKontur((s) => s.setSidebarOpenMobile);
  const eventsCount = currentSession?.events.length ?? 0;

  const [query, setQuery] = useState("");
  const [viewMode, setViewMode] = useState<"grouped" | "recent">("grouped");
  const [resizing, setResizing] = useState(false);
  const resizeRef = useRef<HTMLDivElement>(null);

  const filtered = useMemo(() => {
    if (!query.trim()) return sessions;
    const q = query.toLowerCase();
    return sessions.filter(
      (s) =>
        s.title.toLowerCase().includes(q) ||
        s.messages.some((m) => m.content.toLowerCase().includes(q)),
    );
  }, [sessions, query]);

  /* date-bucketed grouping: pinned → today → yesterday → previous 7 days → earlier */
  const grouped = useMemo(() => {
    const now = new Date();
    const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    const startYesterday = startToday - 86_400_000;
    const startWeek = startToday - 6 * 86_400_000;
    const label = (key: string) =>
      key === "pinned"
        ? t("sidebar.group.pinned")
        : key === "today"
          ? t("sidebar.group.today")
          : key === "yesterday"
            ? t("sidebar.group.yesterday")
            : key === "week"
              ? t("sidebar.group.week")
              : t("sidebar.group.earlier");
    /* aggregate into fixed buckets so a pinned older session can't split a
       date group in two (which produced duplicate React keys). */
    const order = ["pinned", "today", "yesterday", "week", "earlier"] as const;
    const buckets: Record<string, typeof filtered> = {};
    for (const s of filtered) {
      const key = s.pinned
        ? "pinned"
        : s.updatedAt >= startToday
          ? "today"
          : s.updatedAt >= startYesterday
            ? "yesterday"
            : s.updatedAt >= startWeek
              ? "week"
              : "earlier";
      (buckets[key] ??= []).push(s);
    }
    return order
      .filter((key) => buckets[key]?.length)
      .map((key) => ({ key, label: label(key), items: buckets[key] }));
  }, [filtered, t]);

  /* drag resize */
  useEffect(() => {
    if (!resizing) return;
    const onMove = (e: MouseEvent) => {
      useKontur.getState().setSidebarWidth(e.clientX);
    };
    const onUp = () => setResizing(false);
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [resizing]);

  const collapsed = ui.sidebarCollapsed;

  const body = (
    <div className="flex h-full flex-col bg-surface-1">
      {/* zcode rail — search / automations / marketplace
         (new-chat moved to the global header switch) */}
      <nav className={`flex flex-col gap-px ${collapsed ? "px-1.5" : "px-2"} pb-1.5 pt-2`} aria-label={t("sidebar.rail.search")}>
        {collapsed ? (
          <KcToolButton onClick={togglePalette} aria-label={t("sidebar.rail.search")} size={30}>
            <Search size={14} />
          </KcToolButton>
        ) : (
          <button
            type="button"
            onClick={togglePalette}
            className="kc-focus-ring group flex h-[30px] w-full items-center gap-2.5 rounded-sm px-2 text-left text-fg-2 transition-colors duration-100 hover:bg-surface-hover hover:text-fg-1"
          >
            <Search size={14} className="text-fg-3 group-hover:text-fg-2" />
            <span className="truncate text-[12.5px] font-medium">{t("sidebar.rail.search")}</span>
            <kbd className="ml-auto rounded-xs border border-line bg-sunken px-1 py-px font-mono text-[9.5px] text-fg-3">⌘K</kbd>
          </button>
        )}
        <NavItem surface="workflows" icon={Workflow} label={t("sidebar.rail.automations")} collapsed={collapsed} onClick={() => setSurface("workflows")} />
        <NavItem surface="models" icon={Store} label={t("sidebar.rail.marketplace")} collapsed={collapsed} onClick={() => setSurface("models")} />
      </nav>

      <div className={`mx-3 h-px bg-line-faint ${collapsed ? "mx-2" : ""}`} />

      {/* navigation — scoped to the active work mode (chat / cowork / code) */}
      <nav className={`flex flex-col gap-px ${collapsed ? "px-1.5" : "px-2"} pb-2 pt-1.5`} aria-label={t("sidebar.section.workspace")}>
        {!collapsed && <Overline className="px-2 pb-1 pt-1">{t("sidebar.section.workspace")}</Overline>}
        {NAV.filter((item) => MODE_SURFACES[ui.workMode].includes(item.id)).map((item) => (
          <NavItem
            key={item.id}
            surface={item.id}
            icon={item.icon}
            label={t(item.key)}
            collapsed={collapsed}
            badge={item.id === "trajectory" ? eventsCount : undefined}
            onClick={() => setSurface(item.id)}
          />
        ))}
      </nav>

      <div className={`mx-3 h-px bg-line-faint ${collapsed ? "mx-2" : ""}`} />

      {/* sessions */}
      {!collapsed && (
        <>
          <div className="flex items-center justify-between gap-2 px-4 pb-1 pt-2.5">
            <div className="flex items-center gap-1.5">
              <Overline>{t("sidebar.section.sessions")}</Overline>
              <span className="font-mono text-[10px] text-fg-3">{sessions.length}</span>
            </div>
            <div className="flex items-center gap-0.5 rounded-sm border border-line-faint bg-sunken p-0.5">
              {(["grouped", "recent"] as const).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  onClick={() => setViewMode(mode)}
                  aria-pressed={viewMode === mode}
                  className={`rounded-xs px-1.5 py-px text-[10px] font-medium transition-colors ${
                    viewMode === mode ? "bg-surface-3 text-fg-1" : "text-fg-3 hover:text-fg-2"
                  }`}
                >
                  {t(`sidebar.view.${mode}`)}
                </button>
              ))}
            </div>
          </div>
          <div className="px-2 pb-1">
            <div className="flex h-7 items-center gap-1.5 rounded-sm border border-line bg-sunken px-2 focus-within:border-line-strong">
              <Search size={11} className="shrink-0 text-fg-3" />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={t("sidebar.searchPlaceholder")}
                className="w-full bg-transparent text-[12px] text-fg-1 outline-none placeholder:text-fg-3"
                aria-label={t("sidebar.searchPlaceholder")}
              />
              {query && (
                <button type="button" onClick={() => setQuery("")} aria-label="Clear" className="text-fg-3 hover:text-fg-1">
                  <X size={11} />
                </button>
              )}
            </div>
          </div>
        </>
      )}
      <div className={`min-h-0 flex-1 overflow-y-auto ${collapsed ? "px-1.5" : "px-2 pb-2"} pt-1`}>
        {collapsed ? (
          <div className="flex flex-col items-center gap-1">
            {sessions.slice(0, 5).map((s) => (
              <button
                key={s.id}
                type="button"
                title={s.title}
                onClick={() => selectSession(s.id)}
                className={`flex h-7 w-7 items-center justify-center rounded-sm text-[11px] font-medium transition-colors ${
                  s.id === currentSessionId ? "bg-accent-soft text-accent" : "text-fg-3 hover:bg-surface-hover hover:text-fg-1"
                }`}
              >
                {s.title.slice(0, 1).toUpperCase()}
              </button>
            ))}
          </div>
        ) : filtered.length === 0 ? (
          <p className="px-2 py-3 text-[11.5px] text-fg-3">
            {query ? t("sidebar.noMatches", query) : t("sidebar.noChats")}
          </p>
        ) : viewMode === "recent" ? (
          <div className="flex flex-col gap-0.5">
            {filtered.map((s) => (
              <SessionRow
                key={s.id}
                session={s}
                active={s.id === currentSessionId}
                onOpen={() => selectSession(s.id)}
              />
            ))}
          </div>
        ) : (
          <div className="flex flex-col gap-0.5">
            {grouped.map((g) => (
              <div key={g.key} className="flex flex-col gap-0.5">
                <Overline className="px-2 pb-1 pt-2.5 first:pt-1.5">{g.label}</Overline>
                {g.items.map((s) => (
                  <SessionRow
                    key={s.id}
                    session={s}
                    active={s.id === currentSessionId}
                    onOpen={() => selectSession(s.id)}
                  />
                ))}
              </div>
            ))}
          </div>
        )}
      </div>

      {/* footer */}
      <div className={`flex items-center gap-1 border-t border-line-faint p-2 ${collapsed ? "flex-col" : ""}`}>
        <KcToolButton onClick={() => setSurface("settings")} active={useKontur.getState().surface === "settings"} aria-label={t("sidebar.settings")}>
          <Settings size={14} />
        </KcToolButton>
        <KcToolButton
          onClick={() => setUi({ theme: ui.theme === "dark" ? "light" : "dark" })}
          aria-label={t(`theme.${ui.theme === "dark" ? "light" : "dark"}`)}
          title={t(`theme.${ui.theme === "dark" ? "light" : "dark"}`)}
        >
          {ui.theme === "dark" ? <Sun size={14} /> : <Moon size={14} />}
        </KcToolButton>
        {!collapsed && (
          <span className="ml-auto pr-1 font-mono text-[10px] text-fg-3/70">v0.9</span>
        )}
      </div>
    </div>
  );

  return (
    <>
      {/* desktop sidebar */}
      <aside
        className="relative hidden shrink-0 border-r border-line-faint lg:block"
        style={{ width: collapsed ? 48 : ui.sidebarWidth }}
        aria-label="Sidebar"
      >
        {body}
        {/* resize handle */}
        {!collapsed && (
          <div
            ref={resizeRef}
            role="separator"
            aria-orientation="vertical"
            onMouseDown={(e) => {
              e.preventDefault();
              setResizing(true);
            }}
            className="absolute inset-y-0 right-0 z-10 w-1 cursor-col-resize transition-colors hover:bg-accent/40"
          />
        )}
      </aside>

      {/* mobile drawer */}
      {sidebarOpenMobile && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div
            className="absolute inset-0 bg-scrim"
            onClick={() => setSidebarOpenMobile(false)}
            aria-hidden
          />
          <div className="absolute inset-y-0 left-0 w-[260px] animate-rise border-r border-line-faint shadow-overlay">
            {body}
          </div>
        </div>
      )}
    </>
  );
}
