import { afterEach, describe, expect, it, vi } from 'vitest';
import { deleteChargeNow, EV_MODES, EV_REFUSALS, EvError, parseEv, postChargeNow, postEvMode } from './ev';
import { EV_CAUSES } from './plan';
import sample from './ev.sample.json';

// The sample is what GET api/ev writes for the worked example (the car at 40 %, one trip tomorrow, planner.dispatch off), so these tests parse
// what the add-on sends. The EV card must never show a reason, a goal or a mode the add-on did not report: anything outside the contract makes
// the page say nothing instead of guessing. `manual` is the key of S6: an answer without it is an add-on without manual control.
function payload(change?: (p: any) => void): any {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('parseEv', () => {
  it('accepts the documented payload', () => {
    const ev = parseEv(payload());

    expect(ev).toMatchObject({ enabled: true, addOnZone: 'Europe/Warsaw', manual: { mode: 'auto', chargeNow: { active: true, targetPct: 80 } } });
    expect(ev.config).toMatchObject({ device: 'ev_charger', capacityKwh: 18, cheapZone: null, plannerDispatch: 'off', pluggedStates: ['Charging', 'Wait for car', 'complete'] });
    expect(ev.parameters).toEqual({ chargeNowTargetPercent: 90, chargeNowDurationS: 43200 });
    expect(ev.inputs).toMatchObject({ socPercent: 40, rangeKm: 54, isPlugged: true, pluggedState: 'Charging' });
    expect(ev.inputs!.plugged).toMatchObject({ state: 'fresh', maxAgeS: 3600 });
    expect(ev.trips!.nextTrip).toMatchObject({ title: 'Kurs Warszawa', km: 42 });
    expect(ev.trips!.nextTrip!.start).toBeInstanceOf(Date);
    expect(ev.assessment).toMatchObject({ failed: false, reason: 'ev_trip_deadline', notes: ['ev_without_planner_dispatch'], mode: 'auto', socPercent: 40, socGap: null, expired: false });
    expect(ev.assessment!.goals).toEqual([
      expect.objectContaining({ kind: 'cap', targetPercent: 90, hard: false, sources: 'pv_only', spread: false }),
      expect.objectContaining({ kind: 'trip', hard: true, sources: 'pv_and_zone:offpeak', spread: true }),
    ]);
  });

  it('accepts the answer without an ev: section: only the issues, nothing else', () => {
    const ev = parseEv(payload((p) => Object.assign(p, { enabled: false, config: null, parameters: null, addOnZone: null, inputs: null, trips: null, assessment: null, manual: null })));

    expect(ev).toMatchObject({ enabled: false, config: null, assessment: null, manual: null });
  });

  it('accepts a failed computation: no reason, no goals', () => {
    const ev = parseEv(
      payload((p) => Object.assign(p.assessment, { failed: true, reason: null, notes: [], computedAt: null, validUntil: null, expired: true, mode: null, socPercent: null, kwhPerKm: null, nextTrip: null, lastFullAt: null, goals: [] })),
    );

    expect(ev.assessment).toMatchObject({ failed: true, reason: null, goals: [] });
  });

  it('accepts the manual control of S6, with or without a running charge', () => {
    const idle = parseEv(payload((p) => (p.manual = { mode: 'pv_only', chargeNow: { active: false, targetPct: null, until: null } })));
    expect(idle.manual).toEqual({ mode: 'pv_only', chargeNow: { active: false, targetPct: null, until: null } });

    const running = parseEv(payload((p) => (p.manual = { mode: 'auto', chargeNow: { active: true, targetPct: 90, until: '2026-10-27T05:00:00+00:00' } })));
    expect(running.manual!.chargeNow).toMatchObject({ active: true, targetPct: 90 });
    expect(running.manual!.chargeNow.until).toBeInstanceOf(Date);
  });

  it('reads the unparsed events and the warnings', () => {
    const ev = parseEv(
      payload((p) => {
        p.trips.nextTrip = null;
        p.trips.unparsed = [{ title: 'Dentysta', start: '2026-10-26T18:00:00+00:00', reason: 'no_km', km: null }];
        p.trips.warnings = [{ kind: 'next_trip_may_be_hidden', title: 'Dentysta', start: '2026-10-26T18:00:00+00:00' }];
      }),
    );

    expect(ev.trips!.unparsed[0]).toMatchObject({ reason: 'no_km', km: null });
    expect(ev.trips!.warnings[0]!.kind).toBe('next_trip_may_be_hidden');
  });

  it.each([
    ['an unknown reason code', (p: any) => (p.assessment.reason = 'ev_made_up')],
    ['an unknown note', (p: any) => (p.assessment.notes = ['ev_made_up'])],
    ['an unknown mode', (p: any) => (p.assessment.mode = 'turbo')],
    ['an unknown goal kind', (p: any) => (p.assessment.goals[0].kind = 'sprint')],
    ['an unknown SoC gap', (p: any) => (p.assessment.socGap = 'sleepy')],
    ['an unknown unparsed reason', (p: any) => (p.trips.unparsed = [{ title: 'x', start: '2026-10-26T18:00:00+00:00', reason: 'boring', km: null }])],
    ['sources that are no class set', (p: any) => (p.assessment.goals[0].sources = 'sometimes')],
    ['a signal state outside the closed list', (p: any) => (p.inputs.soc.state = 'warm')],
    ['a missing key', (p: any) => delete p.assessment.goals],
    ['a section without its config', (p: any) => (p.config = null)],
    ['a config without a section', (p: any) => Object.assign(p, { enabled: false })],
    ['a failed computation that names a reason', (p: any) => (p.assessment.failed = true)],
    ['a failed computation that still offers goals', (p: any) => Object.assign(p.assessment, { failed: true, reason: null })],
    ['a computation that ran without a reason', (p: any) => (p.assessment.reason = null)],
    ['a manual mode outside the list', (p: any) => (p.manual = { mode: 'turbo', chargeNow: { active: false, targetPct: null, until: null } })],
    ['a timestamp that is not one', (p: any) => (p.trips.nextTrip.start = 'tomorrow-ish')],
  ])('refuses %s', (_name, change) => {
    expect(() => parseEv(payload(change))).toThrow(/Payload: ev\./);
  });

  it('knows the closed lists the add-on writes: every EV code, and the three modes (never "off", the device override)', () => {
    expect(EV_CAUSES).toHaveLength(14);
    expect([...EV_MODES]).toEqual(['auto', 'pv_only', 'full_charge']);
    expect(EV_REFUSALS).toHaveLength(10);
  });

  it.each([
    ['an assessment reason', (p: any) => (p.assessment.reason = 'ev_new_code')],
    ['an assessment note', (p: any) => (p.assessment.notes = ['ev_new_code'])],
    ['a goal kind', (p: any) => (p.assessment.goals[0].kind = 'new_kind')],
    ['a mode', (p: any) => (p.assessment.mode = 'new_mode')],
  ])('rejects %s the page has no sentence for, so a code that grew is never shown as "undefined"', (_name, change) => {
    expect(() => parseEv(payload(change))).toThrow(/Payload: ev\./);
  });

  it('reads a manual block as the add-on writes it, and a mode "off" is not one', () => {
    const idle = { mode: 'full_charge', chargeNow: { active: true, targetPct: 80, until: '2026-10-26T20:00:00+00:00' } };
    expect(parseEv(payload((p: any) => (p.manual = idle))).manual).toMatchObject({ mode: 'full_charge', chargeNow: { active: true, targetPct: 80 } });
    expect(() => parseEv(payload((p: any) => (p.manual = { ...idle, mode: 'off' })))).toThrow(/Payload: ev\.manual/);
  });
});

function answer(status: number, code?: string): Response {
  return { ok: status >= 200 && status < 300, status, json: () => Promise.resolve(code === undefined ? {} : { code, message: 'x' }) } as Response;
}

describe('the manual requests', () => {
  it('POST api/ev/mode sends the mode as a JSON body (a form cannot, so a cross-site page cannot)', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(answer(200)));
    vi.stubGlobal('fetch', fetchMock);

    await postEvMode('pv_only');

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('api/ev/mode');
    expect(init.method).toBe('POST');
    expect(init.headers).toMatchObject({ 'Content-Type': 'application/json' });
    expect(JSON.parse(String(init.body))).toEqual({ mode: 'pv_only' });
  });

  it('POST api/ev/charge-now sends only what was asked for, so the add-on’s own defaults apply to the rest', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(answer(200)));
    vi.stubGlobal('fetch', fetchMock);

    await postChargeNow({});
    await postChargeNow({ targetPct: 80, durationMinutes: 90 });

    const bodies = (fetchMock.mock.calls as unknown as [string, RequestInit][]).map(([, init]) => JSON.parse(String(init.body)));
    expect(bodies).toEqual([{}, { targetPct: 80, durationMinutes: 90 }]);
  });

  it('DELETE api/ev/charge-now has no body', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(answer(200)));
    vi.stubGlobal('fetch', fetchMock);

    await deleteChargeNow();

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('api/ev/charge-now');
    expect(init.method).toBe('DELETE');
    expect(init.body).toBeUndefined();
  });

  it.each([
    [400, 'bad_request'],
    [404, 'not_configured'],
    [404, 'no_charge_now'],
    [409, 'needs_full_dispatch'],
    [409, 'not_plugged'],
    [409, 'soc_unknown'],
    [409, 'already_at_target'],
    [422, 'target_out_of_range'],
    [422, 'duration_out_of_range'],
    [422, 'deadline_beyond_horizon'],
  ])('answer %i with the code %s is worded by the code, not by the status', async (status, code) => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(answer(status, code))));

    const error = await postChargeNow({}).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EvError);
    expect((error as EvError).refusal).toBe(code);
  });

  it.each([
    [400, 'bad_request'],
    [404, 'not_configured'],
    [409, 'needs_full_dispatch'],
    [422, 'failed'],
    [500, 'failed'],
    [503, 'failed'],
  ])('answer %i without a code the page knows is worded as %s', async (status, refusal) => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(answer(status))));

    const error = await postChargeNow({}).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EvError);
    expect((error as EvError).refusal).toBe(refusal);
  });
});
