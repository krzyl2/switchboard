import { describe, expect, it } from 'vitest';
import { formatAge, formatAmps, formatCountdown, formatFixed, formatInterval, formatKw, formatKwh, formatPercent, formatQuantity, roundTo } from './format';

// Ages are what lets a user tell a frozen sensor from a calm one, so they must read the same way
// everywhere and never hide a long silence behind a small number.
describe('formatAge', () => {
  it.each([
    [0, '0 s'],
    [13.555, '13 s'],
    [119.9, '119 s'],
    [120, '2 min'],
    [200, '3 min'],
    [7199, '119 min'],
    [7200, '2 h'],
    [19 * 3600 + 59, '19 h'],
  ])('%s s reads as %s', (seconds, text) => {
    expect(formatAge(seconds, 'en')).toBe(text);
  });

  // A report stamped slightly in the future (clocks out of step) shows as such instead of as "0 s".
  it('keeps the sign of a negative age', () => {
    expect(formatAge(-3.2, 'en')).toBe('-3 s');
  });
});

describe('formatInterval', () => {
  // A 2.5 s charger rounded to "2 s" or "3 s" would look like a different device.
  it('keeps one decimal below two minutes', () => {
    expect(formatInterval(2.5, 'en')).toBe('2.5 s');
    expect(formatInterval(30, 'en')).toBe('30 s');
    expect(formatInterval(2.5, 'pl')).toBe('2,5 s');
  });
});

describe('formatQuantity', () => {
  it('formats with the language and the unit', () => {
    expect(formatQuantity(-5.466, 'A', 'en')).toBe('-5.47 A');
    expect(formatQuantity(-5.466, 'A', 'pl')).toBe('-5,47 A');
    expect(formatQuantity(2240.6, 'W', 'en')).toBe('2,241 W');
    expect(formatQuantity(247.96, 'V', 'en')).toBe('248 V');
  });

  it('never prints a negative zero', () => {
    expect(formatQuantity(-0.2, 'W', 'en')).toBe('0 W');
  });
});

// A lockout countdown reads like the mockup's "blokada postoju 4:12" and never shows 0:00 while
// time is still left (rounded up), nor a negative time once it expired.
describe('formatCountdown', () => {
  it.each([
    [252, '4:12'],
    [42, '0:42'],
    [0.2, '0:01'],
    [0, '0:00'],
    [-3, '0:00'],
    [3600, '60:00'],
  ])('%s s reads as %s', (seconds, text) => {
    expect(formatCountdown(seconds)).toBe(text);
  });
});

// The runtime screens show power as the mockup does ("4,32 kW", "12,6 A", "2,10 kWh", "50%"), with a
// fixed number of decimals and one rounding rule (half away from zero), which the dashboard's sum
// check relies on.
describe('runtime screen numbers', () => {
  it.each([
    [1500, 'en', '1.50 kW'],
    [4320, 'pl', '4,32 kW'],
    [-450, 'pl', '-0,45 kW'],
    [-4, 'en', '0.00 kW'],
    [5, 'en', '0.01 kW'],
  ] as const)('%s W reads as %s (%s)', (watts, lang, text) => {
    expect(formatKw(watts, lang)).toBe(text);
  });

  it('formats currents with one decimal, energy with two, shares without decimals', () => {
    expect(formatAmps(12.64, 'pl')).toBe('12,6 A');
    expect(formatKwh(2.1, 'en')).toBe('2.10 kWh');
    expect(formatPercent(0.504, 'en')).toBe('50%');
    expect(formatFixed(-0.004, 'en', 2)).toBe('0.00');
  });

  it('rounds half away from zero, on both sides of zero', () => {
    expect([roundTo(0.125, 2), roundTo(-0.125, 2), roundTo(2.5, 0), roundTo(-2.5, 0)]).toEqual([0.13, -0.13, 3, -3]);
  });
});
