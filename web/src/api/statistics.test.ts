import { describe, expect, it } from 'vitest';
import { parseStatistics } from './statistics';
import sample from './statistics.sample.json';

// The sample is what GET api/statistics?from=09:20&to=09:30 wrote at the end of the synthetic
// morning's charging hour (generated from the real serializer by PresentationSpaContractTests).
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

describe('parseStatistics', () => {
  it('accepts the documented payload with its range, last hour, devices and counters', () => {
    const parsed = parseStatistics(payload());

    expect(parsed.bucketMinutes).toBe(5);
    expect(parsed.buckets.map((b) => b.start.toISOString())).toEqual(['2026-10-22T09:20:00.000Z', '2026-10-22T09:25:00.000Z']);
    expect(parsed.range?.buckets).toBe(2);
    expect(parsed.devices.find((d) => d.id === 'ev_charger')).toMatchObject({ name: 'Ładowarka go-e', valuePerKwh: 2.03 });
    expect(parsed.lifetime.sheds).toBe(0);
    expect(parsed.lifetime.wouldSheds).toBeGreaterThan(0);
    expect(parsed.stateFile.status).toBe('fresh');
  });

  it('keeps where the PV went summing to the generation of each bucket', () => {
    for (const bucket of parseStatistics(payload()).buckets) {
      const t = bucket.totals;
      const devices = t.devices.reduce((sum, d) => sum + d.pvKwh, 0);
      expect(t.pv.houseKwh + t.pv.storageKwh + t.pv.exportKwh + t.pv.unattributedKwh + devices).toBeCloseTo(t.generationKwh, 9);
    }
  });

  it.each([
    ['null', () => null],
    ['a negative energy', () => payload((p) => (p.buckets[0].totals.gridImportKwh = -0.1))],
    ['a fraction of a shed', () => payload((p) => (p.buckets[0].totals.sheds = 0.5))],
    ['buckets out of order', () => payload((p) => p.buckets.reverse())],
    ['an unknown file status', () => payload((p) => (p.stateFile.status = 'broken'))],
    ['a missing lifetime key', () => payload((p) => delete p.lifetime.supplyOutages)],
    ['a range that ends before it starts', () => payload((p) => (p.range.to = '2026-10-22T09:00:00+00:00'))],
    ['a zero bucket length', () => payload((p) => (p.bucketMinutes = 0))],
    ['a missing range key', () => payload((p) => delete p.range)],
  ])('rejects %s', (_case, make) => {
    expect(() => parseStatistics(make())).toThrow();
  });
});
