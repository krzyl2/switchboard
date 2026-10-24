import { describe, expect, it } from 'vitest';
import { detectLanguage, fill, messages } from '.';

// UI-14: every piece of chrome must exist in both languages. A key present in one file only would
// render as undefined (blank) for half of the users, and an empty string hides a label silently.
describe('translations', () => {
  it('en and pl define exactly the same keys', () => {
    expect(Object.keys(messages.pl).sort()).toEqual(Object.keys(messages.en).sort());
  });

  it.each(Object.entries(messages))('%s has no empty strings', (_lang, table) => {
    const empty = Object.entries(table)
      .filter(([, text]) => typeof text !== 'string' || text.trim() === '')
      .map(([key]) => key);
    expect(empty).toEqual([]);
  });

  // fill() puts numbers into {name} placeholders; a translation that drops or renames one would show
  // a sentence without its number, or fail to render.
  it('en and pl use the same placeholders in every text', () => {
    const names = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    const differing = Object.keys(messages.en).filter(
      (key) =>
        JSON.stringify(names(messages.en[key as keyof typeof messages.en])) !==
        JSON.stringify(names(messages.pl[key as keyof typeof messages.pl])),
    );
    expect(differing).toEqual([]);
  });

  it('pl is actually translated, not a copy of en', () => {
    expect(messages.pl.notProtectiveTitle).not.toBe(messages.en.notProtectiveTitle);
    expect(messages.pl.modeShadowNote).not.toBe(messages.en.modeShadowNote);
  });
});

describe('detectLanguage', () => {
  it.each(['pl', 'pl-PL', 'PL-pl'])('picks Polish for %s', (browserLanguage) => {
    expect(detectLanguage(browserLanguage)).toBe('pl');
  });

  // "plt" (Plateau Malagasy) starts with the letters "pl" but is not Polish.
  it.each(['en-US', 'de-DE', 'plt', '', undefined])('falls back to English for %s', (browserLanguage) => {
    expect(detectLanguage(browserLanguage)).toBe('en');
  });
});

describe('fill', () => {
  it('puts every value into its placeholder', () => {
    expect(fill('{from}–{upTo}×', { from: '1,25', upTo: '1,45' })).toBe('1,25–1,45×');
  });

  // A sentence that silently lost its number would read as complete.
  it('refuses a placeholder without a value', () => {
    expect(() => fill('full again in {time}', {})).toThrow();
  });
});
