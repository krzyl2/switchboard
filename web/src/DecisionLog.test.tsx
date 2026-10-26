import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import { elapsedOfDay, selfConsumption } from './DecisionLog';
import decisionsSample from './api/decisions.sample.json';
import economicsSample from './api/economics.sample.json';
import signalsSample from './api/signals.sample.json';
import statisticsSample from './api/statistics.sample.json';
import { parseStatistics } from './api/statistics';
import { formatAge } from './format';
import { localDay } from './localTime';
import { advance, respond } from './screens.test-support';

let fetchMock: Mock<typeof fetch>;

const STATISTICS_AT = new Date(statisticsSample.at);

function copy<T>(sample: T, change?: (p: any) => void): T {
  const payload = structuredClone(sample);
  change?.(payload);
  return payload;
}

/** The query of a request URL, without an absolute base (the page only ever asks relative URLs). */
function query(input: unknown): URLSearchParams {
  return new URLSearchParams(String(input).split('?')[1] ?? '');
}

/**
 * Statistics the way the add-on answers them: a range with its sums only when from/to were asked,
 * echoing them; signals aligned with the decisions' day, so "today" is 2026-10-24 on the add-on clock.
 */
/** A history page (GET api/decisions/history) made of the decisions sample. */
function history(page: typeof decisionsSample, extra: Record<string, unknown> = {}) {
  return { ...page, retainedFrom: '2026-10-22', retentionDays: 30, partial: false, ...extra };
}

function serve({
  economics = () => respond({}, 404),
  decisions = () => respond(history(copy(decisionsSample))),
  signals = () => respond(copy(signalsSample, (p) => Object.assign(p, { generatedAt: '2026-10-24T10:00:05.000+00:00', snapshotAt: '2026-10-24T09:59:51.449+00:00' }))),
  statistics = (asked: URLSearchParams) =>
    respond(
      copy(statisticsSample, (p) => {
        if (asked.has('from')) Object.assign(p.range, { from: asked.get('from'), to: asked.get('to') });
        else p.range = null;
      }),
    ),
}: { economics?: () => Promise<Response>; decisions?: (asked: URLSearchParams) => Promise<Response>; signals?: () => Promise<Response>; statistics?: (asked: URLSearchParams) => Promise<Response> } = {}) {
  fetchMock.mockImplementation((input) => {
    const url = String(input).split('?')[0];
    if (url === 'api/decisions/history') return decisions(query(input));
    if (url === 'api/signals') return signals();
    if (url === 'api/economics') return economics();
    if (url === 'api/statistics') return statistics(query(input));
    return respond({}, 404);
  });
}

async function open(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/log';
  render(<App lang={lang} />);
  await advance();
  await advance();
}

function card(heading: string): HTMLElement {
  return screen.getByRole('heading', { name: heading }).closest('section') as HTMLElement;
}

function decisionRow(id: string): HTMLTableRowElement {
  const row = document.querySelector(`tr[data-decision="${id}"]`);
  if (!(row instanceof HTMLTableRowElement)) throw new Error(`No row for decision ${id}.`);
  return row;
}

function metric(name: string): HTMLElement {
  const value = document.querySelector(`dd[data-metric="${name}"]`);
  if (!(value instanceof HTMLElement)) throw new Error(`No summary value ${name}.`);
  return value;
}

function decisionRequests(): string[] {
  return fetchMock.mock.calls.map(([input]) => String(input)).filter((url) => url.startsWith('api/decisions'));
}

async function choose(select: HTMLElement, value: string) {
  await act(async () => {
    fireEvent.change(select, { target: { value } });
  });
  await advance();
}

async function click(button: HTMLElement) {
  await act(async () => {
    fireEvent.click(button);
  });
  await advance();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-24T10:00:06Z'));
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('decision log', () => {
  // The local day is the add-on's (data time in development replay), so the summary is asked for
  // with the UTC instants of that day's local midnights, computed in the browser.
  it('asks for the decisions, the signals and the statistics of the local day', async () => {
    serve();
    await open();

    // Besides the shell's mode banner (api/mode, on every page: Shell.test). api/economics is the daily summary's savings.
    const urls = fetchMock.mock.calls.map(([input]) => String(input).split('?')[0]).filter((url) => url !== 'api/mode');
    expect(new Set(urls)).toEqual(new Set(['api/decisions/history', 'api/economics', 'api/signals', 'api/statistics']));
    const day = localDay(STATISTICS_AT);
    const ranged = fetchMock.mock.calls.map(([input]) => query(input)).find((q) => q.has('from'))!;
    expect([ranged.get('from'), ranged.get('to')]).toEqual([day.from.toISOString(), day.to.toISOString()]);
  });

  // MODE-03 on screen: time, device by its name, change and the reason as a sentence.
  it('lists the decisions with time, device name, change and reason sentence', async () => {
    serve();
    await open();

    expect(document.querySelectorAll('tr[data-decision]')).toHaveLength(decisionsSample.items.length);
    const shed = decisionRow('5f3c9a1b2d7e-000000042');
    expect(shed.cells[0]?.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T10:00:00.004Z');
    expect(shed.cells[1]?.textContent).toBe('Grzałka CWU L1heater_l1L1');
    expect(within(shed.cells[1] as HTMLElement).getByText('heater_l1').classList.contains('mono')).toBe(true);
    expect(shed.cells[2]?.textContent).toBe('Switch offNot commanded: Shadow mode: computed and logged, nothing switched.limits');
    expect(shed.cells[3]?.textContent).toContain('Phase current above a tier of the breaker profile.');
    expect(decisionRow('5f3c9a1b2d7e-000000039').cells[1]?.textContent).toBe('L2');
    expect(decisionRow('5f3c9a1b2d7e-000000030').cells[2]?.textContent).toContain('foreign load');
    expect(decisionRow('5f3c9a1b2d7e-000000030').cells[3]?.textContent).toContain('Load appeared on the phase without a command of ours.');
    expect(decisionRow('5f3c9a1b2d7e-000000001').cells[1]?.textContent).toBe('whole installation');
  });

  it('dates a decision from another day than the add-on’s today', async () => {
    serve();
    await open();

    expect(decisionRow('5f3c9a1b2d7e-000000042').cells[0]?.querySelector('.sub')).toBeNull();
    expect(decisionRow('5f3c9a1b2d7e-000000001').cells[0]?.querySelector('.sub')?.textContent).toBe(
      new Intl.DateTimeFormat('en', { day: '2-digit', month: '2-digit' }).format(new Date('2026-10-22T12:00:00Z')),
    );
  });

  // A filter is a request to the add-on (device, kind from the closed list), never a client-side
  // guess over one page; a new filter starts at the newest page again.
  it('filters by device and by kind through the API', async () => {
    serve();
    await open();

    const decisions = card('Decision log');
    const device = within(decisions).getByRole('combobox', { name: 'Device' }) as HTMLSelectElement;
    const kind = within(decisions).getByRole('combobox', { name: 'Kind' }) as HTMLSelectElement;
    expect([...device.options].map((o) => o.textContent)).toEqual(['all devices', 'Grzałka CWU L1', 'Grzałka CWU L2', 'Grzałka CWU L3', 'Ładowarka go-e']);
    expect([...kind.options].map((o) => o.value)).toEqual(['', 'protection', 'dispatch', 'override', 'verification', 'foreign_load', 'system']);

    await choose(device, 'ev_charger');
    expect(decisionRequests().at(-1)).toBe('api/decisions/history?device=ev_charger');
    await choose(kind, 'verification');
    expect(decisionRequests().at(-1)).toBe('api/decisions/history?device=ev_charger&kind=verification');
    await choose(device, '');
    expect(decisionRequests().at(-1)).toBe('api/decisions/history?kind=verification');
  });

  // MODE-04: older pages through the add-on's cursor over the retained files, and an honest end of
  // the retention instead of an empty page that looks like "nothing happened".
  it('pages to older decisions with the cursor and says where the retained history ends', async () => {
    serve({ decisions: (asked) => respond(history(copy(decisionsSample, (p) => asked.has('cursor') && (p.next = null)))) });
    await open();

    const decisions = card('Decision log');
    const older = within(decisions).getByRole('button', { name: 'Older' }) as HTMLButtonElement;
    expect((within(decisions).getByRole('button', { name: 'Newer' }) as HTMLButtonElement).disabled).toBe(true);
    await click(older);

    expect(decisionRequests().at(-1)).toBe('api/decisions/history?cursor=MjAyNi0xMC0yNHwy');
    expect(decisions.textContent).toContain('page 2');
    expect((within(decisions).getByRole('button', { name: 'Older' }) as HTMLButtonElement).disabled).toBe(true);
    expect(decisions.textContent).toContain('No older decisions: the decision log keeps 30 days, and the oldest kept day is 2026-10-22 (UTC).');

    // A new filter starts again at the newest page.
    await choose(within(decisions).getByRole('combobox', { name: 'Kind' }), 'dispatch');
    expect(decisionRequests().at(-1)).toBe('api/decisions/history?kind=dispatch');
    expect(decisions.textContent).toContain('page 1');

    await click(within(decisions).getByRole('button', { name: 'Older' }));
    await click(within(decisions).getByRole('button', { name: 'Newest' }));
    expect(decisionRequests().at(-1)).toBe('api/decisions/history?kind=dispatch');
  });

  it('warns when the decision log cannot be written', async () => {
    serve({ decisions: () => respond(history(copy(decisionsSample, (p) => (p.logWritable = false)))) });
    await open();

    expect(within(card('Decision log')).getByRole('status').textContent).toContain('The decision log cannot be written.');
  });

  it('says so when no decision matches a filter', async () => {
    serve({ decisions: (asked) => respond(history(copy(decisionsSample, (p) => asked.has('kind') && Object.assign(p, { items: [], next: null })))) });
    await open();

    await choose(within(card('Decision log')).getByRole('combobox', { name: 'Kind' }), 'override');
    expect(card('Decision log').textContent).toContain('No decision matches.');
  });

  // A frozen log next to a failure would read as a quiet installation: the rows are withdrawn.
  it('withdraws the decisions when the request fails', async () => {
    serve({ decisions: () => respond({}, 500) });
    await open();

    expect(within(card('Decision log')).getByRole('alert').textContent).toContain('Unavailable');
    expect(document.querySelector('tr[data-decision]')).toBeNull();
  });
});

// SIG-05: every signal with its entity (monospace), its age and its state as a labelled dot.
describe('signal health', () => {
  it('lists each signal with its entity, age, max age and state', async () => {
    serve();
    await open();

    const health = card('Signal health');
    const rows = [...health.querySelectorAll('tbody tr')];
    expect(rows).toHaveLength(signalsSample.signals.length);
    const power = health.querySelector('tr[data-signal="grid_power_l1"]') as HTMLTableRowElement;
    expect(within(power).getByText('sensor.meter_power_a').classList.contains('mono')).toBe(true);
    expect(power.cells[1]?.textContent).toBe('13 smax 90 s');
    expect(power.querySelector('.state')?.getAttribute('data-tone')).toBe('ok');
    expect(power.cells[2]?.textContent).toBe('fresh');
    const stale = health.querySelector('tr[data-signal="grid_current_l2"]') as HTMLElement;
    expect([stale.querySelector('.state')?.textContent, stale.querySelector('.state')?.getAttribute('data-tone')]).toEqual(['stale', 'crit']);
    // How old the snapshot was when the add-on answered: a frozen store shows here.
    expect(health.querySelector('.updated')?.textContent).toBe('evaluated 13 s ago');
  });

  it('names an unmapped role slot by its role, with no entity', async () => {
    serve();
    await open();

    const unmapped = [...card('Signal health').querySelectorAll('tbody tr')].find((r) => r.textContent?.includes('unmapped')) as HTMLTableRowElement;
    expect(unmapped.cells[0]?.textContent).toMatch(/—$/);
    expect(unmapped.querySelector('.state')?.getAttribute('data-tone')).toBe('muted');
  });
});

// docs/05: "Breaker trips: 0 is the product's primary success metric and belongs on this screen
// permanently", but Switchboard claims only what it can measure; savings need a tariff (v2).
describe('daily summary', () => {
  it('counts breaker trips from phase-voltage losses and says how long the voltages were measured', async () => {
    serve();
    await open();

    const elapsed = formatAge(elapsedOfDay(STATISTICS_AT, localDay(STATISTICS_AT)), 'en');
    const trips = metric('breaker-trips');
    expect(trips.querySelector('.log-metric')?.textContent).toBe('0');
    expect(trips.textContent).toContain(`seen during 10 min of ${elapsed} of voltage measurement`);
    expect(trips.textContent).toContain('A trip that also stops Home Assistant or the meter cannot be seen.');
    expect(trips.textContent).toContain('breaker trips 0, supply outages 0');
    expect(metric('supply-outages').textContent).toContain('not counted as a breaker trip');
    // A voltage loss is evidence, not proof: one or two phases may be a grid phase, every phase may
    // be a multi-pole breaker. The copy must not claim a cause Switchboard cannot measure.
    expect(trips.textContent).toContain('possible trips:');
    expect(trips.textContent).toContain('a loss of one grid phase looks the same');
    expect(metric('supply-outages').textContent).toContain('a grid outage or a trip of a multi-pole breaker, which cannot be told apart');
    expect(card('Daily summary').querySelector('.cardhead .sub')?.textContent).toContain(`data observed for 10 min of ${elapsed} so far`);
  });

  it('says breaker trips are not measured when no phase voltage is mapped, never 0', async () => {
    serve({
      statistics: (asked) =>
        respond(
          copy(statisticsSample, (p) => {
            p.voltageMeasured = false;
            if (asked.has('from')) Object.assign(p.range, { from: asked.get('from'), to: asked.get('to') });
            else p.range = null;
          }),
        ),
    });
    await open();

    expect(metric('breaker-trips').textContent).toBe('not measured — no phase voltage signal mapped');
    expect(metric('breaker-trips').querySelector('.log-metric')).toBeNull();
    expect(metric('supply-outages').textContent).toBe('not measured — no phase voltage signal mapped');
  });

  // The metric stays on screen while the statistics cannot be read, without a stale number.
  it('keeps the breaker trips row without a number while the statistics are unavailable', async () => {
    serve({ statistics: () => respond({}, 500) });
    await open();

    const summary = card('Daily summary');
    expect(within(summary).getByText('Breaker trips', { selector: 'dt' })).toBeTruthy();
    expect(metric('breaker-trips').textContent).toBe('—');
    expect(within(summary).getByRole('alert').textContent).toContain('Unavailable');
    expect(metric('savings').textContent).toBe('not available — savings need a tariff, which arrives in v2' + 'Add an economics: section to switchboard.yaml to enable them.');
  });

  it('shows energy per device, self-consumption, sheds and the value delivered from the day’s sums', async () => {
    serve();
    await open();

    // 0.971 kWh produced, 0.082 kWh exported: 92 % used on site.
    expect(metric('self-consumption').textContent).toBe('92% (0.89 kWh of 0.97 kWh)');
    expect(metric('energy-ev_charger').textContent).toBe('0.61 kWhmeasured 0.61 kWh');
    expect(metric('energy-heater_l1').textContent).toBe('0.00 kWh');
    expect(within(card('Daily summary')).getByText('Energy: Ładowarka go-e', { selector: 'dt' })).toBeTruthy();
    expect(metric('sheds').textContent).toBe('0would-sheds recorded in shadow mode, nothing switched: 1');
    expect(metric('value').textContent).toBe('1.25at each device’s value_per_kwh, without a currency in this version');
    expect(metric('savings').textContent).toBe('not available — savings need a tariff, which arrives in v2' + 'Add an economics: section to switchboard.yaml to enable them.');
  });

  it('splits a device’s energy into measured and rated estimates, and names unknown time', async () => {
    serve({
      statistics: (asked) =>
        respond(
          copy(statisticsSample, (p) => {
            Object.assign(p.range, { from: asked.get('from') ?? p.range.from, to: asked.get('to') ?? p.range.to });
            if (!asked.has('from')) p.range = null;
            const heater = p.range?.totals.devices.find((d: any) => d.id === 'heater_l1');
            if (heater) Object.assign(heater, { modelKwh: 1.7, unknownS: 3600 });
          }),
        ),
    });
    await open();

    expect(metric('energy-heater_l1').textContent).toBe('1.70 kWhestimated from the rating 1.70 kWhpower unknown for 60 min');
  });

  it('reports a statistics file that was set aside or cannot be written', async () => {
    serve({
      statistics: (asked) =>
        respond(
          copy(statisticsSample, (p) => {
            p.stateFile = { status: 'reset_corrupt', warning: 'bucket misaligned', writeError: 'No space left on device' };
            if (asked.has('from')) Object.assign(p.range, { from: asked.get('from'), to: asked.get('to') });
            else p.range = null;
          }),
        ),
    });
    await open();

    const summary = card('Daily summary');
    expect(summary.textContent).toContain('statistics.json could not be read and was set aside as statistics.json.corrupt');
    expect(summary.textContent).toContain('bucket misaligned');
    expect(summary.textContent).toContain('statistics.json cannot be written: No space left on device');
  });
});

// The day's coverage is measured from local midnight on the add-on's clock, never from the buckets
// that exist (downtime makes no buckets), and in real time on 23- and 25-hour days.
describe('local day arithmetic', () => {
  it('a 25-hour day (2026-10-25, Warsaw) has run 24 h at 23:00 local time', () => {
    const at = new Date('2026-10-25T22:00:00Z');
    expect(elapsedOfDay(at, localDay(at, 'Europe/Warsaw')) / 3600).toBe(24);
  });

  it('a 23-hour day (2026-03-29, Warsaw) has run 22 h at 23:00 local time', () => {
    const at = new Date('2026-03-29T21:00:00Z');
    expect(elapsedOfDay(at, localDay(at, 'Europe/Warsaw')) / 3600).toBe(22);
  });

  it('never counts past the end of the day', () => {
    const day = localDay(new Date('2026-10-25T12:00:00Z'), 'Europe/Warsaw');
    expect(elapsedOfDay(new Date('2026-10-26T05:00:00Z'), day) / 3600).toBe(25);
  });

  it('self-consumption is the produced share not exported, and absent without production', () => {
    const totals = parseStatistics(statisticsSample).range!.totals;
    expect(selfConsumption(totals)).toBeCloseTo((totals.generationKwh - totals.pv.exportKwh) / totals.generationKwh, 9);
    expect(selfConsumption({ ...totals, generationKwh: 0 })).toBeNull();
  });
});

describe('decision log in Polish', () => {
  it('renders the table, the panels and the honest metrics with the mockup vocabulary', async () => {
    serve();
    await open('pl');

    expect(screen.getByRole('heading', { name: 'Dziennik decyzji' })).toBeTruthy();
    expect([...card('Dziennik decyzji').querySelectorAll('th')].map((th) => th.textContent)).toEqual(['Czas', 'Urządzenie lub faza', 'Zmiana', 'Powód']);
    expect(decisionRow('5f3c9a1b2d7e-000000042').cells[3]?.textContent).toContain('Prąd fazy powyżej progu z profilu bezpiecznika.');
    expect([...card('Stan sygnałów').querySelectorAll('th')].map((th) => th.textContent)).toEqual(['Sygnał', 'Wiek', 'Stan']);
    const summary = card('Podsumowanie dnia');
    expect(within(summary).getByText('Zadziałania bezpiecznika', { selector: 'dt' })).toBeTruthy();
    expect(within(summary).getByText('Autokonsumpcja PV', { selector: 'dt' })).toBeTruthy();
    expect(metric('self-consumption').textContent).toBe('92% (0,89 kWh z 0,97 kWh)');
    expect(metric('savings').textContent).toContain('niedostępne');
    expect(metric('breaker-trips').textContent).toContain('możliwe zadziałania:');
    expect(metric('supply-outages').textContent).toContain('awaria sieci albo zadziałanie bezpiecznika wielobiegunowego, których nie da się odróżnić');
    expect(within(screen.getByRole('navigation')).getByText('Dziennik decyzji').getAttribute('aria-current')).toBe('page');
  });
});

// ECO-12: the daily summary's savings replace "needs a tariff" only when an economics section yields
// them; otherwise the old words stay, with the hint to add the section.
describe('daily summary: savings (ECO-12)', () => {
  it('keeps the old text and adds the hint while the add-on has no economics section', async () => {
    serve({ economics: () => respond(copy(economicsSample, (p) => Object.assign(p, { configured: false, currency: null, tariff: null, settlement: null, devices: [], savings: null }))) });
    await open();

    expect(metric('savings').textContent).toBe('not available — savings need a tariff, which arrives in v2Add an economics: section to switchboard.yaml to enable them.');
  });

  it('keeps the old text when the economics answer cannot be read', async () => {
    serve({ economics: () => respond({}, 500) });
    await open();

    expect(metric('savings').textContent).toContain('not available — savings need a tariff');
  });

  it('shows today’s savings with the currency label of the data, what it is measured against and since when', async () => {
    serve({ economics: () => respond(copy(economicsSample)) });
    await open();

    const savings = metric('savings');
    expect(savings.querySelector('[data-savings="today"]')?.textContent).toBe('2.82 EUR');
    expect(savings.textContent).toContain('against the controlled loads being off');
    expect(savings.textContent).toContain('since 10/26 10:00');
    expect(savings.textContent).not.toContain('not available');
    expect(savings.textContent).not.toMatch(/zł|PLN|[€$£]/);
  });

  it('puts the value delivered in the same currency label once the savings are real', async () => {
    serve({ economics: () => respond(copy(economicsSample, (p) => Object.assign(p, { currency: 'kr', savings: { ...p.savings, currency: 'kr' } }))) });
    await open();

    expect(metric('value').textContent).toBe('1.25 kr' + 'at each device’s value per kWh at the time, in the currency label of the configuration; the savings below take the settlement into account');
    expect(metric('savings').textContent).toContain('2.82 kr');
  });

  it('flags a total counted partly at the fallback price, and says it in Polish', async () => {
    serve({ economics: () => respond(copy(economicsSample, (p) => Object.assign(p.savings.today, { fallback: true, fallbackPeriods: 1 }))) });
    await open('pl');

    expect(metric('savings').querySelector('[data-savings="fallback"]')?.textContent).toContain('cenie zapasowej');
    expect(metric('savings').querySelector('[data-savings="today"]')?.textContent).toBe('2,82 EUR');
    expect(metric('savings').textContent).toContain('względem wyłączonych sterowanych odbiorników');
  });
});
