import { describe, expect, it } from 'vitest';
import { parseGuard } from './guard';
import sample from './guard.sample.json';

// The sample has the keys GET api/guard really writes: GuardSpaContractTests on the .NET side
// compares them with the endpoint's output, so these tests parse what the add-on sends.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// The limits page must never show a limit, a budget or a safe state the add-on did not report: a
// payload outside the exact contract is rejected and the page says "limits unavailable" instead.
describe('parseGuard', () => {
  it('accepts the documented payload and keeps every phase, device, tier and decision', () => {
    const parsed = parseGuard(payload());

    expect(parsed.phases.map((p) => [p.phase, p.state])).toEqual([
      ['L1', 'shedding'],
      ['L2', 'safe_state'],
      ['L3', 'normal'],
    ]);
    expect(parsed.devices.map((d) => d.id)).toEqual(['heater_l1', 'ev_charger', 'heater_l2', 'boiler', 'heat_pump']);
    expect(parsed.breaker?.tiers).toHaveLength(6);
    expect(parsed.decisions).toHaveLength(sample.decisions.length);
    expect(parsed.safeState).toEqual({
      active: true,
      scope: 'phases',
      reason: 'guard_stale_signal',
      phases: ['L2'],
      since: new Date('2026-10-24T09:59:40.011Z'),
    });
    expect(parsed.at.toISOString()).toBe('2026-10-24T10:00:00.004Z');
  });

  it('keeps a stage that is not fresh without an applied state, and its last known one apart', () => {
    const boiler = parseGuard(payload()).devices.find((d) => d.id === 'boiler');

    expect(boiler?.stages[1]).toMatchObject({ stage: 2, fresh: false, applied: null, invalidReason: 'stale', limit: 'off' });
    expect(boiler?.stages[1]?.lastKnown?.applied).toBe('on');
  });

  it('accepts the guard before its first evaluation: no breaker, no phase, no device', () => {
    const parsed = parseGuard(
      payload((p) => {
        Object.assign(p, { breaker: null, phases: [], devices: [], decisions: [] });
        p.safeState = { active: true, scope: 'all', reason: 'safe_state', phases: [], since: p.at };
      }),
    );

    expect([parsed.breaker, parsed.phases, parsed.devices]).toEqual([null, [], []]);
  });

  it('drops fields the page does not know about', () => {
    const parsed = parseGuard(payload((p) => (p.devices[0].actuator = 'switch.must_not_travel')));

    expect(parsed.devices[0]).not.toHaveProperty('actuator');
  });

  it.each([
    ['an unknown effective mode', (p: any) => (p.mode.effective = 'manual')],
    ['an unknown mode note', (p: any) => (p.mode.note = 'soon')],
    ['an unknown safe-state scope', (p: any) => (p.safeState.scope = 'some')],
    ['a safe-state reason outside the code list', (p: any) => (p.safeState.reason = 'meter_offline')],
    ['an unknown kill switch source', (p: any) => (p.killSwitch.source = 'button')],
    ['an unknown characteristic', (p: any) => (p.breaker.characteristic = 'D')],
    ['an unknown tier reaction', (p: any) => (p.breaker.tiers[2].reaction = 'reduce_soon')],
    ['an unknown phase state', (p: any) => (p.phases[0].state = 'overloaded')],
    ['an unknown budget trend', (p: any) => (p.phases[0].budgetTrend = 'draining')],
    ['an unknown invalid reason', (p: any) => (p.phases[1].invalidReason = 'tired')],
    ['an unknown direction', (p: any) => (p.phases[2].direction = 'sideways')],
    ['an unknown device type', (p: any) => (p.devices[0].type = 'onoff')],
    ['an unknown device guard state', (p: any) => (p.devices[0].guardState = 'off')],
    ['a device reason outside the code list', (p: any) => (p.devices[0].reason = 'GuardTierExceeded')],
    ['an unknown stage limit', (p: any) => (p.devices[0].stages[0].limit = 'stopped')],
    ['an unknown relay state', (p: any) => (p.devices[0].stages[0].applied = 'ON')],
    ['an unknown device state reason', (p: any) => (p.devices[3].stages[1].invalidReason = 'old')],
    ['an unknown state file status', (p: any) => (p.stateFile.status = 'broken')],
  ])('rejects unknown enum strings: %s', (_case, change) => {
    expect(() => parseGuard(payload(change))).toThrow();
  });

  it.each([
    ['null', () => null],
    ['an array', () => []],
    ['a missing key that may be null', () => payload((p) => delete p.breaker)],
    ['a missing phase key', () => payload((p) => delete p.phases[0].headroomW)],
    ['a number sent as text', () => payload((p) => (p.phases[0].currentA = '33'))],
    ['a boolean sent as text', () => payload((p) => (p.killSwitch.engaged = 'false'))],
    ['a timestamp that is not one', () => payload((p) => (p.at = 'now'))],
    ['a negative dropped count', () => payload((p) => (p.decisionLog.dropped = -1))],
    // A "valid" next to a reason, or valid data without a current, must never reach the screen.
    ['valid meter data that still names a reason', () => payload((p) => (p.phases[0].invalidReason = 'current_stale'))],
    ['valid meter data without a current', () => payload((p) => (p.phases[0].currentA = null))],
    ['a band outside the breaker profile', () => payload((p) => (p.phases[0].band = 6))],
    ['a band without a breaker profile', () => payload((p) => (p.breaker = null))],
    ['an open band that is not the last', () => payload((p) => (p.breaker.tiers[2].upTo = null))],
    ['a last band with a limit', () => payload((p) => (p.breaker.tiers[5].upTo = 2))],
    ['an active safe state without a scope', () => payload((p) => (p.safeState.scope = 'none'))],
    ['a scope on an inactive safe state', () => payload((p) => (p.safeState.active = false))],
    // A stale "off" must never look like a current one.
    ['a stage that is not fresh but shows an applied state', () => payload((p) => (p.devices[3].stages[1].applied = 'off'))],
    ['a fresh stage that names a reason', () => payload((p) => (p.devices[0].stages[0].invalidReason = 'stale'))],
    ['a fresh stage without an applied state', () => payload((p) => (p.devices[0].stages[0].applied = null))],
    ['a stage with a relay state and a setpoint', () => payload((p) => (p.devices[0].stages[0].setpointA = 6))],
    ['stage number zero', () => payload((p) => (p.devices[0].stages[0].stage = 0))],
    ['shed rank zero', () => payload((p) => (p.devices[0].shedRank = 0))],
    ['a device phase that does not exist', () => payload((p) => (p.devices[0].phases = ['L4']))],
    ['a decision that does not parse', () => payload((p) => (p.decisions[0].reason = 'made_up'))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseGuard(make())).toThrow();
  });
});

// Stage 4: the tier table in kilowatts for this installation and the budget's recovery time.
describe('parseGuard, Stage 4 breaker fields', () => {
  it('keeps the limit of each band in kW and the recovery time', () => {
    const breaker = parseGuard(payload()).breaker!;

    expect(breaker.budgetRecoveryTimeS).toBe(600);
    expect(breaker.tiers.map((t) => t.upToW)).toEqual([5175, 5750, 6497.5, 7187.5, 8337.5, null]);
  });

  it.each([
    ['a missing recovery time', (p: any) => delete p.breaker.budgetRecoveryTimeS],
    ['a missing kW limit', (p: any) => delete p.breaker.tiers[0].upToW],
  ])('rejects %s', (_case, change) => {
    expect(() => parseGuard(payload(change))).toThrow();
  });
});

// Stage C: the kill switch says whether it switches anything now, and whether it is held after an
// untrusted state.json; a page that hides either would misstate what the add-on does.
describe('parseGuard, kill switch of the control stage', () => {
  it('reads the lock and whether the kill switch switches devices', () => {
    const parsed = parseGuard(payload((p) => Object.assign(p.killSwitch, { engaged: true, source: 'corrupt_state', locked: true, switchesDevices: true })));

    expect(parsed.killSwitch).toMatchObject({ engaged: true, source: 'corrupt_state', locked: true, switchesDevices: true });
  });

  it.each([
    ['without its lock flag', (p: any) => delete p.killSwitch.locked],
    ['without saying whether it switches devices', (p: any) => delete p.killSwitch.switchesDevices],
    ['with an unknown source', (p: any) => (p.killSwitch.source = 'browser')],
  ])('refuses a kill switch %s', (_case, change) => {
    expect(() => parseGuard(payload(change))).toThrow();
  });
});
