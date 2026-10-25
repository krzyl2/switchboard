import { afterEach, describe, expect, it, vi } from 'vitest';
import { OVERRIDE_MODES, VERDICTS } from './decisions';
import {
  DISPATCH_INVALID_REASONS,
  HISTORY_KINDS,
  OVERRIDE_SOURCES,
  SETTLING_STATUSES,
  parseDispatch,
  postOverride,
} from './dispatch';
import sample from './dispatch.sample.json';

// The sample has the keys GET api/dispatch really writes: DispatchSpaContractTests on the .NET side
// compares them with the endpoint's output, so these tests parse what the add-on sends.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// The page must never show an intention, an override or a "not responding" flag the add-on did
// not report: anything outside the exact contract is rejected and the card says "unavailable".
describe('parseDispatch', () => {
  // 0.7.1: the 0.6.x UI choice is gone from the mode.
  it('reads the mode without the 0.6.x UI choice', () => {
    expect(parseDispatch(payload()).mode).toEqual({ ceiling: 'shadow', effective: 'shadow', note: 'none' });
  });

  it('accepts the documented payload and keeps every phase, device and history entry', () => {
    const parsed = parseDispatch(payload());

    expect(parsed.commandPath).toBe(false);
    expect(parsed.phases.map((p) => [p.phase, p.dataValid, p.rank])).toEqual([
      ['L1', true, 2],
      ['L2', true, 1],
      ['L3', false, 3],
    ]);
    expect(parsed.phases[0]?.groups).toEqual([{ group: 'carport', envelopeA: 4, inFlightA: -10, increasesAllowed: true }]);
    expect(parsed.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger', 'battery']);
    const charger = parsed.devices[3];
    expect(charger?.intended).toEqual({ relays: null, setpointA: 6, enabled: true });
    expect(charger?.override).toEqual({ mode: 'force_off', changedAt: new Date('2026-10-24T12:00:05.000Z'), source: 'api' });
    expect(charger?.draw).toEqual({ currentA: 9.6, powerW: 2208, fresh: true });
    expect(parsed.devices[0]?.deferred?.refusedBy).toBe('lockout_min_off');
    expect(parsed.devices[2]?.unresponsive?.retryAt?.toISOString()).toBe('2026-10-24T12:10:00.004Z');
    expect(parsed.devices[2]?.history.map((h) => h.verdict)).toEqual(['mismatch', 'match']);
  });

  it('accepts dispatch before its first evaluation: no phase, no device', () => {
    expect(parseDispatch(payload((p) => Object.assign(p, { phases: [], devices: [] }))).devices).toEqual([]);
  });

  it('drops fields the page does not know about', () => {
    const parsed = parseDispatch(payload((p) => (p.devices[0].actuator = 'switch.must_not_travel')));

    expect(parsed.devices[0]).not.toHaveProperty('actuator');
  });

  it.each(SETTLING_STATUSES)('accepts the settling status %s', (status) => {
    expect(parseDispatch(payload((p) => (p.devices[0].settling = status))).devices[0]?.settling).toBe(status);
  });

  it.each(VERDICTS)('accepts the verdict %s', (verdict) => {
    expect(parseDispatch(payload((p) => (p.devices[2].history[0].verdict = verdict))).devices[2]?.history[0]?.verdict).toBe(verdict);
  });

  it.each(HISTORY_KINDS)('accepts the history kind %s', (kind) => {
    expect(parseDispatch(payload((p) => (p.devices[2].history[0].kind = kind))).devices[2]?.history[0]?.kind).toBe(kind);
  });

  it.each(OVERRIDE_SOURCES)('accepts the override source %s', (source) => {
    expect(parseDispatch(payload((p) => (p.devices[3].override.source = source))).devices[3]?.override.source).toBe(source);
  });

  it.each(OVERRIDE_MODES.filter((m) => m !== 'auto'))('accepts the forced override %s', (mode) => {
    expect(parseDispatch(payload((p) => (p.devices[3].override.mode = mode))).devices[3]?.override.mode).toBe(mode);
  });

  it.each(DISPATCH_INVALID_REASONS)('accepts the phase invalid reason %s', (reason) => {
    expect(parseDispatch(payload((p) => (p.phases[2].invalidReason = reason))).phases[2]?.invalidReason).toBe(reason);
  });

  it.each([
    ['an unknown effective mode', (p: any) => (p.mode.effective = 'manual')],
    ['an unknown mode note', (p: any) => (p.mode.note = 'later')],
    ['an unknown device type', (p: any) => (p.devices[0].type = 'onoff')],
    ['an unknown phase', (p: any) => (p.devices[0].phases = ['L4'])],
    ['an unknown origin', (p: any) => (p.devices[1].origin = 'planner')],
    ['a reason outside the code list', (p: any) => (p.devices[0].reason = 'HeadroomAvailable')],
    ['a pending reason outside the code list', (p: any) => (p.devices[2].pending = 'waiting')],
    ['a refusal outside the code list', (p: any) => (p.devices[0].deferred.refusedBy = 'lockout')],
    ['an unknown settling status', (p: any) => (p.devices[0].settling = 'closed')],
    ['an unknown override mode', (p: any) => (p.devices[3].override.mode = 'on')],
    ['an unknown override source', (p: any) => (p.devices[3].override.source = 'ui')],
    ['an unknown history kind', (p: any) => (p.devices[2].history[0].kind = 'probe')],
    ['an unknown unit', (p: any) => (p.devices[2].history[0].unit = 'W')],
    ['an unknown verdict', (p: any) => (p.devices[2].history[0].verdict = 'ok')],
    ['an unknown phase invalid reason', (p: any) => (p.phases[2].invalidReason = 'tired')],
  ])('rejects unknown enum strings: %s', (_case, change) => {
    expect(() => parseDispatch(payload(change))).toThrow();
  });

  it.each([
    ['null', () => null],
    ['an array', () => []],
    ['a missing key that may be null', () => payload((p) => delete p.devices[0].deferred)],
    ['a missing command path', () => payload((p) => delete p.commandPath)],
    ['a command path sent as text', () => payload((p) => (p.commandPath = 'false'))],
    ['a number sent as text', () => payload((p) => (p.phases[0].envelopeA = '3.2'))],
    ['a timestamp that is not one', () => payload((p) => (p.at = 'now'))],
    ['a negative mismatch count', () => payload((p) => (p.devices[2].mismatches = -1))],
    ['start rank zero', () => payload((p) => (p.devices[0].startRank = 0))],
    // A level is relays or a setpoint, exactly one of them.
    ['a level with relays and a setpoint', () => payload((p) => (p.devices[1].intended.setpointA = 6))],
    ['a level with neither relays nor a setpoint', () => payload((p) => (p.devices[3].intended.setpointA = null))],
    // A stale "off" must never look current.
    ['a reported state that is not fresh', () => payload((p) => (p.devices[0].reportedFresh = false))],
    ['a fresh device without a reported state', () => payload((p) => (p.devices[2].reportedFresh = true))],
    // Nothing intended means nothing to change.
    ['no intention that does not match', () => payload((p) => (p.devices[0].matches = false))],
    // Auto is "no override": it has no time and no source; a forced mode always has both.
    ['an automatic override with a source', () => payload((p) => (p.devices[0].override.source = 'api'))],
    ['a forced override without its time', () => payload((p) => (p.devices[3].override.changedAt = null))],
    ['valid phase data that names a reason', () => payload((p) => (p.phases[0].invalidReason = 'power_not_fresh'))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseDispatch(make())).toThrow();
  });
});

describe('postOverride', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stub(status: number, body: unknown = sample) {
    const fetchMock = vi.fn<typeof fetch>(() =>
      Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) } as Response),
    );
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  // SAFE-04 from the page: a JSON body (a cross-site form cannot send one without a CORS preflight)
  // to the relative URL of the device, and the evaluated status as the answer.
  it('posts the mode as JSON to the device and returns the evaluated status', async () => {
    const fetchMock = stub(200);

    const status = await postOverride('ev_charger', 'force_off', new AbortController().signal);

    const [url, init] = fetchMock.mock.calls[0] ?? [];
    expect(url).toBe('api/devices/ev_charger/override');
    expect(init?.method).toBe('POST');
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(init?.body).toBe('{"mode":"force_off"}');
    expect(status.devices).toHaveLength(sample.devices.length);
  });

  it.each([
    [404, 'unknown_device'],
    [409, 'no_configuration'],
    [503, 'not_evaluated'],
    [400, 'failed'],
    [500, 'failed'],
  ] as const)('maps HTTP %i to the refusal %s', async (status, refusal) => {
    stub(status, {});

    await expect(postOverride('heater_l1', 'auto', new AbortController().signal)).rejects.toMatchObject({ refusal });
  });

  it('refuses an answer outside the contract', async () => {
    stub(200, { devices: 'none' });

    await expect(postOverride('heater_l1', 'auto', new AbortController().signal)).rejects.toThrow();
  });
});

// Stage 4: a history entry says when the change was decided (or seen) and from which level to which,
// so the device card shows "8.7 A → 6 A" next to its verdict.
describe('parseDispatch, history levels', () => {
  it('keeps the command time and the levels of each history entry', () => {
    const entry = parseDispatch(payload()).devices[0]?.history[0];

    expect(entry?.commandedAt?.toISOString()).toBe('2026-10-24T11:58:01.004Z');
    expect(entry?.from).toEqual({ relays: [true], setpointA: null, enabled: true });
    expect(entry?.to).toEqual({ relays: [false], setpointA: null, enabled: true });
  });

  it('accepts an entry recorded before 0.5.0, without them', () => {
    const entry = parseDispatch(payload((p) => Object.assign(p.devices[0].history[0], { commandedAt: null, from: null, to: null }))).devices[0]?.history[0];

    expect([entry?.commandedAt, entry?.from, entry?.to]).toEqual([null, null, null]);
  });

  it('rejects a level that is neither relays nor a setpoint', () => {
    expect(() => parseDispatch(payload((p) => (p.devices[0].history[0].to.setpointA = 6)))).toThrow();
  });
});

// Stage C: restore_only devices carry the level Switchboard would bring back and since when it holds
// them lower; an acknowledged command the device did not follow is a warning on its card.
describe('parseDispatch, control stage fields', () => {
  it('reads the start policy, the restore baseline, the command warning and the self-start', () => {
    const parsed = parseDispatch(payload());
    const heater = parsed.devices[0]!;

    expect(heater.start).toBe('restore_only');
    expect(heater.baseline).toMatchObject({ level: { relays: [true] }, commanded: true });
    expect(heater.commandWarning?.since).toBeInstanceOf(Date);
    expect(heater.selfStarted).toBeInstanceOf(Date);
    expect(parsed.devices[3]).toMatchObject({ start: 'headroom', baseline: null, commandWarning: null, selfStarted: null });
  });

  it.each([
    ['an unknown start policy', (p: any) => (p.devices[0].start = 'eager')],
    ['a baseline without its level', (p: any) => delete p.devices[0].baseline.level],
    ['a baseline without saying whether it was commanded', (p: any) => delete p.devices[0].baseline.commanded],
    ['a missing command warning key', (p: any) => delete p.devices[1].commandWarning],
  ])('refuses %s', (_case, change) => {
    expect(() => parseDispatch(payload(change))).toThrow();
  });
});
