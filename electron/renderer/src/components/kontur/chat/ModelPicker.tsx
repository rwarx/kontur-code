"use client";

import { useEffect, useMemo, useState } from "react";
import { Check, Eye, RefreshCw, Search, SlidersHorizontal, Wrench } from "lucide-react";
import { useKontur } from "@/lib/kontur/store";
import { useT } from "@/lib/kontur/useT";
import type { ModelInfo } from "@/lib/kontur/types";
import { KcBadge, Overline } from "@/components/kontur/ui";

export function ModelPicker({ onClose }: { onClose?: () => void }) {
  const t = useT();
  const providers = useKontur((s) => s.providers);
  const selectedModelId = useKontur((s) => s.ui.selectedModelId);
  const setUi = useKontur((s) => s.setUi);
  const pushToast = useKontur((s) => s.pushToast);
  const setProviderStudioOpen = useKontur((s) => s.setProviderStudioOpen);
  const [query, setQuery] = useState("");

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    return providers
      .filter((p) => p.models.length > 0)
      .map((p) => ({
        provider: p,
        models: p.models.filter(
          (m: ModelInfo) =>
            !q ||
            m.name.toLowerCase().includes(q) ||
            m.id.toLowerCase().includes(q) ||
            p.name.toLowerCase().includes(q),
        ),
      }))
      .filter((g) => g.models.length > 0);
  }, [providers, query]);

  const total = groups.reduce((acc, g) => acc + g.models.length, 0);

  /* Esc closes the popup */
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onClose?.();
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [onClose]);

  return (
    <div className="w-[420px] max-w-[calc(100vw-24px)] overflow-hidden rounded-lg border border-line bg-surface-3 shadow-overlay">
      <div className="border-b border-line-faint p-2">
        <div className="flex h-8 items-center gap-2 rounded-sm border border-line bg-sunken px-2.5 focus-within:border-line-strong">
          <Search size={12} className="shrink-0 text-fg-3" />
          <input
            autoFocus
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={t("picker.filterPlaceholder")}
            className="w-full bg-transparent text-[12.5px] text-fg-1 outline-none placeholder:text-fg-3"
            aria-label={t("picker.filterPlaceholder")}
          />
          <RefreshCw size={11} className="shrink-0 text-fg-3" aria-hidden />
        </div>
      </div>
      <div className="max-h-[340px] overflow-y-auto p-1.5">
        {total === 0 && (
          <p className="px-2 py-4 text-center text-[12px] text-fg-3">
            {t("picker.empty", query)}
          </p>
        )}
        {groups.map(({ provider, models }) => (
          <div key={provider.id} className="mb-1">
            <Overline className="px-2 py-1.5">
              {provider.name}
              <span className="ml-1.5 normal-case tracking-normal text-fg-3/60">
                {models.length}
              </span>
            </Overline>
            {models.map((m) => {
              const selected = m.id === selectedModelId;
              return (
                <button
                  key={m.id}
                  type="button"
                  onClick={() => {
                    setUi({ selectedModelId: m.id });
                    pushToast(t("providers.toast.updated"), m.name);
                    onClose?.();
                  }}
                  className={`kc-focus-ring flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left transition-colors duration-100 ${
                    selected ? "bg-accent-soft" : "hover:bg-surface-hover"
                  }`}
                >
                  {selected ? (
                    <Check size={13} className="shrink-0 text-accent" />
                  ) : (
                    <span className="h-[13px] w-[13px] shrink-0" aria-hidden />
                  )}
                  <span className="min-w-0 flex-1">
                    <span className={`block truncate text-[12.5px] font-medium ${selected ? "text-accent" : "text-fg-1"}`}>
                      {m.name}
                      {m.kind === "reasoner" && (
                        <span className="ml-1.5 font-mono text-[9.5px] uppercase tracking-wide text-fg-3">
                          reasoner
                        </span>
                      )}
                    </span>
                    <span className="block truncate font-mono text-[10px] text-fg-3">{m.id}</span>
                  </span>
                  <span className="flex shrink-0 items-center gap-1">
                    <KcBadge>{m.contextK >= 1000 ? `${Math.round(m.contextK / 1000)}M` : `${m.contextK}K`}</KcBadge>
                    {m.vision && (
                      <span title={t("picker.vision")} className="text-fg-3">
                        <Eye size={10} />
                      </span>
                    )}
                    {m.tools && (
                      <span title={t("picker.tools")} className="text-fg-3">
                        <Wrench size={10} />
                      </span>
                    )}
                    {m.pricePrompt !== undefined && (
                      <span className="font-mono text-[9.5px] text-fg-3">${m.pricePrompt}/M</span>
                    )}
                  </span>
                </button>
              );
            })}
          </div>
        ))}
      </div>
      <div className="border-t border-line-faint p-1.5">
        <button
          type="button"
          onClick={() => {
            onClose?.();
            setProviderStudioOpen(true, "manage");
          }}
          className="kc-focus-ring flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-[12px] text-fg-2 transition-colors hover:bg-surface-hover hover:text-fg-1"
        >
          <SlidersHorizontal size={13} className="shrink-0 text-fg-3" aria-hidden />
          <span className="flex-1">{t("picker.manageProviders")}</span>
        </button>
      </div>
    </div>
  );
}
