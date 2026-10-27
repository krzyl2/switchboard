import { describe, expect, it } from 'vitest';
import type { PlanEntry, PlanSlot } from './api/plan';
import { costText, dayTime, priceTones, runsOf } from './planText';
import { messages } from './i18n';

// The helpers behind the day plan and the goal row. A run joins consecutive slots on one energy class (a person reads "22:00 to 06:00", not
// eight lines), and must never join a gap: a run broken by waiting is two runs. Prices are ranked, not named, so the colours mean the same
// for a G13 schedule and a market curve.
const H = 3_600_000;
const at = (hours: number) => new Date(Date.UTC(2026, 9, 26, 0) + hours * H);

function entry(from: number, want: PlanEntry['want'], cls: string | null, kwh = 1, power = 3000): PlanEntry {
  return { start: at(from), end: at(from + 1), want, class: cls, energyKwh: kwh, maxPowerW: power };
}

function slot(from: number, price: number | null): PlanSlot {
  return { start: at(from), end: at(from + 1), zone: null, importPrice: price, exportPrice: null, pvKwh: 0, baseKwh: 0, surplusKwh: 0 };
}

describe('runsOf', () => {
  it('joins consecutive entries of one class and adds their energy', () => {
    const runs = runsOf([entry(0, 'defer', null, 0, 0), entry(1, 'run', 'pv', 1.5, 2000), entry(2, 'run', 'pv', 2, 3000), entry(3, 'done', null, 0, 0)]);

    expect(runs).toEqual([{ from: at(1), to: at(3), class: 'pv', urgent: false, energyKwh: 3.5, maxPowerW: 3000 }]);
  });

  it('does not join across a wait, another class or an urgent slot', () => {
    const runs = runsOf([entry(0, 'run', 'pv'), entry(1, 'defer', null, 0, 0), entry(2, 'run', 'pv'), entry(3, 'run', 'zone:offpeak'), entry(4, 'urgent', 'zone:offpeak')]);

    expect(runs.map((r) => [r.from.getTime(), r.to.getTime(), r.class, r.urgent])).toEqual([
      [at(0).getTime(), at(1).getTime(), 'pv', false],
      [at(2).getTime(), at(3).getTime(), 'pv', false],
      [at(3).getTime(), at(4).getTime(), 'zone:offpeak', false],
      [at(4).getTime(), at(5).getTime(), 'zone:offpeak', true],
    ]);
  });

  it('has no run for a device that is not planned', () => {
    expect(runsOf([entry(0, 'none', null, 0, 0), entry(1, 'none', null, 0, 0)])).toEqual([]);
  });
});

describe('priceTones', () => {
  it('ranks the prices: cheapest, dearest and the ones between', () => {
    expect(priceTones([slot(0, 0.6), slot(1, 1.1), slot(2, 1.6), slot(3, 0.6)])).toEqual(['cheap', 'middle', 'dear', 'cheap']);
  });

  it('calls a single price flat and a missing one unknown', () => {
    expect(priceTones([slot(0, 0.6), slot(1, 0.6)])).toEqual(['flat', 'flat']);
    expect(priceTones([slot(0, 0.6), slot(1, null), slot(2, 1)])).toEqual(['cheap', 'unknown', 'dear']);
  });
});

describe('dayTime', () => {
  it('shows the clock alone for today and names the day for another', () => {
    const now = new Date('2026-10-26T12:00:00Z');

    expect(dayTime(new Date('2026-10-26T20:30:00Z'), now, 'en', 'Europe/Warsaw')).toBe('21:30');
    expect(dayTime(new Date('2026-10-27T05:00:00Z'), now, 'en', 'Europe/Warsaw')).toBe('Tue 06:00');
  });

  it('is right on the day the clocks go back (a 25-hour day)', () => {
    const now = new Date('2026-10-25T00:30:00Z');

    // 02:30 local happens twice that night; the instant after the change is 02:30 CET, one hour after 02:30 CEST.
    expect(dayTime(new Date('2026-10-25T01:30:00Z'), now, 'en', 'Europe/Warsaw')).toBe('02:30');
    expect(dayTime(new Date('2026-10-25T00:30:00Z'), now, 'en', 'Europe/Warsaw')).toBe('02:30');
  });
});

describe('costText', () => {
  it('keeps the currency label as written, and the bare number when no economics section names one', () => {
    expect(costText(messages.en, 'en', 3.4, 'PLN')).toBe('3.40 PLN');
    expect(costText(messages.pl, 'pl', -2.475, 'zł')).toBe('-2,48 zł');
    expect(costText(messages.en, 'en', 3.4, null)).toBe('3.40');
  });
});
