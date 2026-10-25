import { describe, expect, it } from 'vitest';
import { deviceLabel, parseDevices } from './devices';
import sample from './devices.sample.json';

// The sample is GET api/devices for deploy/examples/switchboard.yaml (generated from the real
// serializer by PresentationSpaContractTests): entity IDs come from the configuration layer.
function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

describe('parseDevices', () => {
  it('accepts the documented payload: the example devices with their entities, names and limits', () => {
    const parsed = parseDevices(payload());

    expect(parsed.configuration).toBe('valid');
    expect(parsed.devices.map((d) => d.id)).toEqual(['heater_l1', 'heater_l2', 'heater_l3', 'ev_charger']);
    const charger = parsed.devices[3]!;
    expect(charger).toMatchObject({ name: 'Ładowarka go-e', type: 'modulating', stateReports: 'on_change', stateMaxAgeS: null });
    expect(charger.stages[0]?.actuator).toBe('number.goe_422752_amp');
    expect(charger.draw?.power[0]).toEqual({ phase: 'L1', entity: 'sensor.goe_422752_nrg_7' });
    expect(charger.range).toEqual({ minA: 6, maxA: 20, stepA: 1, unit: 'A' });
    expect(deviceLabel(parsed.devices, 'heater_l1')).toBe('Grzałka CWU L1');
    expect(deviceLabel(parsed.devices, 'boiler')).toBe('boiler');
  });

  it.each([
    ['null', () => null],
    ['an unknown configuration state', () => payload((p) => (p.configuration = 'broken'))],
    ['devices from an invalid configuration', () => payload((p) => (p.configuration = 'invalid'))],
    ['an unknown state report kind', () => payload((p) => (p.devices[0].stateReports = 'push'))],
    ['an on_change device with an age limit', () => payload((p) => (p.devices[0].stateMaxAgeS = 600))],
    ['an unknown device type', () => payload((p) => (p.devices[0].type = 'onoff'))],
    ['stage number zero', () => payload((p) => (p.devices[0].stages[0].stage = 0))],
  ])('rejects %s', (_case, make) => {
    expect(() => parseDevices(make())).toThrow();
  });
});
