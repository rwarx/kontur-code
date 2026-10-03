"use client";

import { useEffect, useRef, useState } from "react";
import { FileArchive, FileDown } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import { BundleError, importBundleFile } from "@/lib/kontur/import";

/* module-level trigger so the palette / header can open the picker */
let pickFile: (() => void) | null = null;
export function openBundleImport() {
  pickFile?.();
}

export function BundleImport() {
  const t = useT();
  const pushToast = useKontur((s) => s.pushToast);
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState<null | "reading" | "restoring">(null);
  const dragDepth = useRef(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const handler = () => inputRef.current?.click();
    pickFile = handler;
    return () => {
      if (pickFile === handler) pickFile = null;
    };
  }, []);

  const runImport = async (file: File) => {
    if (busy) return;
    setBusy("reading");
    try {
      const result = await importBundleFile(file);
      setBusy(null);
      pushToast(
        t("import.successTitle"),
        t("import.successDesc")
          .replace("{messages}", String(result.counts.messages))
          .replace("{nodes}", String(result.counts.nodes))
          .replace("{files}", String(result.counts.files))
          .replace("{goals}", String(result.counts.goals)) +
          (result.counts.skippedFiles
            ? " " + t("import.skipped").replace("{n}", String(result.counts.skippedFiles))
            : ""),
      );
    } catch (err) {
      setBusy(null);
      if (err instanceof BundleError) {
        if (err.code === "busy") pushToast(t("import.errBusyTitle"), t("import.errBusy"), "destructive");
        else if (err.code === "not-zip") pushToast(t("import.errTitle"), t("import.errNotZip"), "destructive");
        else if (err.code === "not-bundle") pushToast(t("import.errTitle"), t("import.errNotBundle"), "destructive");
        else pushToast(t("import.errTitle"), t("import.errCorrupt"), "destructive");
      } else {
        pushToast(t("import.errTitle"), t("import.errCorrupt"), "destructive");
      }
    }
  };

  /* global drag & drop — drop a .zip anywhere on the app */
  useEffect(() => {
    const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes("Files");
    const onEnter = (e: DragEvent) => {
      if (!hasFiles(e) || busy) return;
      e.preventDefault();
      dragDepth.current++;
      setDragging(true);
    };
    const onOver = (e: DragEvent) => {
      if (hasFiles(e)) e.preventDefault();
    };
    const onLeave = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      dragDepth.current = Math.max(0, dragDepth.current - 1);
      if (dragDepth.current === 0) setDragging(false);
    };
    const onDrop = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      dragDepth.current = 0;
      setDragging(false);
      const file = e.dataTransfer?.files?.[0];
      if (file) void runImport(file);
    };
    window.addEventListener("dragenter", onEnter);
    window.addEventListener("dragover", onOver);
    window.addEventListener("dragleave", onLeave);
    window.addEventListener("drop", onDrop);
    return () => {
      window.removeEventListener("dragenter", onEnter);
      window.removeEventListener("dragover", onOver);
      window.removeEventListener("dragleave", onLeave);
      window.removeEventListener("drop", onDrop);
    };
  }, [busy, t]);

  return (
    <>
      <input
        ref={inputRef}
        type="file"
        accept=".zip,application/zip"
        className="hidden"
        aria-hidden
        tabIndex={-1}
        onChange={(e) => {
          const file = e.target.files?.[0];
          e.target.value = "";
          if (file) void runImport(file);
        }}
      />

      {(dragging || busy) && (
        <div
          className="fixed inset-0 z-[80] flex items-center justify-center bg-scrim backdrop-blur-[2px]"
          data-canvas-overlay
          aria-live="polite"
        >
          <div className="mx-4 flex max-w-sm animate-rise flex-col items-center gap-3 rounded-xl border-2 border-dashed border-accent bg-surface-1 px-8 py-10 text-center shadow-overlay">
            <div className="flex h-12 w-12 items-center justify-center rounded-lg border border-accent-border bg-accent-soft">
              {busy ? (
                <span className="h-5 w-5 animate-spin-slow rounded-full border-2 border-accent-border border-t-accent" />
              ) : (
                <FileArchive size={22} className="text-accent" />
              )}
            </div>
            <div className="text-[15px] font-semibold text-fg-1">
              {busy === "reading" && t("import.reading")}
              {busy === "restoring" && t("import.restore")}
              {!busy && t("import.dropTitle")}
            </div>
            <p className="text-[12px] leading-relaxed text-fg-3">
              {busy ? t("import.busyHint") : t("import.dropHint")}
            </p>
            <div className="flex items-center gap-1.5 text-[10px] text-fg-3">
              <FileDown size={10} />
              <span className="font-mono">{t("import.formatTag")}</span>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
