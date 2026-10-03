"use client";

import { useMemo } from "react";
import { useKontur } from "./store";
import { translate } from "./i18n";
import type { Locale } from "./types";

export function useT(): ((key: string, ...args: (string | number)[]) => string) & {
  locale: Locale;
} {
  const locale = useKontur((s) => s.ui.locale);
  /* task 2-c: Object.assign instead of mutating the fn (react-hooks/immutability);
     identical API — callable t(key, …) with t.locale — but stable identity. */
  return useMemo(
    () =>
      Object.assign(
        (key: string, ...args: (string | number)[]) => translate(locale, key, ...args),
        { locale },
      ),
    [locale],
  );
}
