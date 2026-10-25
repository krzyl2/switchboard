import { describe, expect, it } from 'vitest';
import { parseExcursions } from './excursions';
import sample from './excursions.sample.json';

// The sample holds the synthetic morning's L1 excursion (09:20, a kettle while the car charges),
// generated from the real serializer by PresentationSpaContractTests.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

describe('parseExcursions', () => {
  it('accepts the documented payload: every phase, the L1 excursion with its samples and decisions', () => {
    const parsed = parseExcursions(payload());

    expect(parsed.phases.map((p) => p.phase)).toEqual(['L1', 'L2', 'L3']);
    const l1 = parsed.phases[0]!.worst!;
    expect(l1.peakA).toBeGreaterThan(l1.reserveThresholdA);
    expect(l1.samples.some((s) => s.at.getTime() === l1.peakAt.getTime())).toBe(true);
    expect(l1.decisions.map((d) => d.action)).toContain('set_setpoint');
    expect(l1.decisions.every((d) => !d.commanded)).toBe(true);
    expect(parsed.phases[1]).toEqual({ phase: 'L2', last: null, worst: null });
  });

  it.each([
    ['null', () => null],
    ['an active excursion with an end', () => payload((p) => (p.phases[0].last.active = true))],
    ['samples out of time order', () => payload((p) => p.phases[0].last.samples.reverse())],
    ['an unknown action', () => payload((p) => (p.phases[0].last.decisions[0].action = 'turn_off'))],
    ['a reason outside the list', () => payload((p) => (p.phases[0].last.decisions[0].reason = 'kettle'))],
    ['a missing key', () => payload((p) => delete p.phases[0].last.decimated)],
  ])('rejects %s', (_case, make) => {
    expect(() => parseExcursions(make())).toThrow();
  });
});
