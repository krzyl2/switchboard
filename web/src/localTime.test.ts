import { describe, expect, it } from 'vitest';
import { bucketCount, localClock, localDay, localHourStart, offsetAt } from './localTime';

// Local time exists only at the presentation edge (03-ARCHITECTURE): the add-on sends UTC instants
// and the page builds its local day in an explicit zone, so these tests hold whatever zone the CI
// machine runs in. Daylight saving produces 23- and 25-hour days (07-PITFALLS).
const WARSAW = 'Europe/Warsaw';

describe('localDay', () => {
  it('2026-10-25 in Warsaw is a 25-hour day: 300 buckets of 5 minutes', () => {
    const day = localDay(new Date('2026-10-25T12:00:00Z'), WARSAW);

    expect(day.from.toISOString()).toBe('2026-10-24T22:00:00.000Z');
    expect(day.to.toISOString()).toBe('2026-10-25T23:00:00.000Z');
    expect(bucketCount(day, 5)).toBe(300);
  });

  it('2026-03-29 in Warsaw is a 23-hour day: 276 buckets of 5 minutes', () => {
    const day = localDay(new Date('2026-03-29T12:00:00Z'), WARSAW);

    expect(day.from.toISOString()).toBe('2026-03-28T23:00:00.000Z');
    expect(day.to.toISOString()).toBe('2026-03-29T22:00:00.000Z');
    expect(bucketCount(day, 5)).toBe(276);
  });

  it('an ordinary day is 288 buckets, and the instant just before midnight still belongs to it', () => {
    const day = localDay(new Date('2026-10-23T21:59:59Z'), WARSAW);

    expect(day.from.toISOString()).toBe('2026-10-22T22:00:00.000Z');
    expect(bucketCount(day, 5)).toBe(288);
    expect(localDay(new Date('2026-10-23T22:00:00Z'), WARSAW).from.toISOString()).toBe('2026-10-23T22:00:00.000Z');
  });

  it('the hour repeated at the end of summer time belongs to the same local day', () => {
    // 02:30 local happens twice on 2026-10-25 (00:30 and 01:30 UTC).
    expect(localDay(new Date('2026-10-25T00:30:00Z'), WARSAW)).toEqual(localDay(new Date('2026-10-25T01:30:00Z'), WARSAW));
  });
});

describe('localHourStart and offsets', () => {
  it('starts the local hour on a whole local hour, also in a half-hour zone', () => {
    expect(localHourStart(new Date('2026-10-25T09:47:13.250Z'), WARSAW).toISOString()).toBe('2026-10-25T09:00:00.000Z');
    expect(localHourStart(new Date('2026-10-25T09:47:13Z'), 'Asia/Kolkata').toISOString()).toBe('2026-10-25T09:30:00.000Z');
  });

  it('reads the offset of the zone at each instant', () => {
    expect(offsetAt(new Date('2026-07-01T12:00:00Z'), WARSAW)).toBe(2 * 3600_000);
    expect(offsetAt(new Date('2026-12-01T12:00:00Z'), WARSAW)).toBe(3600_000);
  });

  it('labels a time in the zone, 24-hour', () => {
    expect(localClock(new Date('2026-10-25T13:05:00Z'), 'pl', WARSAW)).toBe('14:05');
  });
});
