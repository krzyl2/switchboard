import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/preact';
import devices from './api/devices.sample.json';
import guard from './api/guard.sample.json';
import { parsePower } from './api/power';
import power from './api/power.sample.json';
import statistics from './api/statistics.sample.json';
import { POWER_REFRESH_INTERVAL_MS } from './config';
import { layoutFlows, MIN_STROKE_PX, STOPPED_STROKE_PX, strokeWidth } from './flowLayout';
import { Flows } from './Flows';
import stylesheet from './flows.styles.css?raw';
import { localClock } from './localTime';
import { advance, serveSamples } from './screens.test-support';

let fetchMock: Mock<typeof fetch>;

type Json = Record<string, any>;
const copy = (sample: unknown): Json => structuredClone(sample) as Json;

function serve(overrides: Partial<Record<'power' | 'guard' | 'devices' | 'statistics', unknown>> = {}) {
  serveSamples(fetchMock, {
    'api/power': overrides.power ?? power,
    'api/guard': overrides.guard ?? guard,
    'api/devices': overrides.devices ?? devices,
    'api/statistics': overrides.statistics ?? statistics,
  });
}

async function renderFlows(lang: 'en' | 'pl' = 'en') {
  render(<Flows lang={lang} />);
  // The power picture first; the guard, devices and statistics follow once it is on screen.
  await advance();
  await advance();
}

const flow = (id: string) => document.querySelector(`[data-flow="${id}"]`) as SVGPathElement;
const node = (selector: string) => document.querySelector(`.flowsvg ${selector}`) as SVGGElement;
const value = (g: Element) => g.querySelector('.nval')?.textContent;
const sub = (g: Element) => [...g.querySelectorAll('.nsub')].map((e) => e.textContent).join(' ');
const rowIds = (phase: string) =>
  [...document.querySelectorAll(`.lane[data-phase="${phase}"] .node`)].map((g) => g.getAttribute('data-device') ?? g.getAttribute('data-kind'));

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-22T09:21:03Z'));
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  window.location.hash = '#/flows';
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('flows: what the page asks for', () => {
  // Every other figure on the page belongs to a power picture: without one nothing is drawn, so
  // nothing else is requested either.
  it('asks only for the power picture until one arrives', async () => {
    fetchMock.mockImplementation(() => new Promise<Response>(() => undefined));
    render(<Flows lang="pl" />);
    await advance();

    expect(screen.getByRole('heading', { name: 'Przepływy' })).toBeTruthy();
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual(['api/power']);
  });

  // Only the last hour is shown, so the statistics request names the current hour of the add-on's
  // clock (data time in replay): the answer never carries a day of buckets every 30 s.
  it('then asks for the guard, the devices and the current hour of statistics, and shows the age of the picture', async () => {
    serve();
    await renderFlows();

    expect(screen.getByText('evaluated 2 s ago')).toBeTruthy();
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      'api/power',
      'api/guard',
      'api/devices',
      'api/statistics?from=2026-10-22T09%3A00%3A00.000Z&to=2026-10-22T10%3A00%3A00.000Z',
    ]);
  });

  // A frozen schematic next to a failed poll would read as a calm installation.
  it('withdraws the drawing and the tables when the power poll fails, saying when the last update was', async () => {
    serve();
    await renderFlows();
    expect(document.querySelector('.flowsvg')).not.toBeNull();

    fetchMock.mockImplementation(() => Promise.reject(new TypeError('Failed to fetch')));
    await advance(POWER_REFRESH_INTERVAL_MS);

    expect(document.querySelector('.flowsvg')).toBeNull();
    expect(document.querySelector('table')).toBeNull();
    expect(screen.getByRole('alert').textContent).toContain('Unavailable — last update');
  });
});

describe('flows: the schematic (UI-07)', () => {
  it('draws the sources, the main meter, the board, one lane per phase and the devices of each phase', async () => {
    serve();
    await renderFlows();

    expect([...document.querySelectorAll('.flowsvg .source')].map((g) => g.getAttribute('data-kind'))).toEqual(['pv', 'grid']);
    expect(value(node('.source[data-kind="pv"]'))).toBe('5.68 kW');
    expect(value(node('.source[data-kind="grid"]'))).toBe('1.50 kW');
    expect(sub(node('.source[data-kind="grid"]'))).toBe('import · reported 11 s ago');
    expect(screen.getByText('main meter')).toBeTruthy();
    expect(screen.getByText('distribution board')).toBeTruthy();
    expect([...document.querySelectorAll('.flowsvg .lane')].map((g) => g.getAttribute('data-phase'))).toEqual(['L1', 'L2', 'L3']);
    // heater_l1 and the charger are on L1 (in configuration order), then the foreign step and the house's own load.
    expect(rowIds('L1')).toEqual(['heater_l1', 'ev_charger', 'foreign', 'base']);
    expect(rowIds('L2')).toEqual(['heater_l2', 'base']);
    expect(rowIds('L3')).toEqual(['heater_l3', 'base']);
    const charger = node('[data-device="ev_charger"]');
    expect(charger.querySelector('.nttl')?.textContent).toBe('Ładowarka go-e');
    expect(value(charger)).toBe('3.66 kW');
    expect(sub(charger)).toBe('measured 4 s ago');
    expect(sub(node('.lane[data-phase="L1"] .node[data-kind="foreign"]'))).toBe('detected 61 s ago · above the house baseline of 0.79 kW');
    expect(node('.busbar[data-phase="L1"]').textContent).toContain('19.1 A import');
    expect(node('.busbar[data-phase="L2"]').textContent).toContain('6.5 A export');
    expect(node('.busbar[data-phase="L1"]').textContent).toContain('breaker 25.0 A');
  });

  // Nothing is drawn twice or left out: what the board feeds a phase is exactly what its rows draw.
  it('the rows of each lane add up to what the board feeds that phase', () => {
    const layout = layoutFlows(parsePower(power));
    for (const lane of layout.lanes) {
      const rows = lane.rows.reduce((sum, row) => sum + row.w!, 0);
      expect(rows).toBeCloseTo(lane.feedW!, 6);
    }
    expect(layout.lanes.map((l) => l.feedW!)).toEqual(parsePower(power).phases.map((p) => p.balance.load.w));
  });

  it('one scale for the whole drawing: twice the power is twice the width', async () => {
    const capacity = 17_250;
    expect(strokeWidth(4000, capacity)).toBeCloseTo(2 * strokeWidth(2000, capacity), 9);
    // Every phase at its rating at once is the widest path.
    expect(strokeWidth(capacity, capacity)).toBe(24);
    expect(strokeWidth(1, capacity)).toBe(MIN_STROKE_PX);

    serve();
    await renderFlows();
    const width = (id: string) => Number(flow(id).getAttribute('stroke-width'));
    // PV in total and the charger on L1 are drawn on the same scale.
    expect(width('pv') / width('L1:ev_charger')).toBeCloseTo(5683 / 3662, 6);
  });

  it('keeps a stopped device on screen: a dashed 0 W box and a thin dashed link, never removed', async () => {
    serve();
    await renderFlows();

    const heater = node('[data-device="heater_l1"]');
    expect(heater.getAttribute('data-state')).toBe('stopped');
    expect(value(heater)).toBe('0.00 kW');
    expect(sub(heater)).toContain('reported off');
    const link = flow('L1:heater_l1');
    expect([link.getAttribute('data-state'), link.getAttribute('stroke-width'), link.classList.contains('anim')]).toEqual([
      'stopped',
      String(STOPPED_STROKE_PX),
      false,
    ]);
  });

  // The idea the product is built on: each phase is limited by its own breaker, and load never moves
  // between phases. Separate lanes with gaps between them, each fed by its own path from the board.
  it('draws three busbars, and no path joins two of them', () => {
    const layout = layoutFlows(parsePower(power));
    const frames = layout.lanes.map((l) => l.frame);
    frames.slice(1).forEach((frame, i) => expect(frame.y).toBeGreaterThan(frames[i]!.y + frames[i]!.height));

    const points = (d: string) => [...d.matchAll(/(-?[\d.]+),(-?[\d.]+)/g)].map((m) => ({ x: Number(m[1]), y: Number(m[2]) }));
    const inside = (y: number, phase: string) => {
      const frame = layout.lanes.find((l) => l.phase.phase === phase)!.frame;
      return y >= frame.y && y <= frame.y + frame.height;
    };
    const laneLinks = layout.links.filter((l) => l.lane !== null);
    expect(laneLinks.filter((l) => l.kind === 'feed').map((l) => l.lane)).toEqual(['L1', 'L2', 'L3']);
    for (const link of laneLinks) {
      const all = points(link.d);
      const [start, end] = [all[0]!, all[all.length - 1]!];
      expect(inside(end.y, link.lane!)).toBe(true);
      if (link.kind === 'feed') expect(start).toEqual({ x: layout.junction.x + 7, y: layout.junction.y });
      else expect(inside(start.y, link.lane!)).toBe(true);
    }
  });

  it('reverses the grid path on export: it runs from the board out to the grid', async () => {
    const exporting = copy(power);
    exporting.total.netImport.w = -1200;
    serve({ power: exporting });
    await renderFlows();

    const grid = flow('grid');
    const layout = layoutFlows(parsePower(exporting));
    expect(grid.getAttribute('data-outward')).toBe('true');
    expect(grid.getAttribute('d')!.startsWith(`M${layout.junction.x - 7},${layout.junction.y}`)).toBe(true);
    expect(node('.source[data-kind="grid"]').getAttribute('data-direction')).toBe('out');
    expect(screen.getByText('Sent to the grid')).toBeTruthy();
    expect(value(node('.source[data-kind="grid"]'))).toBe('1.20 kW');
  });

  it('animates only flowing paths, and nothing under prefers-reduced-motion', async () => {
    serve();
    await renderFlows();
    expect(flow('pv').classList.contains('anim')).toBe(true);
    expect(flow('L1:ev_charger').classList.contains('anim')).toBe(true);
    expect(flow('L2:heater_l2').classList.contains('anim')).toBe(false);
    cleanup();

    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(prefers-reduced-motion: reduce)', addEventListener: () => undefined, removeEventListener: () => undefined }));
    await renderFlows();
    expect(document.querySelectorAll('.flowsvg path').length).toBeGreaterThan(0);
    expect(document.querySelectorAll('.flowsvg .anim')).toHaveLength(0);
  });

  // The class is left out under reduced motion; the stylesheet stops the animation as well.
  it('keeps a stylesheet backstop that stops the animation under reduced motion', () => {
    const css = stylesheet.replace(/\s+/g, ' ');
    expect(css).toMatch(/@media \(prefers-reduced-motion: reduce\) \{ \.flowsvg \.anim \{ animation: none; \} \}/);
  });

  // A stale charger draw is the normal idle state on the live installation: unknown, dotted, with the
  // reason, and never a 0 that would look like a measured idle charger.
  it('draws an unknown device draw dotted with its reason, never as 0', async () => {
    const stale = copy(power);
    const charger = stale.devices.find((d: Json) => d.id === 'ev_charger');
    charger.phases[0] = { ...charger.phases[0], powerW: null, source: 'unknown', reportedAt: null };
    charger.totalW = null;
    charger.source = 'unknown';
    const l1 = stale.phases[0].balance;
    l1.controlled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
    l1.uncontrolled = { w: null, unknown: 'device_power_unknown', reportedAt: null };
    l1.unknownDevices = ['ev_charger'];
    serve({ power: stale });
    await renderFlows();

    const box = node('[data-device="ev_charger"]');
    expect([box.getAttribute('data-state'), value(box), sub(box)]).toEqual(['unknown', 'unknown', 'draw not fresh, power unknown']);
    const house = node('.lane[data-phase="L1"] .node[data-kind="base"]');
    expect([house.getAttribute('data-state'), value(house), sub(house)]).toEqual(['unknown', 'unknown', 'a device draw is not fresh: Ładowarka go-e']);
    expect(flow('L1:house').getAttribute('data-kind')).toBe('unattributed');
    expect(flow('L1:house').getAttribute('data-state')).toBe('unknown');
    // Without the house's own load the foreign step cannot be split out of it.
    expect(rowIds('L1')).toEqual(['heater_l1', 'ev_charger', 'base']);
  });

  // The step above the baseline wanders every sample; only a foreign load the add-on detected gets
  // its own box, so rows do not come and go. Undetected, it stays in the house's own load.
  it('splits out only a detected foreign load; an undetected step stays in the house box', async () => {
    const undetected = copy(power);
    undetected.phases[2].foreignStepW = 800;
    undetected.phases[2].foreignDetectedAt = null;
    serve({ power: undetected });
    await renderFlows();

    expect(rowIds('L3')).toEqual(['heater_l3', 'base']);
    expect(value(node('.lane[data-phase="L3"] .node[data-kind="base"]'))).toBe('0.49 kW');
    expect(rowIds('L1')).toContain('foreign');
  });

  it('says the inverter is asleep and counted as 0 W instead of hiding it', async () => {
    const asleep = copy(power);
    asleep.total.generation = { w: 0, unknown: null, reportedAt: '2026-10-21T17:02:00+00:00' };
    asleep.total.generationNote = 'asleep_counted_as_zero';
    serve({ power: asleep });
    await renderFlows();

    expect(sub(node('.source[data-kind="pv"]'))).toBe('inverter asleep, counted as 0 W');
    expect(flow('pv').getAttribute('data-state')).toBe('stopped');
  });

  it('leaves out a source that is not declared, in the drawing and in the legend', async () => {
    const noPv = copy(power);
    noPv.total.generation = { w: null, unknown: 'not_declared', reportedAt: null };
    serve({ power: noPv });
    await renderFlows();

    expect([...document.querySelectorAll('.flowsvg .source')].map((g) => g.getAttribute('data-kind'))).toEqual(['grid']);
    expect(document.querySelector('.flowlegend [data-kind="pv"]')).toBeNull();
    expect(screen.getByText('No inverter is declared, so production is not measured.')).toBeTruthy();
  });
});

// UI-11: the declared devices and a foreign load are told apart everywhere, by kind and by colour.
describe('flows: controlled and foreign load never share a colour (UI-11)', () => {
  it('draws them as different kinds, styled with the two load tokens, and names both in the legend', async () => {
    serve();
    await renderFlows();

    expect(flow('L1:ev_charger').getAttribute('data-kind')).toBe('controlled');
    expect(flow('L1:foreign').getAttribute('data-kind')).toBe('foreign');
    const css = stylesheet.replace(/\s+/g, ' ');
    expect(css).toContain(".flowsvg .flow[data-kind='controlled'] { stroke: var(--load-controlled); }");
    expect(css).toContain(".flowsvg .flow[data-kind='foreign'] { stroke: var(--load-foreign); }");
    expect(css).toContain(".flowsvg .node[data-kind='controlled'] .nbox { stroke: var(--load-controlled); }");
    expect(css).toContain(".flowsvg .node[data-kind='foreign'] .nbox { stroke: var(--load-foreign); }");
    const legend = within(document.querySelector('.flowlegend') as HTMLElement);
    expect(legend.getByText('declared devices')).toBeTruthy();
    expect(legend.getByText('foreign, uncontrolled')).toBeTruthy();
  });

  // Colours come only from the theme tokens, whose controlled/foreign pair theme.test.ts checks in
  // every theme; a literal colour here would bypass that check and the dark theme.
  it('uses no literal colour, only the theme tokens', () => {
    const withoutComments = stylesheet.replace(/\/\*[\s\S]*?\*\//g, '');
    expect(withoutComments).not.toMatch(/#[0-9a-f]{3,8}\b|rgba?\(|hsla?\(/i);
  });
});

describe('flows: headroom per phase (UI-08)', () => {
  it('shows the current, the headroom to the reserve threshold and to the continuous limit, and the state; negative stays negative', async () => {
    serve();
    await renderFlows();

    const table = screen.getByRole('heading', { name: 'Headroom per phase' }).closest('section')!.querySelector('table')!;
    expect([...table.querySelectorAll('thead th')].map((th) => th.textContent)).toEqual([
      'Phase',
      'Now',
      'To the reserve threshold',
      'To the continuous limit, 28.3 A',
      'State',
    ]);
    const cells = (phase: string) => [...table.querySelectorAll(`tr[data-phase="${phase}"] td`)];
    const [now, reserve, continuous, state] = cells('L1');
    expect(now!.textContent).toBe('33.0 A3 s ago');
    expect(reserve!.textContent).toBe('-18.9 Athreshold 14.1 A');
    expect(reserve!.classList.contains('neg')).toBe(true);
    expect(continuous!.textContent).toBe('-4.8 A');
    expect(continuous!.classList.contains('neg')).toBe(true);
    expect(state!.textContent).toBe('shedding');
    // A phase without valid data shows no number at all, and why.
    expect(cells('L2').map((c) => c.textContent)).toEqual(['—', '—threshold 14.1 A', '—', 'safe statecurrent reading stale']);
    // Export: the guard counts from the signed current, so the headroom is larger than the rating.
    const l3 = cells('L3');
    expect(l3[0]!.textContent).toBe('12.4 Aexport3 s ago');
    expect(l3[1]!.classList.contains('neg')).toBe(false);
    expect(screen.getByText('Shadow mode: reducing and shedding are what protection would do; nothing is switched.')).toBeTruthy();
  });

  it('names the breaker it measures against, in Polish with the mockup vocabulary', async () => {
    serve();
    await renderFlows('pl');

    const section = screen.getByRole('heading', { name: 'Zapas na fazach' }).closest('section')!;
    expect(section.querySelector('.cardhead .sub')?.textContent).toBe('25,0 A, charakterystyka B · granica pracy ciągłej 28,3 A (1,13 × In)');
    expect([...section.querySelectorAll('thead th')].map((th) => th.textContent)).toContain('Wolne do progu');
  });
});

describe('flows: where PV went in the last hour', () => {
  it('splits the last hour of production between the devices, the house and export, and the parts sum to it', async () => {
    serve();
    await renderFlows();

    const section = screen.getByRole('heading', { name: 'Where PV went' }).closest('section')!;
    const rows = [...section.querySelectorAll('tbody tr')].map((tr) => [...tr.children].map((c) => c.textContent));
    expect(rows).toEqual([
      ['Grzałka CWU L1', '0.00 kWh', '0%'],
      ['Grzałka CWU L2', '0.00 kWh', '0%'],
      ['Grzałka CWU L3', '0.00 kWh', '0%'],
      ['Ładowarka go-e', '2.41 kWh', '45%'],
      ['House (uncontrolled)', '1.56 kWh', '29%'],
      ['Export to the grid', '1.33 kWh', '25%'],
    ]);
    expect([...section.querySelectorAll('tfoot td')].map((c) => c.textContent)).toEqual(['5.30 kWh', '100%']);
    // The add-on's split leaves nothing out: the recipients add up to the production.
    const totals = statistics.lastHour.totals;
    const parts = totals.devices.reduce((sum, d) => sum + d.pvKwh, 0) + totals.pv.houseKwh + totals.pv.storageKwh + totals.pv.exportKwh + totals.pv.unattributedKwh;
    expect(parts).toBeCloseTo(totals.generationKwh, 9);
    expect(section.querySelector('.cardhead .sub')?.textContent).toBe(
      `last hour, since ${localClock(new Date(statistics.lastHour.from), 'en')} · 60 min with a known power picture`,
    );
    expect(section.textContent).toContain('counted up to 2 s ago');
  });

  it('says so when there was no production, instead of shares of nothing', async () => {
    const dark = copy(statistics);
    dark.lastHour.totals.generationKwh = 0;
    serve({ statistics: dark });
    await renderFlows('pl');

    expect(screen.getByText('Brak produkcji PV w tym okresie.')).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Dokąd idzie energia z PV' }).closest('section')!.querySelector('table')).toBeNull();
  });
});

describe('flows: tiles', () => {
  it('shows production, the grid and the tightest phase, each with its age', async () => {
    serve();
    await renderFlows();

    const tiles = [...document.querySelectorAll('.flowstats .tile')].map((tile) => [...tile.children].map((c) => c.textContent));
    expect(tiles).toEqual([
      ['PV production', '5.68 kW', 'reported 31 s ago'],
      ['Drawn from the grid', '1.50 kW', 'reported 11 s ago'],
      ['Tightest phase', 'L1 · 19.1 A', '76% of limit reported 11 s ago'],
    ]);
  });
});
