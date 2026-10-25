// UI-13: light and dark theming. The ingress panel cannot read the Home Assistant theme
// (07-PITFALLS), so the page follows prefers-color-scheme and offers an explicit choice, kept per
// browser. Storage may be unavailable (private window, blocked site data): every access is guarded,
// and the toggle still works for the session without it.

export const THEME_CHOICES = ['system', 'light', 'dark'] as const;
export type ThemeChoice = (typeof THEME_CHOICES)[number];

/** The per-browser key; a convenience only, never state the add-on depends on. */
export const THEME_STORAGE_KEY = 'switchboard.theme';

function storage(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/** The stored choice, or "system" when nothing (valid) is stored or storage cannot be read. */
export function readTheme(): ThemeChoice {
  try {
    const stored = storage()?.getItem(THEME_STORAGE_KEY);
    return (THEME_CHOICES as readonly string[]).includes(stored ?? '') ? (stored as ThemeChoice) : 'system';
  } catch {
    return 'system';
  }
}

/** Keeps the choice for the next visit, if the browser lets the page store anything. */
export function saveTheme(choice: ThemeChoice): void {
  try {
    const store = storage();
    if (choice === 'system') store?.removeItem(THEME_STORAGE_KEY);
    else store?.setItem(THEME_STORAGE_KEY, choice);
  } catch {
    // The choice still applies for this session.
  }
}

/** system → light → dark → system. */
export function nextTheme(choice: ThemeChoice): ThemeChoice {
  return THEME_CHOICES[(THEME_CHOICES.indexOf(choice) + 1) % THEME_CHOICES.length]!;
}

/** "system" removes the attribute, so the stylesheet's prefers-color-scheme rule decides. */
export function applyTheme(choice: ThemeChoice, root: HTMLElement = document.documentElement): void {
  if (choice === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', choice);
}
