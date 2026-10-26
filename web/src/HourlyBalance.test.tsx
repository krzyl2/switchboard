import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, render } from '@testing-library/preact';
import { HourlyBalanceCard, secondsToHour, whText } from './HourlyBalance';
import { parseBalance } from './api/balance';
import balanceSample from './api/balance.sample.json';
import { advance, respond } from './screens.test-support';

let fetchMock: Mock<typeof fetch>;

function variant(change: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(balanceSample));
  change(copy);
  return copy;
}

async function show(body: unknown, lang: 'en' | 'pl' = 'en'): Promise<void> {
  fetchMock.mockImplementation(() => respond(structuredClone(body)));
  render(<HourlyBalanceCard lang={lang} />);
  await advance();
}

function row(name: string): string {
  const element = document.querySelector<HTMLElement>(`[data-row="${name}"]`);
  if (element === null) throw new Error(`No row ${name}.`);
  return element.textContent ?? '';
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

// BAL-07: the dashboard shows the balance of the running hour, its projection at :00, the band and
// the minutes to :00, in Wh and W with positive = export, and says why when it does not know.
describe('hourly balance card', () => {
  it('shows the balance, projection, band and time to :00 of the known hour', async () => {
    await show(balanceSample);

    expect(row('hb-balance')).toContain('+241 Wh');
    expect(row('hb-balance')).toContain('(export)');
    expect(row('hb-projection')).toBe('+410 Wh');
    expect(row('hb-band')).toBe('150 … 525 Wh');
    expect(row('hb-remaining')).toBe('22:00');
    expect(row('hb-free')).toBe('3,100 W');
    expect(row('hb-load')).toBe('1,650 W');
  });

  it('shows an import as a negative figure without a plus sign', async () => {
    await show(variant((p) => Object.assign(p, { wh: -120, projectionWh: -35.4 })));

    expect(row('hb-balance')).toContain('-120 Wh');
    expect(row('hb-balance')).toContain('(import)');
    expect(row('hb-projection')).toBe('-35 Wh');
  });

  it("says the band is burning down inside the burn window and counts the rule's own time", async () => {
    await show(variant((p) => Object.assign(p, { ruleSecondsToNextHour: 540, band: { loWh: 150, hiWh: 300, burning: true } })));

    expect(row('hb-band')).toContain('150 … 300 Wh');
    expect(row('hb-band')).toContain('burn-down');
    expect(row('hb-remaining')).toBe('9:00');
  });

  it('shows the reason instead of a figure when the balance is not known, never a zero', async () => {
    await show(variant((p) => Object.assign(p, { wh: null, reason: 'counter_reset', known: false, unknownReason: 'counter_reset', freeW: null, loadW: null, projectionWh: null, band: null, ruleSecondsToNextHour: 0 })));

    expect(row('hb-balance')).toBe('A counter decreased this hour; the balance returns at the next :00.');
    expect(row('hb-projection')).toContain('A counter decreased this hour');
    expect(row('hb-band')).toContain('A counter decreased this hour');
    expect(row('hb-free')).toBe('—');
    // The monitor's own time to :00 still counts while the rule has none.
    expect(row('hb-remaining')).toBe('22:00');
  });

  it('keeps the measured balance and says why the projection is missing when only free power is unknown', async () => {
    await show(variant((p) => Object.assign(p, { known: false, unknownReason: 'free_power_unknown', freeW: null, projectionWh: null })));

    expect(row('hb-balance')).toContain('+241 Wh');
    expect(row('hb-projection')).toContain('Free power is not known');
    expect(document.querySelector('.note')?.textContent).toContain('Free power is not known');
  });

  it('says so when no energy counters are mapped', async () => {
    await show(variant((p) => Object.assign(p, { configured: false, wh: null, hourStart: null, secondsToNextHour: null, known: false, freeW: null, loadW: null, projectionWh: null, band: null, ruleSecondsToNextHour: null, ruleAt: null })));

    expect(document.body.textContent).toContain('No energy counters are mapped');
    expect(document.querySelector('[data-row="hb-balance"]')).toBeNull();
  });

  it('is in Polish with the same units', async () => {
    await show(balanceSample, 'pl');

    expect(document.querySelector('h2')?.textContent).toBe('Bilans godzinowy');
    expect(row('hb-balance')).toContain('+241 Wh');
    expect(row('hb-balance')).toContain('(oddawanie)');
  });

  it('withdraws every figure when the add-on does not answer', async () => {
    fetchMock.mockImplementation(() => respond({}, 500));
    render(<HourlyBalanceCard lang="en" />);
    await advance();

    expect(document.querySelector('[data-row="hb-balance"]')).toBeNull();
    expect(document.querySelector('[role="alert"]')).not.toBeNull();
  });
});

describe('whText and secondsToHour', () => {
  it('prints a sign only for export and never "-0"', () => {
    expect(whText(0.2, 'en')).toBe('0 Wh');
    expect(whText(-0.2, 'en')).toBe('0 Wh');
    expect(whText(1, 'en')).toBe('+1 Wh');
  });

  it("uses the time the rule used while it knew, else the monitor's", () => {
    const known = parseBalance(balanceSample);
    expect(secondsToHour({ ...known, ruleSecondsToNextHour: 100, secondsToNextHour: 90 })).toBe(100);
    expect(secondsToHour({ ...known, known: false, ruleSecondsToNextHour: 0, secondsToNextHour: 90 })).toBe(90);
  });
});
