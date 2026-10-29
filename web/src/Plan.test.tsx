import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import plan from './api/plan.sample.json';
import { advance, respond, serveSamples } from './screens.test-support';

// Screen 06 as the shell delivers it: #/plan. The plan is an estimate from forecasts and, with planner.dispatch off, only a preview: the screen
// must say so, must show the plan's status as the add-on reports it (a stale or suspended plan never looks fresh), the cost against both
// references with the currency label of the data, and the warnings the plan carries. Entity ids and zone ids are monospace. It reads api/plan only.
let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-26T17:02:00Z'));
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

function changed<T>(sample: T, change: (copy: any) => void): T {
  const copy = structuredClone(sample);
  change?.(copy);
  return copy;
}

async function open(answer: unknown = plan, lang: 'en' | 'pl' = 'en') {
  serveSamples(fetchMock, { 'api/plan': answer });
  window.location.hash = '#/plan';
  render(<App lang={lang} />);
  await advance();
}

function card(name: string): HTMLElement {
  return document.querySelector(`[data-card="${name}"]`) as HTMLElement;
}

const row = (container: HTMLElement, name: string) => container.querySelector(`[data-row="${name}"]`) as HTMLElement;

describe('day plan: the page', () => {
  it('says what it is: an estimate and a preview while planner.dispatch is off, with the plan fresh', async () => {
    await open();
    const head = card('plan-head');

    expect(within(head).getByRole('heading', { name: 'Day plan' })).toBeTruthy();
    expect(head.querySelector('.badge')!.textContent).toBe('Plan status: Fresh');
    expect(head.querySelector('[data-plan="dispatch"]')!.textContent).toBe('Preview only: planner.dispatch is off, so dispatch ignores this plan and nothing is switched because of it.');
    expect(head.querySelector('[data-plan="estimate"]')!.textContent).toBe('The plan is an estimate based on forecasts, not a guarantee. Switchboard is not a protective device.');
    expect(head.querySelector('[data-plan="status-note"]')!.textContent).toBe('Computed 2 min ago.');
    expect(row(head, 'plan-solve').textContent).toContain('computed in 3 ms, because: first plan');
    expect(row(head, 'plan-slot').textContent).toBe('60 min');
  });

  it('says the same in Polish', async () => {
    await open(plan, 'pl');
    const head = card('plan-head');

    expect(within(head).getByRole('heading', { name: 'Plan doby' })).toBeTruthy();
    expect(head.querySelector('.badge')!.textContent).toBe('Stan planu: Aktualny');
    expect(head.querySelector('[data-plan="dispatch"]')!.textContent).toContain('Tylko podgląd: planner.dispatch jest wyłączone (off)');
    expect(head.querySelector('[data-plan="estimate"]')!.textContent).toContain('nie jest urządzeniem zabezpieczającym');
    expect(row(card('plan-head'), 'plan-solve').textContent).toContain('powód: pierwszy plan');
  });

  it.each([
    ['narrow', 'planner.dispatch is narrow. A plan that is not fresh is never used.'],
    ['full', 'planner.dispatch is full. A plan that is not fresh is never used.'],
  ])('words planner.dispatch %s without calling it a preview', async (mode, text) => {
    await open(changed(plan, (p) => (p.dispatchMode = mode)));

    expect(card('plan-head').querySelector('[data-plan="dispatch"]')!.textContent).toBe(text);
  });

  it('shows the cost against both references with the currency label of the data, estimate not bill', async () => {
    await open();
    const cost = card('plan-cost');

    expect(row(cost, 'cost-planned').textContent).toMatch(/^3\.4[67] PLN$/);
    expect(row(cost, 'cost-v2').textContent).toBe('5.94 PLN');
    expect(row(cost, 'cost-asap').textContent).toBe('14.91 PLN');
    expect(row(cost, 'cost-diff').textContent).toMatch(/^-2\.4[78] PLN$/);
    expect(cost.textContent).toContain('Not a bill.');
    // The mockup's "without control" wording predates the savings definition and is not used.
    expect(cost.textContent).not.toContain('Without control');
  });

  it('shows each device with its goal and planned runs, ids in monospace', async () => {
    await open();
    const chart = card('plan-chart');
    const car = chart.querySelector('.plan-device[data-device="ev_charger"]') as HTMLElement;

    expect(car.querySelector('h3 code.entity.mono')!.textContent).toBe('ev_charger');
    expect(car.querySelector('[data-goal="what"]')!.textContent).toContain('reach 90 % state of charge');
    expect(car.querySelector('[data-goal-status="on_track"]')).toBeTruthy();
    expect(car.querySelector('[data-goal="plan"]')!.textContent).toContain('Latest start on grid power');
    const runs = car.querySelector('[data-device-runs="list"]')!;
    expect(runs.querySelectorAll('li').length).toBeGreaterThan(0);
    expect(runs.textContent).toContain('from PV surplus');
    expect(runs.textContent).toMatch(/kWh, up to \d+\.\d\d kW/);
    // A device whose goal is inactive says why, and plans nothing.
    const heater = chart.querySelector('.plan-device[data-device="heater_l1"]') as HTMLElement;
    expect(heater.querySelector('[data-goal="inactive"]')!.textContent).toBe('The want switch is off: no goal now.');
    expect(heater.querySelector('[data-device-runs="none"]')!.textContent).toBe('No energy is planned for this device.');
  });

  it('draws the chart with the tariff band, the PV line, a bar per run and the marker for now, and a table as its text form', async () => {
    await open();
    const svg = document.querySelector('svg[data-plan="chart"]')!;

    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.getAttribute('aria-label')).toContain('Day plan');
    expect(svg.querySelectorAll('rect.plan-zone').length).toBe(49);
    expect(svg.querySelector('polyline.plan-pv')).toBeTruthy();
    expect(svg.querySelectorAll('g[data-device="ev_charger"] rect.plan-bar[data-class="pv"]').length).toBeGreaterThan(0);
    expect(svg.querySelector('line.plan-now')).toBeTruthy();
    const slots = card('plan-slots');
    expect(slots.querySelector('summary')!.textContent).toBe('Slots (49)');
    expect(slots.querySelectorAll('tbody tr').length).toBe(49);
    // Zone ids in the table are monospace.
    expect(slots.querySelector('tbody td .mono')!.textContent).toBe('afternoon_peak');
  });

  it.each(['en', 'pl'] as const)('keeps the row labels of the chart (%s) left of the tariff band, whatever the language or the device id', async (lang) => {
    await open(plan, lang);
    const svg = document.querySelector('svg[data-plan="chart"]')!;
    const bandX = Number(svg.querySelector('rect.plan-zone')!.getAttribute('x'));
    const labels = [...svg.querySelectorAll('text.plan-axis')].filter((el) => el.getAttribute('x') === '0');

    // The label column is as wide as its longest label needs (9 px text, 5 px per character at most), so a longer translation never runs under the band.
    expect(labels.length).toBeGreaterThanOrEqual(3);
    for (const label of labels) expect((label.textContent ?? '').length * 5).toBeLessThanOrEqual(bandX);
  });

  it('shows the inputs the plan stood on and the warnings it carries', async () => {
    await open();
    const inputs = card('plan-inputs');

    expect(row(inputs, 'inputs-pv').textContent).toContain('hourly series, multiplied by 0.85');
    expect(row(inputs, 'inputs-pv').textContent).toContain('signal absent, nothing reported');
    expect(row(inputs, 'inputs-load').textContent).toContain('a fixed fallback, no profile learned yet');
    expect(row(inputs, 'inputs-prices').textContent).toBe('the tariff schedule');
    expect(row(inputs, 'inputs-goals').textContent).toBe('1 active, 1 inactive');
    expect(card('plan-notes').querySelector('[data-note="pv_forecast_partial"]')!.textContent).toBe('The PV forecast ends before the horizon: the plan assumes no PV after it.');
  });

  it.each([
    ['stale', 'Plan status: Stale', 'Older than the planner allows (computed 2 min ago): a stale plan is not used.'],
    ['suspended', 'Plan status: Suspended', 'The planner stands aside (guard in safe state, kill switch engaged or Home Assistant disconnected). The last plan, computed 2 min ago, is shown for reference only.'],
  ])('shows a %s plan as such and never as fresh', async (status, badge, note) => {
    await open(changed(plan, (p) => Object.assign(p, { status, suspended: status === 'suspended' })));
    const head = card('plan-head');

    expect(head.querySelector('.badge')!.textContent).toBe(badge);
    expect(head.querySelector('[data-plan="status-note"]')!.textContent).toBe(note);
    // The plan's verdict on a goal is not trusted either: the device blocks say the plan is not fresh.
    expect(card('plan-chart').textContent).toContain('The plan is not fresh, so its verdict on this goal is not shown.');
  });

  it('says a refused solve and why, while the previous plan stays', async () => {
    await open(changed(plan, (p) => Object.assign(p.lastSolve, { result: 'invalid', reason: 'cadence', violations: ['capacity_exceeded', 'hard_goal_unmet'] })));

    const solve = row(card('plan-head'), 'plan-solve');
    expect(solve.textContent).toContain('refused by the verifier, the previous plan stays in 3 ms, because: regular cycle');
    expect(solve.textContent).toContain('Refused for: more than a phase or group can take; a hard goal left without energy');
  });

  it('shows a reason it has no words for as it came', async () => {
    await open(changed(plan, (p) => (p.lastSolve.reason = 'solar_flare')));

    expect(row(card('plan-head'), 'plan-solve').textContent).toContain('because: solar_flare');
  });

  it('says plainly that no planner is configured, draws nothing and shows no plan', async () => {
    await open(
      changed(plan, (p) =>
        Object.assign(p, {
          enabled: false,
          dispatchMode: null,
          status: 'absent',
          id: null,
          computedAt: null,
          validUntil: null,
          horizonEnd: null,
          slotMinutes: null,
          configRevision: null,
          cost: null,
          lastSolve: null,
          slots: [],
          notes: [],
          inputs: { pvForecast: null, loadForecast: null, prices: null, goalsActive: 0, goalsInactive: [] },
          devices: [],
          superseded: [],
        }),
      ),
    );

    expect(card('plan-head').querySelector('[data-plan="not-enabled"]')!.textContent).toContain('No planner is configured');
    expect(card('plan-inputs')).toBeNull();
    expect(card('plan-chart')).toBeNull();
    expect(document.querySelector('svg[data-plan="chart"]')).toBeNull();
  });

  it('says there is no plan yet when the planner has not computed one', async () => {
    await open(changed(plan, (p) => Object.assign(p, { status: 'absent', id: null, computedAt: null, validUntil: null, horizonEnd: null, configRevision: null, cost: null, lastSolve: null, slots: [], devices: [] })));

    expect(card('plan-head').querySelector('.badge')!.textContent).toBe('Plan status: No plan yet');
    expect(card('plan-chart').querySelector('[data-plan="no-slots"]')!.textContent).toContain('no slots to draw');
    expect(card('plan-cost').textContent).toContain('No plan, so no cost.');
  });

  it('shows the cost without a currency label when no economics section names one', async () => {
    await open(changed(plan, (p) => (p.cost.currency = null)));

    expect(row(card('plan-cost'), 'cost-v2').textContent).toBe('5.94');
  });

  it('says it is unavailable when the add-on does not answer, instead of showing a plan', async () => {
    fetchMock.mockImplementation(() => respond({}, 500));
    window.location.hash = '#/plan';
    render(<App lang="en" />);
    await advance();

    expect(screen.getByRole('alert').textContent).toBe('Unavailable — no update yet');
    expect(card('plan-chart')).toBeNull();
  });

  it('refuses an answer outside the contract rather than guessing', async () => {
    await open(changed(plan, (p) => (p.status = 'overdue')));

    expect(screen.getByRole('alert').textContent).toContain('Unavailable');
    expect(card('plan-head')).toBeNull();
  });

  it('asks for nothing but api/plan (besides the shell mode banner) and no write', async () => {
    await open();

    expect(fetchMock.mock.calls.map(([input]) => String(input)).filter((url) => url !== 'api/mode')).toEqual(['api/plan']);
    expect(fetchMock.mock.calls.every(([, init]) => init?.method === undefined || init.method === 'GET')).toBe(true);
  });

  it('is the day plan of the navigation, marked as the page on screen', async () => {
    await open();

    const link = within(screen.getByRole('navigation')).getByText('Day plan', { exact: false }).closest('a')!;
    expect(link.getAttribute('href')).toBe('#/plan');
    expect(link.getAttribute('aria-current')).toBe('page');
  });
});
