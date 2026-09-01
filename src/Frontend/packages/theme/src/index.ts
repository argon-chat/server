import { computed } from "vue";
import { persistedValue } from "@argon/storage";
import { logger } from "@argon/core";

export type ThemeId = "dark" | "light" | "oled" | "system";

/**
 * What the client's appearance panel would let a person change, frozen at its defaults.
 *
 * Upstream this is fourteen persisted settings — accent colour, density, dyslexia font, high
 * contrast, colour-blind filter, reduced motion, chat density and the rest. None of them can move
 * here: the widget answers on its own origin, it has no settings UI, and `localStorage` does not
 * cross origins, so nothing ever writes those keys and every read returns the default. What is
 * left is the handful of declarations whose defaults are actually visible on the page.
 *
 * The accent is `hexToHSL("#3b82f6")` — the "blue" entry of the twelve the client offers — worked
 * out once rather than at every mount, since there is no other accent to pick.
 */
const APPEARANCE = {
  fontFamily: "Inter, sans-serif, 'Noto Color Emoji'",
  fontSize: "14px",
  lineHeight: "1.5",
  radius: "0.75rem",
  accent: "217 91% 60%",
} as const;

/** Pure black, for the one theme that is not just a class on <html>. */
const oledOverrides: Record<string, string> = {
  "--background": "0 0% 0%",
  "--foreground": "0 0% 98%",
  "--card": "0 0% 0%",
  "--card-foreground": "0 0% 98%",
  "--popover": "0 0% 0%",
  "--popover-foreground": "0 0% 98%",
  "--primary": "0 0% 98%",
  "--primary-foreground": "0 0% 0%",
  "--secondary": "0 0% 10%",
  "--secondary-foreground": "0 0% 98%",
  "--muted": "0 0% 10%",
  "--muted-foreground": "0 0% 64.9%",
  "--accent": "0 0% 10%",
  "--accent-foreground": "0 0% 98%",
  "--border": "0 0% 15%",
  "--input": "0 0% 15%",
  "--ring": "0 0% 83.9%",
};

export function useTheme() {
  const currentTheme = persistedValue<string>("appearance.theme", "dark");

  const applyTheme = (themeId?: ThemeId) => {
    const theme = (themeId || currentTheme.value) as ThemeId;
    const html = document.documentElement;

    html.classList.remove("dark", "light", "oled");

    // Cleared before anything is written, so switching out of OLED does not leave its inline
    // variables shadowing the stylesheet.
    Object.keys(oledOverrides).forEach((v) => html.style.removeProperty(v));

    const resolved =
      theme === "system"
        ? (window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light")
        : theme;

    logger.info("[theme] applyTheme:", theme, "→", resolved);

    if (resolved === "oled") {
      html.classList.add("dark");
      Object.entries(oledOverrides).forEach(([key, value]) => html.style.setProperty(key, value));
    } else if (resolved === "dark") {
      html.classList.add("dark");
    }
    // Light needs no class: the stylesheet's :root is the light palette.

    currentTheme.value = theme; // may be "system", which is not the same as what it resolved to

    document.body.classList.toggle("oled-theme", resolved === "oled");
  };

  const applyAppearanceSettings = () => {
    applyTheme();

    const root = document.documentElement;

    root.style.setProperty("font-family", APPEARANCE.fontFamily);
    root.style.fontSize = APPEARANCE.fontSize;
    root.style.lineHeight = APPEARANCE.lineHeight;
    root.style.setProperty("--radius", APPEARANCE.radius);
    root.style.setProperty("--primary", APPEARANCE.accent);
    root.style.setProperty("--ring", APPEARANCE.accent);
  };

  return {
    currentTheme: computed(() => currentTheme.value as ThemeId),
    applyAppearanceSettings,
  };
}
