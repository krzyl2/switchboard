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

  // SIG-05: a signal names the devices it makes unsafe while it is not fresh.
  it('keeps the devices that depend on each signal, and the device-state role', () => {
    const parsed = parseSignals(payload());

    expect(parsed.signals.find((s) => s.name === 'grid_current_l1')?.dependentDevices).toEqual(['ev_charger', 'heater_l1']);
    expect(parsed.signals.find((s) => s.name === 'grid_power_l1')?.dependentDevices).toEqual([]);
    const state = parsed.signals.find((s) => s.name === 'device_heater_l2');
    expect([state?.role, state?.kind, state?.dependentDevices]).toEqual(['device_state', 'state', ['heater_l2']]);
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
    ['a missing dependent-device list', () => payload((p) => delete p.signals[1].dependentDevices)],
    ['dependent devices that are not a list', () => payload((p) => (p.signals[1].dependentDevices = 'heater_l1'))],
    ['a dependent device that is not text', () => payload((p) => (p.signals[1].dependentDevices = [1]))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseSignals(make())).toThrow();
  });
});

// Stage C: the runtime derived-sensor check of a hand-written file warns, it never refuses.
describe('parseSignals, configuration warnings', () => {
  it('reads a derived measurement input with its entity and platform', () => {
    const parsed = parseSignals(payload());

    expect(parsed.configurationWarnings[0]).toMatchObject({ code: 'derived_measurement_input', signal: 'grid_current_l1', entity: 'sensor.meter_current_a', platform: 'template' });
  });

  it('reads an unavailable registry check without a signal', () => {
    const parsed = parseSignals(payload((p) => (p.configurationWarnings = [{ code: 'derived_check_unavailable', signal: null, entity: null, platform: null, checkedAt: p.generatedAt }])));

    expect(parsed.configurationWarnings[0]?.code).toBe('derived_check_unavailable');
  });

  it.each([
    ['an unknown warning code', (p: any) => (p.configurationWarnings[0].code = 'looks_odd')],
    ['a missing warning list', (p: any) => delete p.configurationWarnings],
  ])('refuses %s', (_case, change) => {
    expect(() => parseSignals(payload(change))).toThrow();
  });
});

// ECO-10: a price signal travels per kWh in the user's own currency label; its value is a number, never text, and a
// price whose unit is not `<label>/kWh` could be a per-MWh number shown 1000x too large.
describe('parseSignals, price signals', () => {
  const withPrice = (change?: (s: any) => void) =>
    payload((p) => {
      const price = {
        ...p.signals.find((s: any) => s.name === 'grid_power_l1'),
        name: 'rce_price',
        entity: 'sensor.rce_price',
        kind: 'price',
        role: 'price_import',
        phase: null,
        value: 0.45,
        unit: 'PLN/kWh',
        raw: { state: '450', unit: 'PLN/MWh' },
        direction: null,
        dependentDevices: [],
      };
      change?.(price);
      p.signals.push(price);
    });

  it('keeps a price as a number per kWh with the user label, and both price roles', () => {
    const parsed = parseSignals(withPrice());
    const price = parsed.signals.find((s) => s.name === 'rce_price');
    expect([price?.kind, price?.role, price?.value, price?.unit]).toEqual(['price', 'price_import', 0.45, 'PLN/kWh']);

    const exported = parseSignals(withPrice((s) => (s.role = 'price_export')));
    expect(exported.signals.find((s) => s.name === 'rce_price')?.role).toBe('price_export');
  });

  it('accepts a price that is not usable: no number and no unit', () => {
    const parsed = parseSignals(withPrice((s) => Object.assign(s, { value: null, unit: null, state: 'stale', detail: 'max_age_exceeded' })));

    expect(parsed.signals.find((s) => s.name === 'rce_price')?.value).toBeNull();
  });

  it.each([
    ['a per-MWh unit (the add-on converts)', (s: any) => (s.unit = 'PLN/MWh')],
    ['the energy unit of a counter', (s: any) => (s.unit = 'kWh')],
    ['a price without a unit', (s: any) => (s.unit = null)],
    ['a unit without a price', (s: any) => (s.value = null)],
    ['a price sent as text', (s: any) => (s.value = '0.45')],
  ])('rejects %s', (_case, change) => {
    expect(() => parseSignals(withPrice(change))).toThrow();
  });
});

// PLAN-06, 07: forecast and curve signals. A series has no single value and no unit (its content is summarized with points and coverage,
// which is separate from freshness); a day total is a number in kWh; a sun time travels as the instant's text.
describe('parseSignals, forecast and curve signals', () => {
  const base = (p: any) => p.signals.find((s: any) => s.name === 'grid_power_l1');
  const withSignal = (extra: Record<string, unknown>) =>
    payload((p) => {
      p.signals.push({
        ...base(p),
        name: 'forecast',
        entity: 'sensor.forecast',
        phase: null,
        value: null,
        unit: null,
        raw: { state: '3412', unit: null },
        direction: null,
        dependentDevices: [],
        series: null,
        ...extra,
      });
    });
  const series = { valueKind: 'power_w', points: 34, from: '2026-10-23T05:00:00Z', coversUntil: '2026-10-24T14:00:00Z' };

  it('keeps a series with its points and coverage, and no value or unit', () => {
    const parsed = parseSignals(withSignal({ kind: 'series', role: 'pv_forecast', series }));
    const signal = parsed.signals.find((s) => s.name === 'forecast');

    expect([signal?.kind, signal?.role, signal?.value, signal?.unit, signal?.series?.points, signal?.series?.valueKind]).toEqual([
      'series',
      'pv_forecast',
      null,
      null,
      34,
      'power_w',
    ]);
    expect(signal?.series?.coversUntil).toEqual(new Date('2026-10-24T14:00:00Z'));
  });

  it('accepts a series that is not usable: no summary', () => {
    const parsed = parseSignals(withSignal({ kind: 'series', role: 'price_curve_import', state: 'rejected', detail: 'series_too_large' }));

    expect(parsed.signals.find((s) => s.name === 'forecast')?.series).toBeNull();
  });

  it('keeps a day total as kWh and a sun time as text with no unit', () => {
    const total = parseSignals(withSignal({ kind: 'energy_forecast', role: 'pv_forecast_today', value: 4.2, unit: 'kWh' }));
    const sun = parseSignals(withSignal({ kind: 'timestamp', role: 'sun_setting', value: '2026-10-23T15:41:10+00:00' }));

    expect(total.signals.find((s) => s.name === 'forecast')?.value).toBe(4.2);
    expect(sun.signals.find((s) => s.name === 'forecast')?.value).toBe('2026-10-23T15:41:10+00:00');
  });

  it.each([
    ['a series with a number', { kind: 'series', role: 'pv_forecast', value: 1 }],
    ['a series with a unit', { kind: 'series', role: 'pv_forecast', unit: 'W' }],
    ['a series with an unknown value kind', { kind: 'series', role: 'pv_forecast', series: { ...series, valueKind: 'kw' } }],
    ['a series with a negative point count', { kind: 'series', role: 'pv_forecast', series: { ...series, points: -1 } }],
    ['a day total in W', { kind: 'energy_forecast', role: 'pv_forecast_today', value: 4.2, unit: 'W' }],
    ['a sun time with a unit', { kind: 'timestamp', role: 'sun_setting', value: 'x', unit: 's' }],
    ['an unknown role', { kind: 'series', role: 'weather_forecast' }],
    ['an unknown detail', { kind: 'series', role: 'pv_forecast', detail: 'series_half_read' }],
  ])('rejects %s', (_case, extra) => {
    expect(() => parseSignals(withSignal(extra))).toThrow();
  });

  it('requires the series key on every signal', () => {
    expect(() => parseSignals(payload((p) => delete base(p).series))).toThrow();
  });
});
