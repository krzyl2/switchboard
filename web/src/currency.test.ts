import { describe, expect, it } from 'vitest';

// 14-ECONOMICS: no currency symbol anywhere in code or defaults. A price or an amount carries the label the
// user wrote under economics.currency, shown verbatim; the SPA must not know any currency of its own, in
// either language (a Polish text with "zł" would show it to a user who configured euro).
const sources = import.meta.glob(['./**/*.ts', './**/*.tsx', './**/*.css'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

const SYMBOL = /[€£¥₹₽]|(?<![\p{L}])zł(?![\p{L}])|\b(PLN|EUR|USD|GBP|CHF|CZK|SEK|NOK|DKK|HUF|RON|UAH)\b|\$\d|\d\$(?!\{)/u;

describe('currency', () => {
  const files = Object.entries(sources).filter(([name]) => !/\.test\.|test-support/.test(name));

  it('scans the shipped sources, not nothing', () => {
    expect(files.length).toBeGreaterThan(40);
    expect(files.map(([name]) => name)).toContain('./Economics.tsx');
    expect(files.map(([name]) => name)).toContain('./i18n/dashboard.ts');
  });

  it('names no currency or currency symbol in any shipped source', () => {
    const offenders = files.flatMap(([name, text]) =>
      text.split(/\r?\n/).flatMap((line, i) => (SYMBOL.test(line) ? [`${name}:${i + 1}: ${line.trim().slice(0, 100)}`] : [])),
    );

    expect(offenders).toEqual([]);
  });

  it('the pattern catches what it is there for', () => {
    for (const bad of ['1,10 zł', '€ 0.30', 'PLN/kWh', 'price $0.30', '0.30$', 'EUR']) expect(SYMBOL.test(bad)).toBe(true);
    for (const fine of ['{currency}', 'per kWh', 'priceText(t, lang, value, currency)', 'US ${value}']) expect(SYMBOL.test(fine)).toBe(false);
  });
});
