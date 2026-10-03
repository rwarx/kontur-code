"use client";

import { useState } from "react";
import { AlertTriangle, Check, ChevronRight, ShieldAlert, X } from "lucide-react";
import type { Approval } from "@/lib/kontur/types";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { KcDangerButton, KcGhostButton, KcPrimaryButton } from "@/components/kontur/ui";
import { DiffView } from "./DiffView";

export function ApprovalGate({ approval }: { approval: Approval }) {
  const t = useT();
  const resolveApproval = useKontur((s) => s.resolveApproval);
  const [denyOpen, setDenyOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [decided, setDecided] = useState<"allowed" | "allowed-for-run" | "denied" | null>(null);
  const [argsOpen, setArgsOpen] = useState(false);

  const decide = (decision: "allowed" | "allowed-for-run" | "denied") => {
    setDecided(decision);
    resolveApproval(decision, decision === "denied" ? reason : undefined);
  };

  return (
    <div
      className="animate-rise rounded-lg border border-warning/40 bg-surface-2 shadow-subtle"
      role="alertdialog"
      aria-label={t("approval.pending.title")}
    >
      <div className="flex items-center gap-2 border-b border-warning/25 bg-warning-soft/50 px-3.5 py-2">
        <ShieldAlert size={14} className={`shrink-0 text-warning ${decided === null ? "animate-pulse-dot" : ""}`} />
        <span className="text-[12.5px] font-semibold text-fg-1">{t("approval.pending.title")}</span>
        <span className="ml-1 rounded-xs border border-warning/40 bg-warning-soft px-1.5 py-px font-mono text-[10px] uppercase text-warning">
          {approval.tool}
        </span>
        <span className="ml-auto truncate font-mono text-[11px] text-fg-2">{approval.headline}</span>
      </div>

      <div className="px-3.5 py-3">
        <p className="flex items-start gap-1.5 text-[12px] leading-snug text-fg-2">
          <AlertTriangle size={12} className="mt-[2px] shrink-0 text-warning" />
          {approval.consequence}
        </p>

        {approval.argsNote && (
          <div className="mt-2">
            <button
              type="button"
              onClick={() => setArgsOpen((v) => !v)}
              className="flex items-center gap-1 text-[11px] font-medium text-fg-3 transition-colors hover:text-fg-2"
              aria-expanded={argsOpen}
            >
              <ChevronRight size={10} className={`transition-transform ${argsOpen ? "rotate-90" : ""}`} />
              {t("approval.arguments")}
            </button>
            {argsOpen && (
              <p className="mt-1 animate-fade rounded-sm bg-sunken px-2 py-1.5 font-mono text-[10.5px] leading-relaxed text-fg-2">
                {approval.argsNote}
              </p>
            )}
          </div>
        )}

        {approval.diff && approval.diff.length > 0 && (
          <div className="mt-2.5">
            <DiffView lines={approval.diff} />
          </div>
        )}

        {decided === null ? (
          <>
            {denyOpen ? (
              <div className="mt-3">
                <input
                  autoFocus
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") decide("denied");
                    if (e.key === "Escape") setDenyOpen(false);
                  }}
                  placeholder={t("approval.denied.reason")}
                  className="h-8 w-full rounded-sm border border-line bg-sunken px-2.5 text-[12.5px] text-fg-1 outline-none placeholder:text-fg-3 focus:border-line-strong"
                  aria-label={t("approval.denied.reason")}
                />
                <div className="mt-2 flex items-center gap-2">
                  <KcDangerButton onClick={() => decide("denied")}>
                    {t("approval.deny")}
                  </KcDangerButton>
                  <KcGhostButton onClick={() => setDenyOpen(false)}>{t("common.cancel")}</KcGhostButton>
                </div>
              </div>
            ) : (
              <div className="mt-3 flex flex-wrap items-center gap-2">
                <KcPrimaryButton onClick={() => decide("allowed")}>
                  {t("approval.allowOnce")}
                </KcPrimaryButton>
                {approval.allowForRunAvailable && (
                  <KcGhostButton
                    onClick={() => decide("allowed-for-run")}
                    className="border border-line bg-surface-1"
                  >
                    {t("approval.allowRun")}
                  </KcGhostButton>
                )}
                <KcDangerButton className="ml-auto" onClick={() => setDenyOpen(true)}>
                  {t("approval.deny")}
                </KcDangerButton>
              </div>
            )}
          </>
        ) : (
          <p
            className={`mt-3 flex items-center gap-1.5 text-[12px] font-medium ${
              decided === "denied" ? "text-error" : "text-success"
            }`}
          >
            {decided === "denied" ? <X size={12} /> : <Check size={12} />}
            {decided === "denied"
              ? t("approval.denied.toast")
              : decided === "allowed-for-run"
                ? t("approval.allowRun")
                : t("approval.allowOnce")}
          </p>
        )}
      </div>
    </div>
  );
}
