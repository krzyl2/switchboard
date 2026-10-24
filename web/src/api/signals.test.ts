import { describe, expect, it } from 'vitest';
import { parseSignals } from './signals';
import sample from './signals.sample.json';

// The sample is the shape GET api/signals really writes: SignalsSpaContractTests on the .NET side
// compares its keys with the endpoint's output, so these tests parse what the add-on sends.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// The page must never display a signal state the add-on did not report, so anything outside the
// exact contract is rejected and the page shows "signals unavailable" instead of a guess.
describe('parseSignals', () => {
  it('accepts the documented payload and keeps every signal', () => {
    const parsed = parseSignals(payload());

    expect(parsed.source).toEqual({ kind: 'replay', status: 'connected' });
    expect(parsed.signals).toHaveLength(sample.signals.length);
    expect(parsed.phases.map((p) => p.phase)).toEqual(['L1', 'L2', 'L3']);
    expect(parsed.generatedAt.toISOString()).toBe('2026-10-21T10:21:21.019Z');
    expect(parsed.configuration.warnings[0]?.code).toBe('max_age_short_for_probe');
  });

  it('keeps a state signal as text and a measured signal as a number in its internal unit', () => {
    const parsed = parseSignals(payload());

    const car = parsed.signals.find((s) => s.name === 'car_connected');
    expect([car?.value, car?.unit]).toEqual(['on', null]);
    const current = parsed.signals.find((s) => s.name === 'grid_current_l1');
    expect([current?.value, current?.unit]).toEqual([-5.466, 'A']);
  });

  it('drops fields the page does not know about', () => {
    const parsed = parseSignals(payload((p) => (p.signals[0].token = 'must-not-travel')));

    expect(parsed.signals[0]).not.toHaveProperty('token');
  });

  it.each([
    ['an unknown source kind', (p: any) => (p.source.kind = 'mqtt')],
    ['an unknown source status', (p: any) => (p.source.status = 'online')],
    ['an unknown configuration state', (p: any) => (p.configuration.state = 'broken')],
    ['an issue code outside the closed list', (p: any) => (p.configuration.warnings[0].code = 'made_up')],
    ['an unknown topology', (p: any) => (p.connection.topology = 'split_phase')],
    ['an unknown breaker characteristic', (p: any) => (p.connection.characteristic = 'D')],
    ['a phase that does not exist', (p: any) => (p.phases[0].phase = 'L4')],
    ['an unknown invalid reason', (p: any) => (p.phases[1].invalidReason = 'tired')],
    ['an unknown phase direction', (p: any) => (p.phases[0].direction = 'sideways')],
    ['an unknown signal kind', (p: any) => (p.signals[0].kind = 'temperature')],
    ['an unknown role', (p: any) => (p.signals[0].role = 'heater')],
    ['a signal state in the wrong case', (p: any) => (p.signals[0].state = 'Fresh')],
    ['an unknown signal detail', (p: any) => (p.signals[0].detail = 'odd')],
    ['an unknown direction source', (p: any) => (p.signals[1].direction.source = 'guess')],
    ['an unknown signal direction', (p: any) => (p.signals[1].direction.direction = 'both')],
  ])('rejects unknown enum strings: %s', (_case, change) => {
    expect(() => parseSignals(payload(change))).toThrow();
  });

  it.each([
    ['null', () => null],
    ['an array', () => []],
    ['a missing key that may be null', () => payload((p) => delete p.signals[5].lastReported)],
    ['a missing connection key', () => payload((p) => delete p.connection)],
    ['a number sent as text', () => payload((p) => (p.signals[1].value = '-5.466'))],
    ['text for a current signal', () => payload((p) => (p.signals[1].value = 'on'))],
    ['a number for a state signal', () => payload((p) => (p.signals[7].value = 1))],
    ['a unit on a state signal', () => payload((p) => (p.signals[7].unit = 'A'))],
    // A value in kW labelled as the internal W would be read 1000x too small.
    ['a unit other than the kind\'s internal unit', () => payload((p) => (p.signals[0].unit = 'kW'))],
    ['a number without a unit', () => payload((p) => (p.signals[0].unit = null))],
    ['a unit without a number', () => payload((p) => (p.signals[6].unit = 'V'))],
    ['a negative count', () => payload((p) => (p.signals[4].rejectedCount = -1))],
    ['a fractional sequence', () => payload((p) => (p.sequence = 4.5))],
    ['a timestamp that is not one', () => payload((p) => (p.signals[0].lastReported = 'yesterday'))],
    ['a missing residual time', () => payload((p) => delete p.phases[0].residualAt)],
    ['a residual time that is not one', () => payload((p) => (p.phases[0].residualAt = 'just now'))],
    ['valid meter data that still names a reason', () => payload((p) => (p.phases[0].invalidReason = 'current_stale'))],
    ['invalid meter data without a reason', () => payload((p) => (p.phases[1].invalidReason = null))],
    ['a boolean sent as text', () => payload((p) => (p.phases[0].protectionDataValid = 'true'))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseSignals(make())).toThrow();
  });
});
