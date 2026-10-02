import { AppTextError } from "@/lib/glue/accountConsole"

/** A localized string the console edits, with the limits the server enforces for its key. */
export interface AppTextKeyInfo {
  key: string
  maxLength: number
  singleLine: boolean
}

export const APP_TEXT_KEYS = {
  motd: { key: "motd", maxLength: 100, singleLine: true },
} as const satisfies Record<string, AppTextKeyInfo>

export interface AppTextLocale {
  code: string
  name: string
}

/**
 * The languages of the Argon app, as it spells them; mirrors `client/src/lib/languages.ts`.
 * `en_tengwar` is left out: it is English in another script, and its readers get the English value.
 * A reader whose language is empty here gets the value without the variant (`ru_pt` → `ru`), then English.
 */
export const APP_TEXT_LOCALES: readonly AppTextLocale[] = [
  { code: "en", name: "English" },
  { code: "ru", name: "Русский" },
  { code: "ru_pt", name: "Пирацкий" },
  { code: "jp", name: "日本語" },
  { code: "am", name: "Հայկական" },
  { code: "es", name: "Español" },
  { code: "de", name: "Deutsch" },
  { code: "pl", name: "Polski" },
  { code: "ko", name: "한국어" },
  { code: "kk", name: "Қазақша" },
  { code: "uz", name: "Oʻzbekcha" },
]

/** What the server stores for a one-line key: runs of whitespace become one space, the ends are trimmed. */
export function normalizeAppText(raw: string, singleLine: boolean): string {
  return singleLine
    ? raw.replace(/[\s\p{Cc}]+/gu, " ").trim()
    : raw.replace(/\r\n?/g, "\n").replace(/[^\P{Cc}\n]/gu, "").trim()
}

const segmenter = new Intl.Segmenter(undefined, { granularity: "grapheme" })

/** Characters as a person counts them, the way the server counts them: an emoji is one. */
export function appTextLength(raw: string, singleLine: boolean): number {
  let count = 0
  for (const _ of segmenter.segment(normalizeAppText(raw, singleLine))) count++
  return count
}

export function appTextErrorMessage(error: AppTextError, locale: string | null, maxLength: number): string {
  const where = locale ? ` (${locale})` : ""
  switch (error) {
    case AppTextError.NO_PERMISSION: return "You don't have access to this team."
    case AppTextError.NOT_FOUND: return "App not found."
    case AppTextError.UNKNOWN_KEY: return "This kind of app has no such text."
    case AppTextError.ENGLISH_REQUIRED: return "English is required whenever any language has a value."
    case AppTextError.TOO_LONG: return `A value is longer than ${maxLength} characters${where}.`
    case AppTextError.INVALID_LOCALE: return `Unknown language code${where}.`
    case AppTextError.DUPLICATE_LOCALE: return `A language appears twice${where}.`
    case AppTextError.TOO_MANY_LOCALES: return "Too many languages."
    default: return "Internal server error."
  }
}
