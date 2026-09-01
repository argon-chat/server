// @argon/i18n - Internationalization package with merge support
// This package contains ONLY public/shared localization keys
// App-specific keys should be defined in each app's locales folder


// Import core (public) locales
import enCore from "./core/en.json";
import ruCore from "./core/ru.json";
import jpCore from "./core/jp.json";
import amCore from "./core/am.json";
import ruPtCore from "./core/ru_pt.json";

export const coreMessages = {
  en: enCore,
  ru: ruCore,
  jp: jpCore,
  am: amCore,
  ru_pt: ruPtCore,
} as const;

export type SupportedLocale = keyof typeof coreMessages;
export type CoreLocaleSchema = typeof enCore;

// Re-export vue-i18n utilities
export { useI18n } from "vue-i18n";
export type { I18n, I18nOptions } from "vue-i18n";
