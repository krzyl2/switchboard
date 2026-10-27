import { describe, expect, it } from 'vitest';
import {
  DISPATCH_MODES,
  ENTRY_WANTS,
  GOAL_SOURCES,
  GOAL_STATUSES,
  INACTIVE_REASONS,
  PLAN_NOTES,
  PLAN_STATUSES,
  parsePlan,
  VIOLATION_CODES,
} from './plan';
import sample from './plan.sample.json';

// The sample is what GET api/plan wrote for the worked example's fresh plan (PlanSpaContractTests compares its keys with the endpoint's
// and the closed lists with the backend's enums), so these tests parse what the add-on sends. The screen and the goal row must never show a
// status, a run or a cost the add-on did not report: anything outside the contract makes them say "unavailable" instead of guessing.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

describe('parsePlan', () => {
  it('accepts the documented payload', () => {
    const plan = parsePlan(payload());

    expect(plan).toMatchObject({ enabled: true, dispatchMode: 'off', status: 'fresh', suspended: false, slotMinutes: 60, cost: { currency: 'PLN' }, lastSolve: { result: 'computed', reason: 'first', violations: [] } });
    expect(plan.cost!.planned).toBeCloseTo(3.465, 9);
    expect(plan.slots).toHaveLength(49);
    expect(plan.slots[0]!.start).toBeInstanceOf(Date);
    const car = plan.devices.find((d) => d.device === 'ev_charger')!;
    expect(car.goal).toMatchObject({ kind: 'soc', hard: true, source: 'entity', targetSoc: 90, currentSoc: 40, status: 'on_track', plannedKwh: 9.9, urgent: false, device: null });
    expect(car.goal!.latestGridStart).toBeInstanceOf(Date);
    expect(car.entries).toHaveLength(49);
    expect(car.entries.filter((e) => e.want === 'run').every((e) => e.class === 'pv')).toBe(true);
    expect(plan.devices.find((d) => d.device === 'heater_l1')!.goal).toMatchObject({ status: 'inactive', inactiveReason: 'want_off', inactiveInput: 'want' });
    expect(plan.superseded).toHaveLength(1);
    expect(plan.superseded[0]).toMatchObject({ status: 'superseded', source: 'config', device: 'ev_charger' });
  });

  it('accepts the answer without a planner: no plan, no slots, no cost, the goals still listed', () => {
    const plan = parsePlan(
      payload((p) =>
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
          inputs: { pvForecast: null, loadForecast: null, prices: null, goalsActive: 1, goalsInactive: [] },
          devices: p.devices.map((d: any) => ({ ...d, entries: [], shortfallKwh: null, predictedCompletion: null })),
        }),
      ),
    );

    expect(plan).toMatchObject({ enabled: false, dispatchMode: null, status: 'absent', cost: null, lastSolve: null });
    expect(plan.slots).toEqual([]);
  });

  it('accepts a planner that holds no plan yet', () => {
    const plan = parsePlan(payload((p) => Object.assign(p, { status: 'absent', id: null, computedAt: null, validUntil: null, horizonEnd: null, configRevision: null, cost: null, lastSolve: null, slots: [] })));

    expect(plan).toMatchObject({ enabled: true, status: 'absent', id: null, cost: null });
  });

  it.each(PLAN_STATUSES)('accepts the plan status %s', (status) => {
    expect(parsePlan(payload((p) => (p.status = status))).status).toBe(status);
  });

  it.each(DISPATCH_MODES)('accepts the dispatch mode %s', (mode) => {
    expect(parsePlan(payload((p) => (p.dispatchMode = mode))).dispatchMode).toBe(mode);
  });

  it.each(GOAL_SOURCES)('accepts the goal source %s', (source) => {
    expect(parsePlan(payload((p) => (p.devices[0].goal.source = source))).devices[0]!.goal!.source).toBe(source);
  });

  it.each(GOAL_STATUSES.filter((s) => s !== 'inactive'))('accepts the goal status %s', (status) => {
    expect(parsePlan(payload((p) => (p.devices[0].goal.status = status))).devices[0]!.goal!.status).toBe(status);
  });

  it.each(INACTIVE_REASONS)('accepts the inactive reason %s', (reason) => {
    expect(parsePlan(payload((p) => (p.devices[1].goal.inactiveReason = reason))).devices[1]!.goal!.inactiveReason).toBe(reason);
  });

  it.each(PLAN_NOTES)('accepts the note %s', (note) => {
    expect(parsePlan(payload((p) => (p.notes = [note]))).notes).toEqual([note]);
  });

  it.each(VIOLATION_CODES)('accepts the verifier code %s', (code) => {
    expect(parsePlan(payload((p) => Object.assign(p.lastSolve, { result: 'invalid', violations: [code] }))).lastSolve!.violations).toEqual([code]);
  });

  it('accepts a PV forecast signal that is not mapped (absent) and one that is fresh with its age', () => {
    expect(parsePlan(payload((p) => (p.inputs.pvForecast.state = 'absent'))).inputs.pvForecast!.state).toBe('absent');
    expect(parsePlan(payload((p) => Object.assign(p.inputs.pvForecast, { state: 'fresh', ageS: 120 }))).inputs.pvForecast).toMatchObject({ state: 'fresh', ageS: 120 });
  });

  it('accepts a class of a zone entry', () => {
    const plan = parsePlan(payload((p) => Object.assign(p.devices[0].entries[0], { want: 'urgent', class: 'zone:offpeak', energyKwh: 2, maxPowerW: 3700 })));

    expect(plan.devices[0]!.entries[0]).toMatchObject({ want: 'urgent', class: 'zone:offpeak' });
  });

  it('keeps the closed lists to what the backend sends today', () => {
    expect([...ENTRY_WANTS].sort()).toEqual(['defer', 'done', 'none', 'run', 'urgent']);
    expect([...GOAL_STATUSES]).toContain('superseded');
  });

  it.each([
    ['an unknown plan status', (p: any) => (p.status = 'old')],
    ['an unknown dispatch mode', (p: any) => (p.dispatchMode = 'wide')],
    ['an unknown goal status', (p: any) => (p.devices[0].goal.status = 'doomed')],
    ['an unknown goal source', (p: any) => (p.devices[0].goal.source = 'calendar')],
    ['an unknown goal kind', (p: any) => (p.devices[0].goal.kind = 'power')],
    ['an unknown note', (p: any) => (p.notes = ['weather_bad'])],
    ['an unknown entry want', (p: any) => (p.devices[0].entries[0].want = 'sleep')],
    ['an unknown PV basis', (p: any) => (p.inputs.pvForecast.basis = 'guess')],
    ['an unknown load basis', (p: any) => (p.inputs.loadForecast.basis = 'guess')],
    ['an unknown price basis', (p: any) => (p.inputs.prices.basis = 'guess')],
    ['an unknown solve result', (p: any) => (p.lastSolve.result = 'maybe')],
    ['an unknown verifier code', (p: any) => (p.lastSolve.violations = ['too_expensive'])],
    ['sources outside the grammar', (p: any) => (p.devices[0].goal.sources = 'grid_only')],
    ['a class outside the grammar', (p: any) => Object.assign(p.devices[0].entries[0], { want: 'run', class: 'grid' })],
    ['a run without its class', (p: any) => Object.assign(p.devices[0].entries[0], { want: 'run', class: null })],
    ['a class on a slot where the device waits', (p: any) => Object.assign(p.devices[0].entries[0], { want: 'defer', class: 'pv' })],
    ['an inactive goal without its reason', (p: any) => (p.devices[1].goal.inactiveReason = null)],
    ['a reason on a goal that is not inactive', (p: any) => Object.assign(p.devices[0].goal, { inactiveReason: 'want_off', inactiveInput: 'want' })],
    ['a reason without its input', (p: any) => (p.devices[1].goal.inactiveInput = null)],
    ['a dispatch mode without a planner', (p: any) => (p.enabled = false)],
    ['a planner without a dispatch mode', (p: any) => (p.dispatchMode = null)],
    ['a plan without its cost', (p: any) => (p.cost = null)],
    ['a cost without a plan', (p: any) => (p.id = null)],
    ['a missing key', (p: any) => delete p.slots],
    ['a missing goal key', (p: any) => delete p.devices[0].goal.latestGridStart],
    ['a missing superseded device key', (p: any) => delete p.superseded[0].device],
    ['a number sent as text', (p: any) => (p.cost.planned = '3.46')],
    ['a negative count', (p: any) => (p.inputs.goalsActive = -1)],
    ['a malformed time', (p: any) => (p.slots[0].start = 'noon')],
    ['an issue without its message', (p: any) => (p.issues = [{ code: 'x', path: 'planner' }])],
  ])('rejects %s', (_case, change) => {
    expect(() => parsePlan(payload(change))).toThrow();
  });

  it('rejects an answer that is not an object', () => {
    expect(() => parsePlan('nope')).toThrow();
    expect(() => parsePlan(null)).toThrow();
  });
});
