"use client";

import type { ReactNode, ButtonHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

/* ============================================================
   KONTUR CODE — shared UI primitives.
   Every surface builds on these so the visual language stays
   identical across Chat / Canvas / Settings / Trajectory.
   ============================================================ */

export function Overline({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("kc-overline", className)}>{children}</div>;
}

export function KcToolButton({
  children,
  active,
  size = 26,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { children: ReactNode; active?: boolean; size?: number }) {
  return (
    <button
      type="button"
      style={{ width: size, height: size }}
      className={cn(
        "kc-focus-ring flex shrink-0 items-center justify-center rounded-sm text-fg-2 transition-colors duration-100",
        "hover:bg-surface-hover hover:text-fg-1 active:bg-sunken",
        "disabled:pointer-events-none disabled:opacity-40",
        active && "bg-accent-soft text-accent hover:bg-accent-soft hover:text-accent",
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

export function KcGhostButton({
  children,
  active,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { children: ReactNode; active?: boolean }) {
  return (
    <button
      type="button"
      className={cn(
        "kc-focus-ring inline-flex h-7 items-center gap-1.5 rounded-sm px-2.5 text-[12.5px] font-medium text-fg-2 transition-colors duration-100",
        "hover:bg-surface-hover hover:text-fg-1 active:bg-sunken",
        "disabled:pointer-events-none disabled:opacity-40",
        active && "bg-accent-soft text-accent hover:bg-accent-soft hover:text-accent",
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

export function KcPrimaryButton({
  children,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { children: ReactNode }) {
  return (
    <button
      type="button"
      className={cn(
        "kc-focus-ring inline-flex h-7 items-center gap-1.5 rounded-sm bg-accent px-3 text-[12.5px] font-semibold text-on-accent transition-all duration-100",
        "hover:bg-accent-hover active:translate-y-px",
        "disabled:pointer-events-none disabled:opacity-40",
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

export function KcDangerButton({
  children,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { children: ReactNode }) {
  return (
    <button
      type="button"
      className={cn(
        "kc-focus-ring inline-flex h-7 items-center gap-1.5 rounded-sm px-2.5 text-[12.5px] font-medium text-error transition-colors duration-100",
        "hover:bg-error-soft active:bg-sunken",
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

export function StatusDot({
  color,
  pulse,
  size = 7,
  className,
}: {
  color: "success" | "warning" | "error" | "accent" | "muted";
  pulse?: boolean;
  size?: number;
  className?: string;
}) {
  const colorVar =
    color === "success" ? "var(--kc-success)"
    : color === "warning" ? "var(--kc-warning)"
    : color === "error" ? "var(--kc-error)"
    : color === "accent" ? "var(--kc-accent)"
    : "var(--kc-fg-3)";
  return (
    <span
      className={cn("inline-block shrink-0 rounded-full", pulse && "animate-pulse-dot", className)}
      style={{ width: size, height: size, background: colorVar }}
      aria-hidden
    />
  );
}

export function KcCard({
  children,
  className,
  as: Tag = "div",
}: {
  children: ReactNode;
  className?: string;
  as?: "div" | "section" | "article";
}) {
  return (
    <Tag className={cn("rounded-md border border-line-faint bg-surface-2", className)}>{children}</Tag>
  );
}

export function Keycap({ children }: { children: ReactNode }) {
  return <span className="kc-keycap">{children}</span>;
}

export function KcBadge({
  children,
  tone = "neutral",
  className,
}: {
  children: ReactNode;
  tone?: "neutral" | "accent" | "success" | "warning" | "error";
  className?: string;
}) {
  const tones: Record<string, string> = {
    neutral: "border-line text-fg-2",
    accent: "border-accent-border text-accent bg-accent-soft",
    success: "border-success/40 text-success bg-success-soft",
    warning: "border-warning/40 text-warning bg-warning-soft",
    error: "border-error/40 text-error bg-error-soft",
  };
  return (
    <span
      className={cn(
        "inline-flex h-[18px] items-center rounded-xs border px-1.5 font-mono text-[10px] leading-none",
        tones[tone],
        className,
      )}
    >
      {children}
    </span>
  );
}

export function SectionHeader({
  overline,
  title,
  right,
  className,
}: {
  overline?: string;
  title: string;
  right?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex items-end justify-between gap-3", className)}>
      <div>
        {overline && <Overline className="mb-1">{overline}</Overline>}
        <h2 className="text-[15px] font-semibold text-fg-1">{title}</h2>
      </div>
      {right}
    </div>
  );
}

/* Mono icon+name row used for tools, files, events */
export function MonoLabel({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span className={cn("font-mono text-[11.5px] leading-none", className)}>{children}</span>
  );
}

export function Divider({ className }: { className?: string }) {
  return <div className={cn("h-px w-full bg-line-faint", className)} />;
}
