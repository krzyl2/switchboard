import { describe, expect, it } from 'vitest';
import { ENERGY_CLASSES, PRICE_BASES, PRICE_KINDS, SAVINGS_BASES, SETTLEMENT_MODELS, VALUE_BASES, VALUE_FOLLOWS, parseEconomics } from './economics';
import sample from './economics.sample.json';

// The sample is what GET api/economics wrote for a configured, zoned tariff with savings, a range and
// a running period (EconomicsSpaContractTests compares its keys with the endpoint's and the closed
// lists with the backend's enums), so these tests parse what the add-on sends. The cards must never
// show a price, a zone or a saving the add-on did not report: anything outside the contract makes
// them say nothing instead of guessing.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

describe('parseEconomics', () => {
  it('accepts the documented payload', () => {
    const parsed = parseEconomics(payload());

    expect(parsed).toMatchObject({
      configured: true,
      currency: 'EUR',
      tariff: { kind: 'zoned', zone: 'offpeak', importPrice: 0.6, priceBasis: 'zone', nextZone: 'afternoon_peak', signal: null, fallbackInForce: false },
      settlement: { model: 'hourly_netting', exportPrice: { kind: 'flat', perKwh: 0.35, priceBasis: 'flat' } },
      holidaysListStale: false,
      issues: [],
    });
    expect(parsed.tariff?.nextChangeAt?.toISOString()).toBe('2026-10-26T15:00:00.000Z');
    expect(parsed.devices?.map((d) => [d.id, d.sources, d.energyClass, d.valueBasis])).toEqual([
      ['heater_l1', 'pv_only', 'none', 'static'],
      ['ev_charger', 'pv_and_zone:offpeak', 'zone', 'followed'],
    ]);
    expect(parsed.savings).toMatchObject({ basis: 'against the controlled loads being off', currency: 'EUR', model: 'hourly_netting' });
    expect(parsed.savings?.today.periods).toBe(3);
    expect(parsed.savings?.running?.start.toISOString()).toBe('2026-10-26T12:00:00.000Z');
  });

  it('keeps a negative device total as it is: it is shown, never hidden', () => {
    const car = parseEconomics(payload()).savings!.today.devices.find((d) => d.id === 'ev_charger')!;

    expect(car.savings).toBeLessThan(0);
  });

  it('accepts the answer without an economics section: nothing is configured, there are no savings', () => {
    const parsed = parseEconomics(
      payload((p) => Object.assign(p, { configured: false, currency: null, tariff: null, settlement: null, devices: [], savings: null })),
    );

    expect(parsed).toMatchObject({ configured: false, currency: null, tariff: null, settlement: null, savings: null });
  });

  it('accepts a dynamic price with its signal, at the fallback, and without a next change', () => {
    const parsed = parseEconomics(
      payload((p) =>
        Object.assign(p.tariff, {
          kind: 'dynamic',
          priceBasis: 'fallback',
          fallbackInForce: true,
          nextChangeAt: null,
          nextZone: null,
          signal: { name: 'price_import', state: 'stale', detail: 'max_age_exceeded', perKwh: null, ageS: 3600 },
        }),
      ),
    );

    expect(parsed.tariff).toMatchObject({ kind: 'dynamic', priceBasis: 'fallback', fallbackInForce: true, nextChangeAt: null, signal: { name: 'price_import', state: 'stale', ageS: 3600 } });
  });

  it('accepts a savings payload while no period is counted yet and without a running period', () => {
    const parsed = parseEconomics(payload((p) => Object.assign(p.savings, { since: null, range: null, running: null, today: { ...p.savings.today, periods: 0, devices: [] } })));

    expect(parsed.savings).toMatchObject({ since: null, range: null, running: null });
  });

  it.each(PRICE_BASES)('accepts the price basis %s', (basis) => {
    expect(parseEconomics(payload((p) => (p.tariff.priceBasis = basis))).tariff?.priceBasis).toBe(basis);
  });

  it.each(PRICE_KINDS)('accepts the price kind %s', (kind) => {
    expect(parseEconomics(payload((p) => (p.tariff.kind = kind))).tariff?.kind).toBe(kind);
  });

  it.each(VALUE_BASES)('accepts the value basis %s', (basis) => {
    expect(parseEconomics(payload((p) => (p.devices[0].valueBasis = basis))).devices?.[0]?.valueBasis).toBe(basis);
  });

  it.each(VALUE_FOLLOWS)('accepts value_follows %s', (follows) => {
    expect(parseEconomics(payload((p) => (p.devices[0].valueFollows = follows))).devices?.[0]?.valueFollows).toBe(follows);
  });

  it.each(SETTLEMENT_MODELS)('accepts the settlement model %s', (model) => {
    expect(parseEconomics(payload((p) => (p.settlement.model = model))).settlement?.model).toBe(model);
  });

  it.each(ENERGY_CLASSES)('accepts the energy class %s', (energyClass) => {
    expect(parseEconomics(payload((p) => (p.devices[0].class = energyClass))).devices?.[0]?.energyClass).toBe(energyClass);
  });

  it('accepts a device before any dispatch step: its class is null', () => {
    expect(parseEconomics(payload((p) => (p.devices[0].class = null))).devices?.[0]?.energyClass).toBeNull();
  });

  it('keeps the closed lists to what the backend sends today', () => {
    expect([...SAVINGS_BASES]).toEqual(['against the controlled loads being off']);
    expect([...ENERGY_CLASSES].sort()).toEqual(['none', 'pv', 'zone']);
  });

  it.each([
    ['an unknown price basis', (p: any) => (p.tariff.priceBasis = 'estimated')],
    ['an unknown price kind', (p: any) => (p.tariff.kind = 'tiered')],
    ['an unknown export price basis', (p: any) => (p.settlement.exportPrice.priceBasis = 'cached')],
    ['an unknown value basis', (p: any) => (p.devices[0].valueBasis = 'guessed')],
    ['an unknown value_follows', (p: any) => (p.devices[0].valueFollows = 'spot_price')],
    ['an unknown settlement model', (p: any) => (p.settlement.model = 'net_metering')],
    ['an unknown energy class', (p: any) => (p.devices[0].class = 'grid')],
    ['an unknown savings basis', (p: any) => (p.savings.basis = 'against last year')],
    ['an unknown savings model', (p: any) => (p.savings.model = 'monthly')],
    ['an unknown signal state', (p: any) => (p.tariff.signal = { name: 'price_import', state: 'warm', detail: 'none', perKwh: 1, ageS: 1 })],
    ['sources outside the grammar', (p: any) => (p.devices[0].sources = 'grid_only')],
    ['a zone source without a zone', (p: any) => (p.devices[0].sources = 'pv_and_zone:')],
    ['a price sent as text', (p: any) => (p.tariff.importPrice = '0.60')],
    ['an export price sent as text', (p: any) => (p.settlement.exportPrice.perKwh = 'NaN')],
    ['a count that is negative', (p: any) => (p.savings.today.periods = -1)],
    ['a count that is fractional', (p: any) => (p.savings.today.fallbackPeriods = 1.5)],
    ['a missing key in a tariff', (p: any) => delete p.tariff.fallbackInForce],
    ['a missing class key on a device', (p: any) => delete p.devices[0].class],
    ['a missing savings key', (p: any) => delete p.savings.retained],
    ['a missing fallback flag on savings', (p: any) => delete p.savings.today.fallback],
    ['a device saving sent as text', (p: any) => (p.savings.today.devices[0].savings = '-0.4')],
    ['a malformed time', (p: any) => (p.tariff.nextChangeAt = 'at 15:00')],
    ['a malformed since', (p: any) => (p.savings.since = 'yesterday')],
    ['a currency that is not text', (p: any) => (p.currency = 5)],
    ['a missing currency key', (p: any) => delete p.currency],
    ['an issue without its message', (p: any) => (p.issues = [{ code: 'zone_gap', path: 'economics.tariff' }])],
  ])('rejects %s', (_case, change) => {
    expect(() => parseEconomics(payload(change))).toThrow();
  });

  it('rejects an answer that is not an object', () => {
    expect(() => parseEconomics('nope')).toThrow();
    expect(() => parseEconomics(null)).toThrow();
  });

  it('refuses a payload where devices is neither a list nor null', () => {
    expect(() => parseEconomics(payload((p) => (p.devices = { heater_l1: {} })))).toThrow();
  });
});
