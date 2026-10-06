/**
 * Localisation coverage and correctness for the Aegis widget — the client's `validate:locales`,
 * pointed at this workspace.
 *
 * Every string comes from `packages/i18n/src/core/<locale>.json`, with `en` as the reference and the
 * runtime fallback. vue-i18n renders a missing key as the key itself, so:
 *
 *   1. A key used in code but defined in no locale fails — the user would read `editing_resets_step`.
 *   2. A key in a translation but not in `en` fails — unreachable, usually a half-finished rename.
 *   3. A message vue-i18n cannot compile (a bare `@`, an unbalanced `{`) fails — the component
 *      rendering it would crash.
 *   4. A locale behind `en` is reported, and fails only under `--strict`.
 *
 * Keys assembled at runtime (`t(`consent.scopes.${key}.name`)`) are matched by pattern.
 *
 *   bun run validate:locales             # report; fail on 1–3
 *   bun run validate:locales --strict    # also fail when any locale is missing keys
 *   bun run validate:locales --full      # list every key instead of a sample
 */

import { existsSync, readFileSync } from "node:fs";
import { relative, resolve } from "node:path";
import { baseCompile, CompileErrorCodes } from "@intlify/message-compiler";

const root = resolve(import.meta.dir, "..");
const LOCALE_DIR = resolve(root, "packages", "i18n", "src", "core");
const IGNORE_FILE = resolve(root, "packages", "i18n", "validate-locales.ignore.json");
const REFERENCE = "en";
const SOURCE_GLOBS = ["Aegis/src/**/*.{ts,vue}", "packages/*/src/**/*.{ts,vue}"];
const SAMPLE = 25;

const args = new Set(process.argv.slice(2));
const strict = args.has("--strict");
const full = args.has("--full");

// ── locales ─────────────────────────────────────────────────────────────────────────────────────

function readMessages(locale: string): Map<string, string> {
  const out = new Map<string, string>();
  const walk = (value: unknown, prefix: string) => {
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
        walk(child, prefix ? `${prefix}.${key}` : key);
      }
    } else if (prefix) {
      out.set(prefix, String(value));
    }
  };
  walk(JSON.parse(readFileSync(resolve(LOCALE_DIR, `${locale}.json`), "utf8")), "");
  return out;
}

const locales = [...new Bun.Glob("*.json").scanSync({ cwd: LOCALE_DIR })]
  .map((f) => f.replace(/\.json$/, ""))
  .sort((a, b) => (a === REFERENCE ? -1 : b === REFERENCE ? 1 : a.localeCompare(b)));

if (!locales.includes(REFERENCE)) {
  console.error(`No ${REFERENCE}.json in ${relative(root, LOCALE_DIR)} — nothing to measure against.`);
  process.exit(2);
}

const messagesByLocale = new Map(locales.map((locale) => [locale, readMessages(locale)]));
const reference = messagesByLocale.get(REFERENCE)!;

// ── syntax ──────────────────────────────────────────────────────────────────────────────────────

const codeNames = new Map(Object.entries(CompileErrorCodes).map(([name, code]) => [code, name]));
const syntaxProblems: string[] = [];
for (const [locale, messages] of messagesByLocale) {
  for (const [key, message] of messages) {
    try {
      baseCompile(message, { onError: (error) => { throw error; } });
    } catch (error) {
      const code = (error as { code?: number }).code;
      const name = code === undefined ? String(error) : (codeNames.get(code) ?? String(code));
      syntaxProblems.push(`${locale}  ${key}  ${name}  ${JSON.stringify(message)}`);
    }
  }
}

// ── how the code refers to them ─────────────────────────────────────────────────────────────────

/** `t("key")`, `$t('key')`; the lookbehind keeps `it(`, `format(` and friends out. */
const LITERAL_CALL = /(?<![\w$])\$?t\(\s*(['"])((?:[^'"\\]|\\.)*?)\1/g;
const TEMPLATE_CALL = /(?<![\w$])\$?t\(\s*`([^`]*)`/g;
/** Any quoted string, for keys that travel as data before reaching t(). */
const ANY_STRING = /(['"])((?:[^'"\\\n]|\\.)*?)\1/g;

const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

function dynamicPattern(template: string): RegExp | null {
  if (!template.includes("${")) return null;
  const masked = template.replace(/\$\{[^{}]*\}/g, "\u0000");
  if (masked.includes("${")) return null;
  return new RegExp(`^${masked.split("\u0000").map(escapeRegex).join(".*")}$`);
}

const literalKeys = new Map<string, string[]>();
const dynamicPatterns: RegExp[] = [];
const mentionedStrings = new Set<string>();

for (const pattern of SOURCE_GLOBS) {
  for (const file of new Bun.Glob(pattern).scanSync({ cwd: root, absolute: true })) {
    if (file.includes("node_modules")) continue;
    const text = readFileSync(file, "utf8");
    const rel = relative(root, file).replaceAll("\\", "/");
    const lineOf = (index: number) => text.slice(0, index).split("\n").length;

    const add = (key: string, index: number) => {
      const at = `${rel}:${lineOf(index)}`;
      literalKeys.set(key, [...(literalKeys.get(key) ?? []), at]);
    };

    for (const match of text.matchAll(LITERAL_CALL)) add(match[2], match.index);
    for (const match of text.matchAll(TEMPLATE_CALL)) {
      const pattern = dynamicPattern(match[1]);
      if (pattern) dynamicPatterns.push(pattern);
      else add(match[1], match.index);
    }
    for (const match of text.matchAll(ANY_STRING)) mentionedStrings.add(match[2]);
  }
}

// ── findings ────────────────────────────────────────────────────────────────────────────────────

const ignored = new Set<string>(existsSync(IGNORE_FILE) ? JSON.parse(readFileSync(IGNORE_FILE, "utf8")) : []);

const undefinedKeys = [...literalKeys.keys()]
  .filter((key) => key.length && !/\s/.test(key) && !reference.has(key))
  .sort();

const orphans = new Map<string, string[]>();
const missing = new Map<string, string[]>();
for (const [locale, messages] of messagesByLocale) {
  if (locale === REFERENCE) continue;
  const extra = [...messages.keys()].filter((key) => !reference.has(key)).sort();
  if (extra.length) orphans.set(locale, extra);
  missing.set(locale, [...reference.keys()].filter((key) => !messages.has(key)).sort());
}

const isReferenced = (key: string) =>
  literalKeys.has(key) || mentionedStrings.has(key) || dynamicPatterns.some((pattern) => pattern.test(key));
const unused = [...reference.keys()].filter((key) => !ignored.has(key) && !isReferenced(key)).sort();

// ── output ──────────────────────────────────────────────────────────────────────────────────────

const list = (items: string[]) => {
  const shown = full ? items : items.slice(0, SAMPLE);
  for (const item of shown) console.log(`    ${item}`);
  if (shown.length < items.length) console.log(`    … ${items.length - shown.length} more (--full to list them)`);
};

console.log(`\nLocales — ${relative(root, LOCALE_DIR).replaceAll("\\", "/")}, measured against ${REFERENCE}\n`);
for (const locale of locales) {
  const gap = locale === REFERENCE ? 0 : missing.get(locale)!.length;
  const percent = (((reference.size - gap) / reference.size) * 100).toFixed(1);
  console.log(`  ${locale.padEnd(8)} ${String(reference.size - gap).padStart(5)}/${reference.size}  ${percent.padStart(6)}%`);
}

for (const [locale, keys] of missing) {
  if (!keys.length) continue;
  console.log(`\n  Missing from ${locale} (${keys.length})`);
  list(keys);
}

if (undefinedKeys.length) {
  console.log(`\n✗ Used in code, defined in no locale (${undefinedKeys.length}) — these render as the key itself`);
  list(undefinedKeys.map((key) => `${key}  ${literalKeys.get(key)!.join(", ")}`));
}

if (syntaxProblems.length) {
  console.log(`\n✗ Messages vue-i18n cannot compile (${syntaxProblems.length}) — the component rendering them crashes`);
  list(syntaxProblems);
  console.log("    (a literal @ or { must be written as {'@'} / {'{'})");
}

for (const [locale, keys] of orphans) {
  console.log(`\n✗ In ${locale} but not in ${REFERENCE} (${keys.length}) — unreachable, usually a half-finished rename`);
  list(keys);
}

if (unused.length) {
  console.log(`\n· Defined but never referenced (${unused.length}) — dead weight, or a key built in a way this cannot see`);
  list(unused);
  console.log(`    (add a key to packages/i18n/validate-locales.ignore.json if it is reached indirectly)`);
}

console.log("");

// ── verdict ─────────────────────────────────────────────────────────────────────────────────────

const failures: string[] = [];
if (syntaxProblems.length) failures.push(`${syntaxProblems.length} message(s) vue-i18n cannot compile`);
if (undefinedKeys.length) failures.push(`${undefinedKeys.length} key(s) used in code but defined in no locale: ${undefinedKeys.slice(0, 5).join(", ")}`);
if (orphans.size) failures.push(`${[...orphans.values()].flat().length} key(s) translated but absent from ${REFERENCE}`);
if (strict) {
  for (const [locale, keys] of missing) if (keys.length) failures.push(`${locale} is missing ${keys.length} key(s)`);
}

if (failures.length) {
  console.error(`Locale check failed:\n${failures.map((f) => `  - ${f}`).join("\n")}\n`);
  process.exit(1);
}
