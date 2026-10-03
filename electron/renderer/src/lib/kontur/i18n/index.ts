import type { Locale } from "../types";
import type { Dictionary } from "./types-dict";
import { en } from "./en";
import { ru } from "./ru";
import { de } from "./de";

export type { Dictionary };

const raw: Record<Locale, Dictionary> = { en, ru, de };

/* Merged with English fallback so partial dictionaries never break the UI */
export const dictionaries: Record<Locale, Dictionary> = {
  en,
  ru: { ...en, ...ru },
  de: { ...en, ...de },
};

export function translate(locale: Locale, key: string, ...args: (string | number)[]): string {
  const dict = dictionaries[locale] ?? en;
  let value = dict[key] ?? en[key] ?? key;
  args.forEach((arg, i) => {
    value = value.replaceAll(`{${i}}`, String(arg));
  });
  return value;
}

export const locales: { id: Locale; label: string; native: string }[] = [
  { id: "en", label: "English", native: "EN" },
  { id: "ru", label: "Русский", native: "RU" },
  { id: "de", label: "Deutsch", native: "DE" },
];

export { raw };
