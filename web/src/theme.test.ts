import { afterEach, describe, expect, it, vi } from 'vitest';
import stylesheet from './styles.css?raw';
import { THEME_STORAGE_KEY, applyTheme, nextTheme, readTheme, saveTheme } from './theme';

afterEach(() => {
  vi.restoreAllMocks();
  window.localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
});

// UI-13: the page follows prefers-color-scheme and offers an explicit choice kept per browser.
describe('theme', () => {
  it('toggles system → light → dark → system and persists the explicit choices', () => {
    expect(readTheme()).toBe('system');
    const light = nextTheme('system');
    saveTheme(light);
    applyTheme(light);
    expect([light, readTheme(), document.documentElement.getAttribute('data-theme')]).toEqual(['light', 'light', 'light']);

    const dark = nextTheme(light);
    saveTheme(dark);
    applyTheme(dark);
    expect([dark, window.localStorage.getItem(THEME_STORAGE_KEY), document.documentElement.getAttribute('data-theme')]).toEqual(['dark', 'dark', 'dark']);

    const system = nextTheme(dark);
    saveTheme(system);
    applyTheme(system);
    // "system" leaves the decision to the stylesheet's prefers-color-scheme rule: no attribute, nothing stored.
    expect([system, window.localStorage.getItem(THEME_STORAGE_KEY), document.documentElement.hasAttribute('data-theme')]).toEqual(['system', null, false]);
  });

  it('still toggles for the session when storage throws (private window, blocked site data)', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'QuotaExceededError');
    });

    expect(readTheme()).toBe('system');
    expect(() => saveTheme('dark')).not.toThrow();
    applyTheme('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('ignores a stored value it does not know', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'neon');

    expect(readTheme()).toBe('system');
  });
});

// UI-11: controlled and foreign load never share a colour, in the light theme, the dark theme of the
// operating system and the explicitly chosen dark theme alike; the stylesheet is read as shipped.
describe('load colours', () => {
  const css = stylesheet.replace(/\r\n/g, '\n');

  function block(selectorPattern: RegExp): string {
    const match = selectorPattern.exec(css);
    if (match === null) throw new Error(`No block for ${selectorPattern}.`);
    return match[1]!;
  }

  function token(body: string, name: string): string {
    const match = new RegExp(`--${name}:\\s*([^;]+);`).exec(body);
    if (match === null) throw new Error(`--${name} is not defined in the block.`);
    return match[1]!.trim().toLowerCase();
  }

  const themes: [string, string][] = [
    ['light', block(/\n:root \{\n([\s\S]*?)\n\}/)],
    ['dark (system)', block(/:root:not\(\[data-theme='light'\]\) \{\n([\s\S]*?)\n {2}\}/)],
    ['dark (chosen)', block(/:root\[data-theme='dark'\] \{\n([\s\S]*?)\n\}/)],
  ];

  it.each(themes)('controlled and foreign differ in the %s theme, and neither is the base load', (_name, body) => {
    const controlled = token(body, 'load-controlled');
    const foreign = token(body, 'load-foreign');
    const base = token(body, 'load-base');

    expect(controlled).not.toBe(foreign);
    expect(base).not.toBe(controlled);
    expect(base).not.toBe(foreign);
  });

  it('defines every theme token in both dark blocks, so no dark page shows a light colour', () => {
    const names = (body: string) => [...body.matchAll(/--([a-z0-9-]+):/g)].map((m) => m[1]).filter((n) => !n!.startsWith('font'));
    const light = names(themes[0]![1]);

    expect(names(themes[1]![1])).toEqual(light);
    expect(names(themes[2]![1])).toEqual(light);
  });
});
