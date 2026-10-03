"use client";

import { FolderOpen } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { isServerMode, openWorkspaceDialog } from "@/lib/kontur/sync";
import { KcPrimaryButton } from "@/components/kontur/ui";

/* Mandatory project gate for Code mode — code mode is workspace-bound, so it
   must not render any work surface until a project folder is open. Chat & Cowork
   stay project-free (item 5 / Turn 2). Rendered by KonturApp in place of the
   surface router whenever workMode === "code" and no workspaceRoot is set (and
   only in server mode, where a real folder can actually be opened). */
export function ProjectGate() {
  const t = useT();
  const pushToast = useKontur((s) => s.pushToast);

  const openProject = () => {
    if (!isServerMode() || !window.kontur) {
      pushToast(t("settings.workspace.toast"));
      return;
    }
    void openWorkspaceDialog()
      .then((root) => { if (root) pushToast(t("palette.openWorkspace"), root); })
      .catch((err) =>
        pushToast(t("palette.openWorkspace"), err instanceof Error ? err.message : undefined, "destructive"),
      );
  };

  return (
    <div className="flex h-full w-full flex-col items-center justify-center gap-4 bg-app px-6 text-center">
      <span className="flex h-14 w-14 animate-rise items-center justify-center rounded-2xl border border-line bg-surface-1 text-accent shadow-subtle">
        <FolderOpen size={26} />
      </span>
      <div className="animate-rise">
        <h2 className="text-[17px] font-semibold tracking-[-0.01em] text-fg-1">{t("code.project.required.title")}</h2>
        <p className="mx-auto mt-1.5 max-w-[380px] text-[12.5px] leading-relaxed text-fg-3">
          {t("code.project.required.desc")}
        </p>
      </div>
      <KcPrimaryButton onClick={openProject} className="mt-1 h-8 animate-rise px-4">
        <FolderOpen size={13} />
        {t("sidebar.openFolder")}
      </KcPrimaryButton>
    </div>
  );
}
