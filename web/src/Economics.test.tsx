import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, render, screen } from '@testing-library/preact';
import { Dashboard } from './Dashboard';
import balanceSample from './api/balance.sample.json';
import devicesSample from './api/devices.sample.json';
import dispatchSample from './api/dispatch.sample.json';
import economicsSample from './api/economics.sample.json';
import powerSample from './api/power.sample.json';
import statisticsSample from './api/statistics.sample.json';
import type { Lang } from './i18n';
import { advance, serveSamples } from './screens.test-support';

// ECO-12: the tariff status and savings cards on the dashboard. The numbers come from the sample the
// backend test holds to the real endpoint (economics.sample.json): offpeak now at 0.60 EUR per kWh, the
// afternoon peak at 16:00 local, savings 2.82 EUR over three counted hours, the car at -0.41 EUR.

let fetchMock: Mock<typeof fetch>;

const ZONE = 'Europe/Warsaw';

const SAMPLES = {
  'api/power': powerSample,
  'api/dispatch': dispatchSample,
  'api/devices': devicesSample,
  'api/statistics': statisticsSample,
  'api/balance': balanceSample,
  'api/economics': economicsSample,
};

function variant<T>(sample: T, change: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change(copy);
  return copy;
}

async function show(lang: Lang = 'en', economics: unknown = economicsSample): Promise<void> {
  serveSamples(fetchMock, { ...SAMPLES, 'api/economics': economics });
  render(<Dashboard lang={lang} zone={ZONE} />);
  await advance();
  await advance();
}

const tariff = () => document.querySelector<HTMLElement>('[data-card="tariff"]');
const savings = () => document.querySelector<HTMLElement>('[data-card="savings"]');

function row(name: string): HTMLElement {
  const element = document.querySelector<HTMLElement>(`[data-row="${name}"]`);
  if (element === null) throw new Error(`No row ${name}.`);
  return element;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-26T12:38:03Z'));
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  window.location.hash = '#/';
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('tariff status card', () => {
  it('shows the zone now, both prices with the label from the configuration, their basis and the next change', async () => {
    await show();

    expect(screen.getByRole('heading', { name: 'Tariff' })).toBeTruthy();
    expect(row('eco-zone').textContent).toBe('offpeak');
    expect(row('eco-import').textContent).toBe('0.60 EUR per kWhzone');
    expect(row('eco-export').textContent).toBe('0.35 EUR per kWhflat');
    // 15:00Z is 16:00 in Warsaw after the clocks went back, 142 minutes (shown as 2 h) from the sample's 12:38Z.
    expect(row('eco-next').textContent).toBe('16:00 (in 2 h)zone afternoon_peak');
  });

  it('writes the zone ids in monospace, the current and the next one', async () => {
    await show();

    const mono = [...tariff()!.querySelectorAll('.mono')].map((e) => e.textContent);
    expect(mono).toContain('offpeak');
    expect(mono).toContain('afternoon_peak');
    expect(row('eco-zone').querySelector('.mono')?.textContent).toBe('offpeak');
  });

  // The label is the user's own word: a different one must come through unchanged, and no symbol of ours may stand in for it.
  it('prints whatever currency label the data carries, never a symbol of its own', async () => {
    await show('en', variant(economicsSample, (p) => {
      p.currency = 'kr';
      p.savings.currency = 'kr';
    }));

    expect(row('eco-import').textContent).toBe('0.60 kr per kWhzone');
    expect(tariff()!.textContent).not.toMatch(/EUR|PLN|zł|[€$£]/);
    expect(savings()!.textContent).not.toMatch(/EUR|PLN|zł|[€$£]/);
    expect(row('eco-savings').textContent).toContain('2.82 kr');
  });

  it('says the same in Polish with the same label', async () => {
    await show('pl');

    expect(screen.getByRole('heading', { name: 'Taryfa' })).toBeTruthy();
    expect(row('eco-import').textContent).toBe('0,60 EUR za kWhstrefa');
    expect(row('eco-export').textContent).toBe('0,35 EUR za kWhstała');
    expect(row('eco-next').textContent).toBe('16:00 (za 2 h)strefa afternoon_peak');
  });

  it('keeps a price that is not exact to two decimals exact', async () => {
    await show('en', variant(economicsSample, (p) => (p.tariff.importPrice = 0.3547)));

    expect(row('eco-import').textContent).toContain('0.3547 EUR per kWh');
  });

  it('flags a price at its fallback with a warning pill and says the last known value is never used', async () => {
    await show('en', variant(economicsSample, (p) => {
      Object.assign(p.tariff, {
        kind: 'dynamic',
        priceBasis: 'fallback',
        fallbackInForce: true,
        signal: { name: 'price_import', state: 'stale', detail: 'max_age_exceeded', perKwh: null, ageS: 3600 },
      });
    }));

    const pill = row('eco-import').querySelector('.badge')!;
    expect(pill.textContent).toBe('fallback');
    expect(pill.getAttribute('data-tone')).toBe('warn');
    expect(tariff()!.textContent).toContain('the last known value is never used');
    // The signal is named in monospace with its state and age, never an entity id.
    expect(row('eco-signal').querySelector('.mono')?.textContent).toBe('price_import');
    expect(row('eco-signal').textContent).toBe('price_importstale, last reported 60 min ago');
  });

  it('shows a flat tariff without a zone and without a next change', async () => {
    await show('en', variant(economicsSample, (p) => Object.assign(p.tariff, { kind: 'flat', zone: null, priceBasis: 'flat', nextChangeAt: null, nextZone: null })));

    expect(row('eco-zone').textContent).toBe('none (a flat price)');
    expect(row('eco-next').textContent).toBe('no change ahead');
  });

  it('says the export price is not configured instead of showing a guess', async () => {
    await show('en', variant(economicsSample, (p) => (p.settlement = null)));

    expect(row('eco-export').textContent).toContain('not configured');
  });

  it('shows no card without an economics section, and no card when the answer cannot be read', async () => {
    await show('en', variant(economicsSample, (p) => Object.assign(p, { configured: false, currency: null, tariff: null, settlement: null, devices: [], savings: null })));
    expect(tariff()).toBeNull();
    expect(savings()).toBeNull();

    cleanup();
    await show('en', { nonsense: true });
    expect(tariff()).toBeNull();
    expect(savings()).toBeNull();
  });
});

describe('savings card', () => {
  it('shows today’s savings with the label, what it is measured against and since when', async () => {
    await show();

    expect(screen.getByRole('heading', { name: 'Savings' })).toBeTruthy();
    const figure = row('eco-savings');
    expect(figure.querySelector('[data-savings="today"]')?.textContent).toBe('2.82 EUR');
    expect(figure.textContent).toContain('against the controlled loads being off');
    expect(figure.querySelector('[data-savings="since"]')?.textContent).toBe('since 10/26 10:00');
    expect(row('eco-periods').textContent).toBe('3');
  });

  it('is an estimate, never a bill or a guarantee, and not a protective device', async () => {
    await show();

    const text = savings()!.textContent!;
    expect(text).toContain('not a bill, not a settlement and not a guarantee');
    expect(text).not.toMatch(/\bprotect|\bguarantees\b|\binvoice\b/i);
  });

  it('shows every device’s split and a negative total as it is, with a warning, not hidden', async () => {
    await show();

    expect(row('eco-savings-heater_l1').textContent).toContain('3.23 EUR');
    const car = row('eco-savings-ev_charger');
    expect(car.textContent).toContain('-0.41 EUR');
    expect(car.textContent).toContain('1.50 kWh; value 0.12 EUR, extra cost 0.53 EUR');
    expect(car.getAttribute('data-negative')).toBe('true');
    expect(car.textContent).toContain('running it cost more than it was worth');
    expect(row('eco-savings-heater_l1').getAttribute('data-negative')).toBeNull();
  });

  it('shows the running period apart, and says it is not counted above', async () => {
    await show();

    expect(row('eco-running').textContent).toBe('13:00–14:00: 0.47 EUR so far, not yet counted above');
    // The running period is in no total: today still sums three counted hours.
    expect(row('eco-savings').querySelector('[data-savings="today"]')?.textContent).toBe('2.82 EUR');
  });

  it('flags a total that includes periods priced at the fallback', async () => {
    await show('en', variant(economicsSample, (p) => Object.assign(p.savings.today, { fallback: true, fallbackPeriods: 2 })));

    const flag = row('eco-savings').querySelector('[data-savings="fallback"]')!;
    expect(flag.textContent).toContain('fallback');
    expect(flag.textContent).toContain('at the fallback price');
    expect(flag.textContent).toContain(': 2.');
  });

  it('shows no flag when no counted period used a fallback price', async () => {
    await show();

    expect(row('eco-savings').querySelector('[data-savings="fallback"]')).toBeNull();
  });

  it('says nothing is counted yet, and shows no figure, while no complete priced period exists', async () => {
    await show('en', variant(economicsSample, (p) => Object.assign(p.savings, { since: null, running: null, today: { ...p.savings.today, periods: 0, savings: 0, valueGained: 0, extraCost: 0, devices: [] } })));

    expect(row('eco-savings').querySelector('[data-savings="today"]')?.textContent).toBe('—');
    expect(row('eco-savings').textContent).toContain('No complete priced period yet');
  });

  it('says how many periods were left out for want of prices or observation', async () => {
    await show('en', variant(economicsSample, (p) => Object.assign(p.savings.today, { unpricedPeriods: 2, incompletePeriods: 1 })));

    expect(savings()!.textContent).toContain('Not counted: 2 without prices, 1 not observed throughout.');
  });

  it('says it in Polish', async () => {
    await show('pl');

    expect(screen.getByRole('heading', { name: 'Oszczędność' })).toBeTruthy();
    expect(row('eco-savings').textContent).toContain('względem wyłączonych sterowanych odbiorników');
    expect(row('eco-savings').querySelector('[data-savings="today"]')?.textContent).toBe('2,82 EUR');
    expect(row('eco-savings-ev_charger').textContent).toContain('-0,41 EUR');
    expect(savings()!.textContent).toContain('to nie rachunek, nie rozliczenie i nie gwarancja');
  });

  it('shows no card while the section has no savings, and leaves the rest of the dashboard alone', async () => {
    await show('en', variant(economicsSample, (p) => (p.savings = null)));

    expect(savings()).toBeNull();
    expect(tariff()).not.toBeNull();
    expect(screen.getByRole('heading', { name: 'Allocation' })).toBeTruthy();
  });
});
