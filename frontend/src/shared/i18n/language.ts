/** The languages the application has; no choice (an empty one) follows the browser. */
export const LANGUAGES = ['ru', 'en'] as const;
export type Language = (typeof LANGUAGES)[number];

/** The language of the first screens when nothing else is known; Russian is the default of the application. */
export const DEFAULT_LANGUAGE: Language = 'ru';

/** Where the last explicit choice is kept in the browser, so that the sign-in screen and the first paint already speak that language. */
const STORAGE_KEY = 'vinette.language';

/** The language to show for a choice: the chosen one, or, for none, the browser's if the application has it, else Russian. */
export function languageFor(
  choice: string | null | undefined,
  browser: string = typeof navigator === 'undefined' ? '' : navigator.language,
): Language {
  const own = LANGUAGES.find((l) => l === choice);
  if (own) return own;
  return LANGUAGES.find((l) => browser.toLowerCase().startsWith(l)) ?? DEFAULT_LANGUAGE;
}

/** The language the user chose last in this browser, if any (the browser may keep nothing: then none). */
export function rememberedLanguage(): Language | null {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return LANGUAGES.find((l) => l === stored) ?? null;
  } catch {
    return null;
  }
}

/** Keeps an explicit choice in the browser; no choice (follow the browser) forgets it. */
export function rememberLanguage(choice: string | null | undefined): void {
  try {
    const own = LANGUAGES.find((l) => l === choice);
    if (own) localStorage.setItem(STORAGE_KEY, own);
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    // the browser keeps nothing: the next start follows the browser, which is harmless
  }
}

/** The language to start with: the remembered choice, else the browser's. */
export function initialLanguage(): Language {
  return rememberedLanguage() ?? languageFor(null);
}

/** Tells the browser and assistive technology which language the page is in (spelling, hyphenation, screen reader voice). */
export function applyDocumentLanguage(language: string): void {
  if (typeof document !== 'undefined') document.documentElement.lang = language;
}
