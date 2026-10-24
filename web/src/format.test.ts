import { describe, expect, it } from 'vitest';
import { formatAge, formatCountdown, formatInterval, formatQuantity } from './format';

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
