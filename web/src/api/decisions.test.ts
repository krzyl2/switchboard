import { describe, expect, it } from 'vitest';
import { REASON_CODES, SAFE_STATE_CAUSES, parseDecisionsPage } from './decisions';
import sample from './decisions.sample.json';

// The sample has the keys GET api/decisions really writes (GuardSpaContractTests compares them).
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
    ['an unknown scope', (p: any) => (p.items[0].scope = 'group')],
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
