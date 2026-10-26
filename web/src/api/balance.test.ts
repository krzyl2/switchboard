import { describe, expect, it } from 'vitest';
import { BALANCE_UNKNOWN_REASONS, parseBalance } from './balance';
import sample from './balance.sample.json';

// The sample is what GET api/balance wrote for a known hour (BalanceEndpointTests compares its keys
// with the endpoint's), so these tests parse what the add-on sends.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

// The dashboard must never show a balance, projection or band the add-on did not report: an unknown
// is a reason, never a 0, and anything outside the contract makes the card say "unavailable".
describe('parseBalance', () => {
  it('accepts the documented payload', () => {
    expect(parseBalance(payload())).toMatchObject({
      configured: true,
      wh: 240.5,
      known: true,
      freeW: 3100,
      loadW: 1650,
      projectionWh: 410.25,
      band: { loWh: 150, hiWh: 525, burning: false },
      ruleSecondsToNextHour: 1320,
    });
    expect(parseBalance(payload()).hourStart?.toISOString()).toBe('2026-10-26T12:00:00.000Z');
  });

  it('keeps an unknown as nulls with its reason', () => {
    const parsed = parseBalance(
      payload((p) => Object.assign(p, { wh: null, reason: 'stale', known: false, unknownReason: 'stale', freeW: null, loadW: null, projectionWh: null, band: null, ruleSecondsToNextHour: 0 })),
    );

    expect(parsed).toMatchObject({ wh: null, known: false, unknownReason: 'stale', projectionWh: null, band: null });
  });

  it('accepts the answer without energy counters', () => {
    const parsed = parseBalance(
      payload((p) => Object.assign(p, { configured: false, wh: null, hourStart: null, secondsToNextHour: null, known: false, freeW: null, loadW: null, projectionWh: null, band: null, ruleSecondsToNextHour: null, ruleAt: null })),
    );

    expect(parsed.configured).toBe(false);
  });

  it.each(BALANCE_UNKNOWN_REASONS)('accepts the unknown reason %s', (reason) => {
    expect(parseBalance(payload((p) => (p.unknownReason = reason))).unknownReason).toBe(reason);
  });

  it.each([
    ['an unknown reason', (p: any) => (p.reason = 'tired')],
    ['an unknown unknown-reason', (p: any) => (p.unknownReason = 'tired')],
    ['a number sent as text', (p: any) => (p.projectionWh = '410')],
    ['a band without its burning flag', (p: any) => delete p.band.burning],
    ['a missing key', (p: any) => delete p.ruleAt],
    ['a malformed time', (p: any) => (p.hourStart = 'noon')],
  ])('rejects %s', (_case, change) => {
    expect(() => parseBalance(payload(change))).toThrow();
  });

  it('rejects anything that is not an object', () => {
    expect(() => parseBalance(null)).toThrow();
    expect(() => parseBalance([])).toThrow();
  });
});
