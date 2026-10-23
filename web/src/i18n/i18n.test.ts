import { describe, expect, it } from 'vitest';
import { detectLanguage, messages } from '.';

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
