import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import { balanceFigures, Dashboard, queueState, type QueueState } from './Dashboard';
import dashboardCss from './dashboard.css?raw';
import { parseDevices, type DeviceConfig } from './api/devices';
import devicesSample from './api/devices.sample.json';
import { parseDispatch, type DispatchDevice } from './api/dispatch';
import dispatchSample from './api/dispatch.sample.json';
import { parsePower } from './api/power';
import powerSample from './api/power.sample.json';
import statisticsSample from './api/statistics.sample.json';
import { DISPATCH_REFRESH_INTERVAL_MS, POWER_REFRESH_INTERVAL_MS } from './config';
import type { Lang } from './i18n';
import { advance, serveSamples } from './screens.test-support';

let fetchMock: Mock<typeof fetch>;

// Local times are asserted in a fixed zone, whatever zone the test machine runs in.
const ZONE = 'Europe/Warsaw';

const SAMPLES = {
  'api/power': powerSample,
  'api/dispatch': dispatchSample,
  'api/devices': devicesSample,
  'api/statistics': statisticsSample,
};

function variant<T>(sample: T, change: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change(copy);
  return copy;
}

async function show(lang: Lang = 'en', samples: Record<string, unknown> = SAMPLES): Promise<void> {
  serveSamples(fetchMock, { ...SAMPLES, ...samples });
  render(<Dashboard lang={lang} zone={ZONE} />);
  // The hourly balance asks for its statistics once the power picture has given it the hour.
  await advance();
  await advance();
}

function row(name: string): HTMLElement {
  const element = document.querySelector<HTMLElement>(`[data-row="${name}"]`);
  if (element === null) throw new Error(`No balance row ${name}.`);
  return element;
}

/** The kW figure a balance row displays; "not declared" counts as 0, as it does in the identity. */
function shownKw(name: string): number {
  const text = row(name).textContent ?? '';
  if (text.startsWith('not declared')) return 0;
  const match = /^(-?\d+\.\d{2}) kW/.exec(text);
  if (match === null) throw new Error(`Balance row ${name} shows no kW figure: "${text}".`);
  return Number(match[1]);
}

function queueRow(id: string): HTMLElement {
  const element = document.querySelector<HTMLElement>(`[data-device="${id}"]`);
  if (element === null) throw new Error(`No queue row for ${id}.`);
  return element;
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-22T09:21:03Z'));
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

describe('dashboard: phase bars (UI-08, UI-11)', () => {
  it('is the home page and draws a bar per phase with the age of the evaluation on the add-on clock', async () => {
    serveSamples(fetchMock, SAMPLES);
    render(<App lang="en" />);
    await advance();

    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeTruthy();
    expect(screen.getByText('evaluated 2 s ago')).toBeTruthy();
    const bars = document.querySelectorAll('.phasebar');
    expect([...bars].map((b) => b.getAttribute('data-phase'))).toEqual(['L1', 'L2', 'L3']);
  });

  // 05-UI-SPEC: the reserve threshold is drawn, not only stated; the breaker is a solid marker; the
  // current in A and its share of the limit stand beside the bar, with the age of the reading so a
  // frozen value is told apart from a calm one.
  it('draws the reserve threshold dashed before the breaker, and states current, share of the limit and age', async () => {
    await show();
    const l1 = document.querySelector<HTMLElement>('.phasebar[data-phase="L1"]')!;
    const reserve = l1.querySelector<HTMLElement>('.tick.reserve')!;
    const breaker = l1.querySelector<HTMLElement>('.tick.breaker')!;

    expect(parseFloat(reserve.style.left)).toBeLessThan(parseFloat(breaker.style.left));
    const value = l1.querySelector('.pval')!.textContent;
    expect(value).toContain('19.1 A');
    expect(value).toContain('76% of limit');
    expect(value).toContain('over the reserve threshold');
    // 09:21:02 (the add-on's clock) − 09:20:50.010 (the meter's report).
    expect(value).toContain('11 s ago');
    // An exporting phase says so: its magnitude alone would read as load.
    expect(document.querySelector('.phasebar[data-phase="L2"] .pval')!.textContent).toContain('export');
  });

  // In shadow mode a decided reduction stays "in flight" (it is never applied), so the guard's
  // estimate falls below the threshold while the meter still reads above it: the bar keeps saying
  // what it draws. An export above the threshold is no reserve excursion.
  it('says a phase is over the reserve threshold from the measured import, not from the estimate', async () => {
    const power = variant(powerSample, (p) => {
      p.phases[0].overReserve = false;
      p.banners = [];
      Object.assign(p.phases[1], { currentA: -16.2, direction: 'export' });
    });
    await show('en', { 'api/power': power });

    expect(document.querySelector('.phasebar[data-phase="L1"] .pval')!.textContent).toContain('over the reserve threshold');
    expect(document.querySelector('.phasebar[data-phase="L2"] .pval')!.textContent).not.toContain('over the reserve threshold');
    expect(document.querySelector('[data-banner-phase]')).toBeNull();
  });

  // UI-11: the declared devices and a foreign step are separate segments with their own legend
  // entries, so they can never be read as one load.
  it('keeps declared devices and foreign load in separate segments, both named in the legend', async () => {
    await show();

    const l1 = [...document.querySelectorAll('.phasebar[data-phase="L1"] .seg')].map((s) => s.getAttribute('data-kind'));
    expect(l1).toEqual(['base', 'controlled', 'foreign']);
    const legend = document.querySelector('.dash-legend')!;
    const swatches = [...legend.querySelectorAll('.dash-swatch')].map((s) => s.getAttribute('data-kind'));
    expect(swatches).toEqual(expect.arrayContaining(['base', 'controlled', 'foreign']));
    expect(legend.textContent).toContain('declared devices');
    expect(legend.textContent).toContain('foreign, uncontrolled');
    expect(legend.textContent).toContain('reserve threshold 14.1 A');
    expect(legend.textContent).toContain('breaker 25 A');
    expect(legend.textContent).toContain('continuous limit 28.3 A');
    // L1 is drawn scaled (PV covers part of its load), and the page says why the parts do not add up to the bar.
    expect(screen.getByText(/^L1: local production covers part of this load/)).toBeTruthy();
  });

  // The load colours live only in styles.css (theme.test.ts checks controlled ≠ foreign in every
  // theme); a colour defined here could make the two meet in one theme.
  it('defines no colour of its own in dashboard.css', () => {
    expect(dashboardCss).not.toMatch(/#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i);
  });

  it('says which device draw is missing when part of a phase is unattributed', async () => {
    const power = variant(powerSample, (p) => {
      const l1 = p.phases[0];
      l1.bar = [
        { kind: 'controlled', a: 0, w: 0 },
        { kind: 'unattributed', a: 19.12, w: null },
      ];
      l1.balance.controlled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
      l1.balance.uncontrolled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
      l1.balance.unknownDevices = ['ev_charger'];
    });
    await show('en', { 'api/power': power });

    expect(screen.getByText('L1: part of the current is unattributed — a device draw is not fresh: Ładowarka go-e.')).toBeTruthy();
  });
});

describe('dashboard: power balance', () => {
  // 05-UI-SPEC: the parts must sum, and a mismatch is a bug worth catching. The house load is the
  // add-on's residual, so the check is on what the page renders after rounding, against the rule
  // fixed in balanceFigures, with "not declared" storage counted as 0.
  it('renders parts that sum: production + grid = house + declared devices + storage', async () => {
    await show();

    const production = shownKw('production');
    const grid = shownKw('grid');
    const house = shownKw('house');
    const controlled = shownKw('controlled');
    const storage = shownKw('storage');
    expect([production, grid, controlled]).toEqual([5.68, 1.5, 3.66]);
    expect(row('storage').textContent).toContain('not declared');
    expect(Math.round((production + grid) * 100)).toBe(Math.round((house + controlled + storage) * 100));
  });

  // The mockup's "Zużycie domu" is the uncontrolled part: showing the whole load there would count
  // the declared devices twice and the rows would no longer sum.
  it('shows the uncontrolled part as house load, never the whole load', async () => {
    await show();
    const total = powerSample.total;

    expect(row('house').textContent).not.toContain(`${(total.load.w / 1000).toFixed(2)} kW`);
    expect(Math.abs(shownKw('house') - total.uncontrolled.w / 1000)).toBeLessThanOrEqual(0.02);
  });

  it('keeps the rendered identity exact on every rounding, with the house as the rounded residual', () => {
    const balance = parsePower(powerSample).total;
    for (const [generation, net, controlled] of [
      // Rounded on its own the house would read 2.01 kW next to 1.00 + 1.00 − 0.00: off by a unit.
      [1004.9, 1004.9, 1],
      [1004.9, 1004.9, 1004.9],
      [5683, 1502.9, 3662],
      [0, -1489.3, 0],
      [2345.5, 0.4, 1234.5],
    ] as const) {
      const figures = balanceFigures({
        ...balance,
        generation: { ...balance.generation, w: generation },
        netImport: { ...balance.netImport, w: net },
        controlled: { ...balance.controlled, w: controlled },
        uncontrolled: { ...balance.uncontrolled, w: generation + net - controlled },
      });
      expect(Math.round((figures.production! + figures.grid!) * 100)).toBe(Math.round((figures.house! + figures.controlled! + figures.storage!) * 100));
      expect(Math.abs(figures.house! - (generation + net - controlled) / 1000)).toBeLessThanOrEqual(0.02);
    }
  });

  // A part without a source is "unknown" with its reason, never a number: 0 W would be a claim.
  it('renders an unknown part with its reason and no figure', async () => {
    const power = variant(powerSample, (p) => {
      p.total.controlled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
      p.total.uncontrolled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
      p.total.unknownDevices = ['ev_charger'];
    });
    await show('en', { 'api/power': power });

    for (const name of ['controlled', 'house']) {
      expect(row(name).textContent).toContain('unknown — a device draw is not fresh: Ładowarka go-e');
      expect(row(name).textContent).not.toMatch(/\d kW/);
    }
    expect(balanceFigures(parsePower(power).total).house).toBeNull();
  });

  it('says production is not declared, and an inverter asleep is counted as 0 W with a note', async () => {
    const notDeclared = variant(powerSample, (p) => {
      p.total.generation = { w: null, unknown: 'not_declared', reportedAt: null };
    });
    await show('en', { 'api/power': notDeclared });
    expect(row('production').textContent).toBe('not declared');
    cleanup();

    const asleep = variant(powerSample, (p) => {
      p.total.generation = { w: 0, unknown: null, reportedAt: '2026-10-22T05:00:00+00:00' };
      p.total.generationNote = 'asleep_counted_as_zero';
    });
    await show('en', { 'api/power': asleep });
    expect(row('production').textContent).toContain('0.00 kW');
    expect(row('production').textContent).toContain('inverter asleep, counted as 0 W');
  });

  it('shows a negative house load as it came out, and says why', async () => {
    const power = variant(powerSample, (p) => {
      p.total.controlled.w = p.total.load.w + 300;
      p.total.uncontrolled.w = -300;
      p.total.modelExceedsLoad = true;
    });
    await show('en', { 'api/power': power });

    expect(row('house').textContent).toContain('-0.31 kW');
    expect(screen.getByText(/^The declared devices add up to more than the measured load/)).toBeTruthy();
  });

  it('labels the grid direction and gives every measured part its age', async () => {
    await show();

    expect(row('grid').textContent).toContain('(import)');
    // 09:21:02 − 09:20:30.004: production is the oldest input.
    expect(row('production').textContent).toContain('31 s ago');
  });

  // The hourly balance is the local hour (presentation edge) of the add-on's own clock, summed by
  // the add-on over that range; its coverage is measured against the elapsed time of the hour.
  it('asks for the local hour and shows export minus import, with its coverage', async () => {
    const statistics = variant(statisticsSample, (s) => {
      s.range.totals.gridImportKwh = 0.5;
      s.range.totals.gridExportKwh = 0.92;
    });
    await show('en', { 'api/statistics': statistics });

    const request = fetchMock.mock.calls.map(([input]) => String(input)).find((url) => url.startsWith('api/statistics?'))!;
    const query = new URLSearchParams(request.split('?')[1]);
    // 09:21:02 UTC is 11:21 in Warsaw (CEST, UTC+2): the hour began at 11:00 local, 09:00 UTC.
    expect([query.get('from'), query.get('to')]).toEqual(['2026-10-22T09:00:00.000Z', '2026-10-22T10:00:00.000Z']);
    const hour = row('hour').textContent;
    expect(hour).toContain('+0.42 kWh');
    expect(hour).toContain('since 11:00: import 0.50 kWh, export 0.92 kWh');
    // 600 s observed; 09:40:02 (the statistics answer's clock) − 09:00.
    expect(hour).toContain('observed 10 min of 40 min');
  });

  // 2026-10-25 is a 25-hour day in Warsaw: 02:00–03:00 local happens twice. Each of the two hours is
  // its own UTC hour, and both read "since 02:00".
  it.each([
    ['2026-10-25T00:30:00Z', '2026-10-25T00:00:00.000Z'],
    ['2026-10-25T01:30:00Z', '2026-10-25T01:00:00.000Z'],
  ])('on the 25-hour day, %s belongs to the hour from %s', async (at, from) => {
    const power = variant(powerSample, (p) => {
      p.at = at;
      p.evaluatedAt = at;
    });
    await show('en', { 'api/power': power });

    const request = fetchMock.mock.calls.map(([input]) => String(input)).find((url) => url.startsWith('api/statistics?'))!;
    expect(new URLSearchParams(request.split('?')[1]).get('from')).toBe(from);
    expect(row('hour').textContent).toContain('since 02:00');
  });

  it('says the hourly balance is unavailable when the statistics cannot be read', async () => {
    serveSamples(fetchMock, { 'api/power': powerSample, 'api/dispatch': dispatchSample, 'api/devices': devicesSample });
    render(<Dashboard lang="en" zone={ZONE} />);
    await advance();
    await advance();

    expect(row('hour').textContent).toBe('unavailable — the statistics could not be read');
  });
});

describe('dashboard: reserve banner', () => {
  // 05-UI-SPEC: above the queue, naming the foreign load and the planned action with its timing. In
  // shadow mode the action is what Switchboard would do, and the banner says nothing is switched.
  it('names the foreign load, the reduction that would follow and its timing, above the queue', async () => {
    await show();
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;
    const text = within(banner);

    expect(text.getByRole('heading').textContent).toBe('L1 is over its reserve threshold: 19.1 A against 14.1 A.');
    // 09:21:02 − 09:20:00.015.
    expect(text.getByText('A foreign load of 1.84 kW above the baseline appeared outside Switchboard 61 s ago.')).toBeTruthy();
    expect(text.getByText('Ładowarka go-e would be reduced from 20 A to 10 A.')).toBeTruthy();
    expect(text.getByText('Due at the next cycle boundary, 11:21:30 (in 0:28).')).toBeTruthy();
    expect(text.getByText('19.1 A is below the breaker rating 25 A, so the reduction is not immediate.')).toBeTruthy();
    expect(text.getByText('Shadow mode: nothing is switched.')).toBeTruthy();
    const queue = screen.getByRole('heading', { name: 'Allocation' });
    expect(banner.compareDocumentPosition(queue) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(document.querySelectorAll('[data-banner-phase]')).toHaveLength(1);
  });

  // In shadow mode a decided reduction relieves the guard's estimate while the meter still reads the
  // same (nothing is switched): the banner stays on the measured current and says what the decided
  // changes would leave.
  it('keeps the measured current in the title and adds what the decided changes would leave', async () => {
    await show('en', { 'api/power': variant(powerSample, (p) => (p.banners[0].effectiveCurrentA = 9.12)) });
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;

    expect(within(banner).getByRole('heading').textContent).toBe('L1 is over its reserve threshold: 19.1 A against 14.1 A.');
    expect(banner.textContent).toContain('With the changes already decided: 9.1 A.');
  });

  // A decided start counts in the estimate before it draws (live 0.4.1 night): the banner must not
  // claim the measured 7.5 A is over a 14.1 A threshold.
  it.each([
    ['en', 'L1 would be over its reserve threshold with the changes already decided: 14.5 A against 14.1 A (measured 7.5 A).'],
    ['pl', 'L1 przekroczyłaby próg zapasu po uwzględnieniu już podjętych decyzji: 14,5 A przy progu 14,1 A (zmierzono 7,5 A).'],
  ] as const)('%s: words a banner raised by the estimate alone from the estimate', async (lang, title) => {
    await show(lang, { 'api/power': variant(powerSample, (p) => Object.assign(p.banners[0], { currentA: 7.5, effectiveCurrentA: 14.5 })) });
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;

    expect(within(banner).getByRole('heading').textContent).toBe(title);
    expect(banner.textContent).not.toContain('With the changes already decided');
  });

  it('shows no banner while no phase is over its reserve threshold', async () => {
    await show('en', { 'api/power': variant(powerSample, (p) => (p.banners = [])) });

    expect(document.querySelector('[data-banner-phase]')).toBeNull();
  });

  // An exporting phase cannot be relieved by switching load off; the guard records
  // cannot_reduce_exporting, and the banner must not plan a reduction there.
  it('says there is nothing to reduce on an exporting phase', async () => {
    const power = variant(powerSample, (p) => {
      Object.assign(p.banners[0], { timing: 'cannot_reduce_exporting', dueAt: null, reductionRequested: false, foreignStepW: 0 });
    });
    await show('en', { 'api/power': power });
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;

    expect(banner.textContent).toContain('The phase is exporting: there is nothing to reduce.');
    expect(banner.textContent).not.toContain('would be reduced');
    expect(banner.textContent).toContain('No foreign load above the baseline');
  });

  it('names a switch-off, a lockout that delays it, and a phase above the breaker with the tier reaction', async () => {
    const power = variant(powerSample, (p) => {
      Object.assign(p.banners[0], {
        currentA: 27.5,
        reaction: 'reduce_next_cycle',
        planned: [
          { device: 'heater_l1', from: { relays: [true], setpointA: null, enabled: true }, to: { relays: [false], setpointA: null, enabled: true }, origin: 'guard', reason: 'guard_tier_exceeded', dueAt: '2026-10-22T09:22:00+00:00' },
        ],
      });
    });
    await show('en', { 'api/power': power });
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;

    expect(banner.textContent).toContain('Grzałka CWU L1 would be switched off. Its lockout ends at 11:22:00.');
    expect(banner.textContent).toContain('Above the breaker rating 25 A: reduce next cycle.');
  });

  // Shadow mode: no text on the banner or in the queue may say that Switchboard will act.
  it.each(['en', 'pl'] as const)('%s: no banner or queue text uses a future-indicative action', async (lang) => {
    await show(lang);
    const texts = [...document.querySelectorAll('[data-banner-phase], .dash-queue')].map((e) => e.textContent ?? '').join(' ');

    expect(texts.length).toBeGreaterThan(100);
    const future = lang === 'en' ? /\bwill\b|\bgoing to\b/i : /(?<!\p{L})(zostanie|zostaną|wyłączy|włączy|zmniejszy|zrzuci)(?!\p{L})/iu;
    expect(texts).not.toMatch(future);
  });
});

describe('dashboard: allocation queue (UI-09, UI-10)', () => {
  it('lists every declared device in dispatch start order, with its display name', async () => {
    await show();
    const rows = [...document.querySelectorAll('.dash-queue [data-device]')];

    // Start ranks in the sample: ev_charger 1, battery 2, heater_l2 3, heater_l1 4, heater_l3 5.
    expect(rows.map((r) => r.getAttribute('data-device'))).toEqual(['ev_charger', 'battery', 'heater_l2', 'heater_l1', 'heater_l3']);
    expect(within(queueRow('ev_charger')).getByRole('link', { name: 'Ładowarka go-e' }).getAttribute('href')).toBe('#/devices/ev_charger');
    // A device the configuration names nothing is shown by its id.
    expect(within(queueRow('battery')).getByRole('link').textContent).toBe('battery');
  });

  // UI-10: a device that is not running keeps its row and says what blocks it; a disappearing row
  // would read as a broken configuration.
  it('keeps stopped, deferred, unresponsive and forced devices listed with what blocks them', async () => {
    await show();
    const state = (id: string) => [queueRow(id).getAttribute('data-state'), queueRow(id).querySelector('.badge')!.textContent];
    const reason = (id: string) => queueRow(id).querySelector('.dash-reason')!.textContent;

    expect(state('battery')).toEqual(['stopped', 'off']);
    expect(reason('battery')).toBe('Increase waiting. Waiting for the headroom to hold for enough cycles in a row before adding load.');
    // Lockout countdown from the step's own time: 12:02:02.004 − 12:01:20.004.
    expect(state('heater_l1')).toEqual(['deferred', 'lockout 0:42']);
    expect(reason('heater_l1')).toBe('Minimum off time not yet elapsed (lockout). Due at 14:02:02.');
    expect(state('heater_l3')).toEqual(['unresponsive', 'not responding']);
    expect(reason('heater_l3')).toBe('The device does not respond to commands. Next try at 14:10:00.');
    expect(state('ev_charger')).toEqual(['forced_off', 'forced off (override)']);
    expect(reason('ev_charger')).toBe('Manual override (set from this page or the API) since 14:00:05.');
    expect(state('heater_l2')).toEqual(['would_start', 'would start']);
    expect(reason('heater_l2')).toBe('Headroom is available again.');
    expect(queueRow('heater_l2').querySelector('.dash-meta')!.textContent).toBe('L2 · value 0.35 per kWh · target: on · minimum on time, 5 min left');
  });

  it('puts a protective hold first, with the hold reason', async () => {
    const dispatch = variant(dispatchSample, (d) => {
      Object.assign(d.devices[1], { origin: 'guard', reason: 'reserve_threshold', intended: { relays: [false], setpointA: null, enabled: true }, matches: true, settling: 'none' });
    });
    await show('en', { 'api/dispatch': dispatch });

    expect(queueRow('heater_l2').getAttribute('data-state')).toBe('held');
    expect(queueRow('heater_l2').querySelector('.badge')!.textContent).toBe('would be held (limits)');
    expect(queueRow('heater_l2').querySelector('.dash-reason')!.textContent).toBe('Phase current above the reserve threshold.');
  });

  it('shows each power with its source: measured with its age, rated, or unknown with the reason', async () => {
    await show();
    const power = (id: string) => queueRow(id).querySelector('.dash-power')!.textContent;

    // 09:21:02 − 09:20:57.502.
    expect(power('ev_charger')).toBe('3.66 kWmeasured · 4 s ago');
    expect(power('heater_l2')).toBe('0.00 kWrated');
    expect(queueRow('ev_charger').querySelector('.dash-meta')!.textContent).toBe('L1 · value 2.03 per kWh · 9.6 A of 16 A · minimum on time, 60 s left');
    cleanup();

    await show('en', {
      'api/power': variant(powerSample, (p) => {
        Object.assign(p.devices[3], { totalW: null, source: 'unknown', phases: [{ phase: 'L1', powerW: null, source: 'unknown', reportedAt: null }] });
      }),
    });
    expect(power('ev_charger')).toBe('unknown — draw not fresh');
  });

  it('says every state is what Switchboard would aim for, and that it switches nothing', async () => {
    await show();

    expect(screen.getByText('Shadow mode: these are the states Switchboard would aim for; it switches nothing.')).toBeTruthy();
  });

  it('says so when no device is declared', async () => {
    await show('en', { 'api/dispatch': variant(dispatchSample, (d) => (d.devices = [])) });

    expect(screen.getByText('No devices declared: there is nothing to allocate.')).toBeTruthy();
  });
});

describe('queueState', () => {
  const sample = parseDispatch(dispatchSample);
  const configs = parseDevices(devicesSample).devices;
  const charger = configs.find((c) => c.id === 'ev_charger')!;
  const base = (id: string, change: Partial<DispatchDevice> = {}): DispatchDevice => ({
    ...sample.devices.find((d) => d.id === id)!,
    override: { mode: 'auto', changedAt: null, source: null },
    unresponsive: null,
    deferred: null,
    origin: null,
    ...change,
  });
  const on = { relays: [true], setpointA: null, enabled: true };
  const off = { relays: [false], setpointA: null, enabled: true };
  const at = (setpointA: number) => ({ relays: null, setpointA, enabled: true });
  const held = { origin: 'guard' as const, intended: off, matches: false };
  const unresponsive = { unresponsive: { since: new Date('2026-10-24T11:40:00Z'), retryAt: null } };
  const forced = { override: { mode: 'force_on' as const, changedAt: new Date('2026-10-24T11:30:00Z'), source: 'api' as const } };
  const deferred = { deferred: { target: on, reason: 'headroom_available' as const, refusedBy: 'lockout_min_off' as const, since: new Date('2026-10-24T12:00:50Z'), dueAt: null } };
  const wouldStart = { intended: on, reported: off, matches: false };

  // The precedence follows the effector's target resolution: a protective hold outranks an override.
  it.each<[string, Partial<DispatchDevice>, QueueState]>([
    ['a hold over everything else', { ...unresponsive, ...forced, ...deferred, ...held }, 'held'],
    ['unresponsive over an override', { ...forced, ...deferred, ...unresponsive, ...wouldStart }, 'unresponsive'],
    ['an override over a deferral', { ...deferred, ...wouldStart, ...forced }, 'forced_on'],
    ['a deferral over a would-change', { ...wouldStart, ...deferred }, 'deferred'],
    ['would start from off', wouldStart, 'would_start'],
    ['would stop', { intended: off, reported: on, matches: false }, 'would_stop'],
    ['running relay', { intended: null, reported: on, matches: true }, 'running'],
    ['stopped relay', { intended: null, reported: off, matches: true }, 'stopped'],
    ['a stale state is unknown, never off', { intended: null, reported: null, reportedFresh: false, matches: true }, 'unknown'],
  ])('%s', (_name, change, expected) => {
    expect(queueState(base('heater_l2', change), null, 230)).toBe(expected);
  });

  // A setpoint is a ceiling: only the device's own fresh draw says whether it runs (an idle car at
  // 20 A must not read as running). Below half the 6 A minimum counts as idle.
  it.each<[string, Partial<DispatchDevice>, DeviceConfig | null, QueueState]>([
    ['drawing 9.6 A', { draw: { currentA: 9.6, powerW: 2208, fresh: true } }, charger, 'running'],
    ['drawing 0.8 A while set to 20 A', { reported: at(20), draw: { currentA: 0.8, powerW: 150, fresh: true } }, charger, 'idle'],
    ['power only, converted at the phase voltage', { draw: { currentA: null, powerW: 2208, fresh: true } }, charger, 'running'],
    ['a draw that is not fresh', { draw: { currentA: 9.6, powerW: 2208, fresh: false } }, charger, 'enabled_stale'],
    ['no draw declared', { draw: null }, charger, 'enabled'],
    ['stopped through a stop actuator', { reported: { relays: null, setpointA: 6, enabled: false } }, charger, 'stopped'],
  ])('a modulating device %s', (_name, change, config, expected) => {
    expect(queueState(base('ev_charger', { intended: null, matches: true, reported: at(16), ...change }), config, 230)).toBe(expected);
  });

  it.each<[number | null, number, QueueState]>([
    [4, 4, 'charging'],
    [-4, -4, 'discharging'],
    [0.2, 0, 'idle'],
  ])('a storage device drawing %s A at a %s A setpoint', (currentA, setpointA, expected) => {
    const storage: DeviceConfig = { ...charger, id: 'battery', type: 'storage', range: { minA: -10, maxA: 10, stepA: 1, unit: 'A' } };
    const device = base('battery', { intended: null, matches: true, reported: at(setpointA), draw: { currentA, powerW: null, fresh: true } });
    expect(queueState(device, storage, 230)).toBe(setpointA === 0 ? 'stopped' : expected);
  });
});

describe('dashboard in Polish', () => {
  it('uses the mockup vocabulary with its diacritics', async () => {
    await show('pl');

    expect(screen.getByRole('heading', { name: 'Pulpit' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Bilans mocy' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Rozdział mocy' })).toBeTruthy();
    expect(screen.getByText('Zużycie domu')).toBeTruthy();
    expect(queueRow('heater_l2').querySelector('.badge')!.textContent).toBe('zostałoby włączone');
    expect(queueRow('heater_l1').querySelector('.badge')!.textContent).toBe('blokada 0:42');
    const banner = document.querySelector<HTMLElement>('[data-banner-phase="L1"]')!;
    expect(within(banner).getByRole('heading').textContent).toBe('L1 przekroczyła próg zapasu: 19,1 A przy progu 14,1 A.');
    expect(banner.textContent).toContain('Urządzenie Ładowarka go-e zostałoby zmniejszone z 20 A do 10 A.');
    expect(banner.textContent).toContain('Obserwacja: nic nie jest przełączane.');
  });
});

describe('dashboard: failed polls', () => {
  // A failed poll withdraws every value it carried (a frozen number next to a failure reads as a
  // calm system) and says when the last update was.
  it('withdraws the bars, the balance and the banner when the power poll fails', async () => {
    await show();
    fetchMock.mockImplementation((input) =>
      String(input) === 'api/power' ? Promise.reject(new TypeError('Failed to fetch')) : Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(structuredClone(dispatchSample)) } as Response),
    );
    await advance(POWER_REFRESH_INTERVAL_MS);

    expect(document.querySelectorAll('.phasebar')).toHaveLength(0);
    expect(document.querySelector('[data-row]')).toBeNull();
    expect(document.querySelector('[data-banner-phase]')).toBeNull();
    expect(screen.getAllByRole('alert').map((a) => a.textContent)).toEqual([expect.stringContaining('Unavailable — last update')]);
  });

  it('keeps the bars and withdraws only the queue when the dispatch poll fails', async () => {
    await show();
    serveSamples(fetchMock, { 'api/power': powerSample, 'api/devices': devicesSample, 'api/statistics': statisticsSample });
    await advance(DISPATCH_REFRESH_INTERVAL_MS);

    expect(document.querySelectorAll('.phasebar')).toHaveLength(3);
    expect(document.querySelector('.dash-queue')).toBeNull();
    const queue = screen.getByRole('heading', { name: 'Allocation' }).closest('section')!;
    expect(within(queue).getByRole('alert').textContent).toContain('Unavailable — last update');
  });
});
