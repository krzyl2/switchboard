import { describe, expect, it } from 'vitest';
import { GENERATION_NOTES, POWER_SOURCES, POWER_UNKNOWN_REASONS, REACTION_TIMINGS, SEGMENT_KINDS, parsePower } from './power';
import sample from './power.sample.json';

// The sample is what GET api/power wrote over the synthetic morning at 09:21, the car charging and a
// kettle on L1 (PresentationSpaContractTests generates it from the real serializer and compares
// its keys with the endpoint's), so these tests parse what the add-on sends.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// The dashboard must never show a power the add-on did not report: an unknown figure is a reason,
// never a 0, and anything outside the contract makes the screen say "unavailable" instead.
describe('parsePower', () => {
  it('accepts the documented payload with every phase, device and banner', () => {
    const parsed = parsePower(payload());

    expect(parsed.phases.map((p) => [p.phase, p.overReserve])).toEqual([
      ['L1', true],
      ['L2', false],
      ['L3', false],
    ]);
    expect(parsed.devices.map((d) => [d.id, d.source])).toEqual([
      ['heater_l1', 'rated_model'],
      ['heater_l2', 'rated_model'],
      ['heater_l3', 'rated_model'],
      ['ev_charger', 'measured'],
    ]);
    const banner = parsed.banners[0]!;
    expect(banner).toMatchObject({ phase: 'L1', timing: 'next_boundary', reductionRequested: true });
    // Reason list version 3: the planned reduction under a detected foreign load carries foreign_load.
    expect(banner.planned[0]).toMatchObject({ device: 'ev_charger', origin: 'dispatch', reason: 'foreign_load' });
    expect(banner.planned[0]?.to.setpointA).toBeLessThan(banner.planned[0]?.from?.setpointA ?? 0);
    expect(parsed.at.toISOString()).toBe('2026-10-22T09:21:02.000Z');
  });

  it('keeps the balance identity of every phase the add-on reports: generation + import = load = controlled + storage + uncontrolled', () => {
    for (const phase of parsePower(payload()).phases) {
      const b = phase.balance;
      const storage = b.storage.unknown === 'not_declared' ? 0 : b.storage.w!;
      expect(b.netImport.w! + b.generation.w!).toBeCloseTo(b.load.w!, 6);
      expect(b.controlled.w! + storage + b.uncontrolled.w!).toBeCloseTo(b.load.w!, 6);
    }
  });

  it('keeps an unknown figure as a reason without a number', () => {
    const parsed = parsePower(payload());

    expect(parsed.total.storage).toEqual({ w: null, unknown: 'not_declared', reportedAt: null });
  });

  it.each([
    ['an unknown reason', (p: any) => (p.total.storage.unknown = 'missing')],
    ['an unknown generation note', (p: any) => (p.total.generationNote = 'asleep')],
    ['an unknown segment kind', (p: any) => (p.phases[0].bar[0].kind = 'house')],
    ['an unknown power source', (p: any) => (p.devices[0].source = 'model')],
    ['an unknown reaction timing', (p: any) => (p.banners[0].timing = 'soon')],
    ['an unknown tier reaction', (p: any) => (p.banners[0].reaction = 'shed')],
    ['an unknown invalid reason', (p: any) => Object.assign(p, { dataValid: false, invalidReason: 'tired' })],
  ])('rejects unknown enum strings: %s', (_case, change) => {
    expect(() => parsePower(payload(change))).toThrow();
  });

  it.each([
    ['null', () => null],
    ['a missing key that may be null', () => payload((p) => delete p.evaluatedAt)],
    ['a figure with a number and a reason', () => payload((p) => (p.total.load.unknown = 'power_not_fresh'))],
    ['a figure with neither', () => payload((p) => (p.total.load.w = null))],
    ['a negative segment', () => payload((p) => (p.phases[0].bar[0].a = -1))],
    ['a device power without a source', () => payload((p) => (p.devices[3].phases[0].source = 'unknown'))],
    ['an unknown device source with a power', () => payload((p) => (p.devices[3].source = 'unknown'))],
    ['valid data that names a reason', () => payload((p) => (p.invalidReason = 'power_not_fresh'))],
    ['a number sent as text', () => payload((p) => (p.phases[0].currentA = '19'))],
    ['a phase that does not exist', () => payload((p) => (p.phases[0].phase = 'L4'))],
  ])('rejects %s', (_case, make) => {
    expect(() => parsePower(make())).toThrow();
  });

  it('accepts the answer before the first step: nothing known, no phase', () => {
    const missing = { w: null, unknown: 'configuration_invalid', reportedAt: null };
    const parsed = parsePower({
      ...(sample as object),
      evaluatedAt: null,
      dataValid: false,
      invalidReason: 'configuration_invalid',
      total: {
        generation: missing,
        generationNote: null,
        netImport: missing,
        load: missing,
        controlled: missing,
        unknownDevices: [],
        storage: missing,
        uncontrolled: missing,
        modelExceedsLoad: false,
      },
      phases: [],
      devices: [],
      banners: [],
    });

    expect([parsed.evaluatedAt, parsed.phases, parsed.total.load.w]).toEqual([null, [], null]);
  });

  it('names the values the backend lists (PresentationSpaContractTests compares them exactly)', () => {
    expect(POWER_UNKNOWN_REASONS).toContain('not_declared');
    expect(GENERATION_NOTES).toEqual(['asleep_counted_as_zero']);
    expect(SEGMENT_KINDS).toContain('foreign');
    expect(POWER_SOURCES).toContain('rated_model');
    expect(REACTION_TIMINGS).toContain('cannot_reduce_exporting');
  });
});
