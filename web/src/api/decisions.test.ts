import { describe, expect, it } from 'vitest';
import { DECISION_ACTIONS, DECISION_KINDS, DECISION_ORIGINS, MODE_NOTES, REASON_CODES, SAFE_STATE_CAUSES, parseDecisionsPage } from './decisions';
import sample from './decisions.sample.json';

// The sample has the keys GET api/decisions really writes (GuardSpaContractTests compares them).
/** The first sample item with this action. */
function at(p: any, action: string): any {
  return p.items.find((d: any) => d.action === action);
}

function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// MODE-03: every decision carries a reason code from the closed list. A code the page does not know
// would be rendered as a blank reason, which reads as "no reason": the whole answer is refused instead.
describe('parseDecisionsPage', () => {
  it('accepts the documented payload with its cursor and every input part', () => {
    const parsed = parseDecisionsPage(payload());

    expect(parsed.items.map((d) => d.action).slice(0, 3)).toEqual(['switch_off', 'switch_off', 'reduce_to_minimum']);
    expect(parsed.items).toHaveLength(sample.items.length);
    expect(parsed.next).toBe(sample.next);
    expect(parsed.logWritable).toBe(true);
    const first = parsed.items[0];
    expect(first).toMatchObject({ device: 'heater_l1', reason: 'guard_tier_exceeded', overridden: 'lockout_min_on' });
    expect(first?.inputs.phase).toMatchObject({ phase: 'L1', currentA: 33, band: 4, reaction: 'shed_one_immediately' });
    expect(first?.inputs.device).toMatchObject({ applied: 'on', shedRank: 2, canSwitchOff: true });
    expect(first?.at.toISOString()).toBe('2026-10-24T10:00:00.004Z');
  });

  it.each(SAFE_STATE_CAUSES)('accepts the safe-state cause %s', (cause) => {
    const parsed = parseDecisionsPage(payload((p) => (p.items[0].inputs.safeStateCause = cause)));
    expect(parsed.items[0]?.inputs.safeStateCause).toBe(cause);
  });

  it.each(REASON_CODES)('accepts the reason code %s', (code) => {
    expect(parseDecisionsPage(payload((p) => (p.items[0].reason = code))).items[0]?.reason).toBe(code);
  });

  it.each([
    ['a reason outside the list', (p: any) => (p.items[0].reason = 'made_up_code')],
    ['a reason in the C# spelling', (p: any) => (p.items[0].reason = 'GuardTierExceeded')],
    ['an overridden rule outside the list', (p: any) => (p.items[0].overridden = 'lockout')],
    ['a refusal outside the list', (p: any) => (p.items[0].refusedBy = 'shadow')],
    ['an unknown action', (p: any) => (p.items[0].action = 'turn_off')],
    ['an unknown scope', (p: any) => (p.items[0].scope = 'circuit')],
    ['an unknown origin', (p: any) => (p.items[0].origin = 'planner')],
    ['an unknown settling state in the dispatch inputs', (p: any) => (at(p, 'switch_on').inputs.dispatch.settling = 'pending')],
    ['an unknown verdict in the dispatch inputs', (p: any) => (at(p, 'marked_unresponsive').inputs.dispatch.verification.verdict = 'ok')],
    ['an unknown unit in the dispatch inputs', (p: any) => (at(p, 'marked_unresponsive').inputs.dispatch.verification.unit = 'W')],
    ['an unknown override in the dispatch inputs', (p: any) => (at(p, 'switch_on').inputs.dispatch.override = 'manual')],
    ['an unknown mode', (p: any) => (p.items[0].mode = 'Shadow')],
    ['an unknown tier reaction in the inputs', (p: any) => (p.items[0].inputs.phase.reaction = 'shed')],
    ['an unknown kill switch source in the inputs', (p: any) => (p.items[0].inputs.killSwitch = 'ui')],
    ['an unknown safe-state cause in the inputs', (p: any) => (p.items[0].inputs.safeStateCause = 'reboot')],
    ['an unknown device state reason in the inputs', (p: any) => (p.items[0].inputs.device.stateInvalidReason = 'old')],
  ])('rejects unknown enum strings: %s', (_case, change) => {
    expect(() => parseDecisionsPage(payload(change))).toThrow();
  });

  it.each([
    ['null', () => null],
    ['a bare array', () => sample.items],
    ['a missing cursor key', () => payload((p) => delete p.next)],
    ['a missing input part', () => payload((p) => delete p.items[0].inputs.mode)],
    ['a missing safe-state cause key', () => payload((p) => delete p.items[0].inputs.safeStateCause)],
    ['a missing dispatch input key', () => payload((p) => delete p.items[0].inputs.dispatch)],
    ['a missing target key', () => payload((p) => delete p.items[0].target)],
    ['a group decision without its group', () => payload((p) => Object.assign(p.items[0], { scope: 'group', group: null }))],
    // A target is relays or a setpoint, exactly one of them.
    ['a target with relays and a setpoint', () => payload((p) => (at(p, 'switch_on').target.setpointA = 6))],
    ['a target with neither', () => payload((p) => (at(p, 'set_setpoint').target.setpointA = null))],
    ['a start rank of zero in the dispatch inputs', () => payload((p) => (at(p, 'switch_on').inputs.dispatch.startRank = 0))],
    ['a device decision without its device', () => payload((p) => (p.items[0].device = null))],
    ['a phase decision without its phase', () => payload((p) => Object.assign(p.items[0], { scope: 'phase', phase: null }))],
    ['valid phase data that names a reason', () => payload((p) => (p.items[0].inputs.phase.invalidReason = 'current_stale'))],
    ['a relay state next to a setpoint', () => payload((p) => (p.items[0].inputs.device.setpointA = 6))],
    ['stage number zero', () => payload((p) => (p.items[0].stage = 0))],
    ['a commanded flag sent as text', () => payload((p) => (p.items[0].commanded = 'false'))],
    ['a time that is not one', () => payload((p) => (p.items[0].at = 'today'))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseDecisionsPage(make())).toThrow();
  });
});

// Decision log format 2 (Stage 3): who decided, the group, the target, and dispatch's inputs.
describe('parseDecisionsPage, dispatch decisions', () => {
  it.each(DECISION_ACTIONS)('accepts the action %s', (action) => {
    expect(parseDecisionsPage(payload((p) => (p.items[0].action = action))).items[0]?.action).toBe(action);
  });

  it.each(DECISION_ORIGINS)('accepts the origin %s', (origin) => {
    expect(parseDecisionsPage(payload((p) => (p.items[0].origin = origin))).items[0]?.origin).toBe(origin);
  });

  it('keeps the target and the inputs dispatch decided from', () => {
    const items = parseDecisionsPage(payload()).items;
    const start = items.find((d) => d.action === 'switch_on');
    const unresponsive = items.find((d) => d.action === 'marked_unresponsive');
    const forced = items.find((d) => d.action === 'set_setpoint');

    expect(start).toMatchObject({ origin: 'dispatch', target: { relays: [true], setpointA: null, enabled: true }, refusedBy: 'mode_shadow' });
    expect(start?.inputs.dispatch?.headroom).toEqual([{ group: null, phase: 'L2', envelopeA: 8.6, remainingA: 8.6 }]);
    expect(start?.inputs.dispatch?.settling).toBe('open');
    expect(unresponsive?.inputs.dispatch?.verification).toEqual({ expected: 1700, measured: 12, unit: 'watts', verdict: 'mismatch' });
    expect(forced).toMatchObject({ origin: 'override', group: 'carport', target: { relays: null, setpointA: 6 } });
    expect(forced?.inputs.dispatch?.override).toBe('force_off');
    expect(forced?.inputs.dispatch?.inFlight).toContainEqual({ group: 'carport', phase: 'L1', currentA: 0 });
  });

  it('accepts a group decision that names its group', () => {
    const parsed = parseDecisionsPage(payload((p) => Object.assign(p.items[3], { scope: 'group', group: 'carport' })));

    expect(parsed.items[3]).toMatchObject({ scope: 'group', group: 'carport' });
  });

  // 0.3.x wrote active_unavailable_before_stage3; its lines stay in the log for the retention period
  // and must still read, or the whole decision list would turn "unavailable" after an update.
  it.each(MODE_NOTES)('accepts the mode note %s, the 0.3.x one included', (note) => {
    const parsed = parseDecisionsPage(
      payload(
        (p) =>
          (at(p, 'mode_resolved').inputs.mode = {
            ceiling: 'active',
            note,
            controlAllowed: null,
            configurationValid: null,
            stateConfirmed: null,
            missing: [],
            source: null,
          }),
      ),
    );

    expect(parsed.items.find((d) => d.action === 'mode_resolved')?.inputs.mode?.note).toBe(note);
  });

  // 0.7.0: a mode decision names the control switch, who set it, and every condition that kept the
  // mode in shadow; lines of older versions carry nulls and an empty list and still read.
  it('reads the control switch and the missing conditions of a mode decision', () => {
    const parsed = parseDecisionsPage(
      payload(
        (p) =>
          (at(p, 'mode_resolved').inputs.mode = {
            ceiling: 'active',
            note: 'none',
            controlAllowed: false,
            configurationValid: true,
            stateConfirmed: false,
            missing: ['control_off', 'state_unconfirmed'],
            source: 'mqtt',
          }),
      ),
    );

    expect(parsed.items.find((d) => d.action === 'mode_resolved')?.inputs.mode).toMatchObject({
      controlAllowed: false,
      stateConfirmed: false,
      missing: ['control_off', 'state_unconfirmed'],
      source: 'mqtt',
    });
  });

  it.each([
    ['an unknown missing condition', (m: any) => (m.missing = ['license_expired'])],
    ['an unknown control source', (m: any) => (m.source = 'browser')],
    ['a missing control field', (m: any) => delete m.controlAllowed],
  ])('refuses a mode decision with %s', (_case, change) => {
    expect(() => parseDecisionsPage(payload((p) => change(at(p, 'mode_resolved').inputs.mode)))).toThrow();
  });

  it('reads the reason codes of list version 4 and the self-test origin', () => {
    const parsed = parseDecisionsPage(
      payload((p) => Object.assign(at(p, 'mode_resolved'), { reason: 'change_limit', refusedBy: 'foreign_load', origin: 'self_test' })),
    );

    expect(parsed.items.find((d) => d.action === 'mode_resolved')).toMatchObject({ reason: 'change_limit', refusedBy: 'foreign_load', origin: 'self_test' });
  });
});

// Stage 4: every decision carries its kind, what the decision log filters by.
describe('parseDecisionsPage, decision kinds', () => {
  it('keeps the kind the add-on assigned: action first, then origin', () => {
    const items = parseDecisionsPage(payload()).items;

    expect(items.find((d) => d.action === 'foreign_load_detected')?.kind).toBe('foreign_load');
    expect(items.find((d) => d.action === 'marked_unresponsive')?.kind).toBe('verification');
    expect(items.find((d) => d.action === 'switch_on')?.kind).toBe('dispatch');
    expect(items.find((d) => d.action === 'mode_resolved')?.kind).toBe('system');
    expect(items[0]?.kind).toBe('protection');
  });

  it.each(DECISION_KINDS)('accepts the kind %s', (kind) => {
    expect(parseDecisionsPage(payload((p) => (p.items[0].kind = kind))).items[0]?.kind).toBe(kind);
  });

  it.each([
    ['an unknown kind', (p: any) => (p.items[0].kind = 'guard')],
    ['a missing kind', (p: any) => delete p.items[0].kind],
  ])('rejects %s', (_case, change) => {
    expect(() => parseDecisionsPage(payload(change))).toThrow();
  });
});

// BAL-07: a dispatch decision carries the hourly balance it was made beside; lines written before
// it, and steps without energy counters, carry none.
describe('parseDecisionsPage, hourly balance inputs', () => {
  it('keeps the balance, free power, load, projection, band and time to :00 of the step', () => {
    const parsed = parseDecisionsPage(payload());
    const unresponsive = parsed.items.find((d) => d.action === 'marked_unresponsive');

    expect(unresponsive?.inputs.dispatch?.balance).toEqual({ balanceWh: 240.5, freeW: 3100, loadW: 1650, projectionWh: 410.25, loWh: 150, hiWh: 525, tRemS: 1320, burning: false });
  });

  it('has no balance on a dispatch decision of a step without energy counters', () => {
    const start = parseDecisionsPage(payload()).items.find((d) => d.action === 'switch_on');

    expect(start?.inputs.dispatch?.balance).toBeNull();
  });

  it('keeps an unknown figure as null, never as zero', () => {
    const parsed = parseDecisionsPage(payload((p) => Object.assign(at(p, 'marked_unresponsive').inputs.dispatch.balance, { balanceWh: null, projectionWh: null, loWh: null, hiWh: null, tRemS: 0 })));
    const balance = parsed.items.find((d) => d.action === 'marked_unresponsive')?.inputs.dispatch?.balance;

    expect(balance).toMatchObject({ balanceWh: null, projectionWh: null, loWh: null, hiWh: null, tRemS: 0 });
  });

  it.each([
    ['a balance figure sent as text', (p: any) => (at(p, 'marked_unresponsive').inputs.dispatch.balance.freeW = '3100')],
    ['a missing time to :00', (p: any) => delete at(p, 'marked_unresponsive').inputs.dispatch.balance.tRemS],
    ['a missing burning flag', (p: any) => delete at(p, 'marked_unresponsive').inputs.dispatch.balance.burning],
    ['a missing balance key', (p: any) => delete at(p, 'marked_unresponsive').inputs.dispatch.balance],
  ])('rejects %s', (_case, change) => {
    expect(() => parseDecisionsPage(payload(change))).toThrow();
  });
});
