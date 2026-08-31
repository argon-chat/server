import { persistedValue } from "@argon/storage";
import { coreMessages, type SupportedLocale } from "@argon/i18n";
import { defineStore } from "pinia";
import { watch } from "vue";
import { useI18n } from "vue-i18n";

const supportedLocales = Object.keys(coreMessages) as SupportedLocale[];

const langMap: Record<string, SupportedLocale> = {
    ja: "jp",
    hy: "am",
};

function detectBrowserLocale(): SupportedLocale {
    for (const lang of navigator.languages ?? [navigator.language]) {
        const tag = lang.toLowerCase();
        // exact match (e.g. "ru", "en")
        if (supportedLocales.includes(tag as SupportedLocale)) return tag as SupportedLocale;
        // mapped match (e.g. "ja" → "jp", "hy" → "am")
        if (langMap[tag]) return langMap[tag];
        // base language (e.g. "ru-RU" → "ru", "ja-JP" → "jp")
        const base = tag.split("-")[0];
        if (supportedLocales.includes(base as SupportedLocale)) return base as SupportedLocale;
        if (langMap[base]) return langMap[base];
    }
    return "en";
}

export const useLocale = defineStore("locale", () => {
    const currentLocale = persistedValue<string>("locale", detectBrowserLocale());

    const { t, locale } = useI18n({
        legacy: false,
        locale: "en",
        fallbackLocale: "en",
        messages: coreMessages as any,
        silentTranslationWarn: true,
        missingWarn: false,
        fallbackWarn: false,
        warnHtmlMessage: false,
    } as any);

    function updateLocale(key: string) {
        currentLocale.value = key as any;
    }

    locale.value = currentLocale.value as any;

    watch(currentLocale, (x) => {
        locale.value = x as any;
    });

    return {
        t,
        currentLocale,
        updateLocale,
    };
});
