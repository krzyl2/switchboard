import { describe, expect, it } from 'vitest';
import { parseNumber } from './parseNumber';

// One parser for every configuration field of the wizard (the meter and connection steps used to
// clear a bad entry silently, the loads step cleared it and said so, and both let Number() read
// "0x10" as 16). A value that reaches the draft is one the user meant.
describe('parseNumber', () => {
  it.each([
    ['16', 16],
    ['0,35', 0.35],
    ['0.35', 0.35],
    ['  230 ', 230],
    ['-1,5', -1.5],
    ['+4', 4],
    ['5.', 5],
    ['5,', 5],
    ['.5', 0.5],
    [',5', 0.5],
    ['007', 7],
  ])('reads %j as %d', (text, expected) => {
    expect(parseNumber(text)).toBe(expected);
  });

  it.each([[''], ['   ']])('treats %j as a field cleared on purpose (null)', (text) => {
    expect(parseNumber(text)).toBeNull();
  });

  // undefined (not null) so a caller can tell "cleared" from "not a number" and say so.
  it.each([['abc'], ['-'], ['.'], ['1,5,5'], ['1.5.5'], ['1 000'], ['0x10'], ['0b11'], ['1e3'], ['Infinity'], ['NaN'], ['--5'], ['5 A']])('refuses %j (undefined)', (text) => {
    expect(parseNumber(text)).toBeUndefined();
  });
});
