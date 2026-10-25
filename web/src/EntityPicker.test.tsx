import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/preact';
import { EntityPicker, matchesFilter, type PickerFilter } from './EntityPicker';
import { parseDiscovery, type DiscoveredEntity } from './api/setup';
import discoverySample from './api/setup-discovery.sample.json';
import { messages } from './i18n';

const t = messages.en;
const entities = parseDiscovery(structuredClone(discoverySample)).entities;
const CURRENT: PickerFilter = { domains: ['sensor'], deviceClasses: ['current'], units: ['A', 'mA'] };
const RELAY: PickerFilter = { domains: ['switch', 'input_boolean'] };

function options(): string[] {
  return [...(screen.getByLabelText('L1 current') as HTMLSelectElement).options].map((o) => o.value);
}

afterEach(cleanup);

// UI-03: pickers are fed by discovery and filtered by domain and device class (or a compatible unit);
// a derived sensor is visible but never selectable (07-PITFALLS); nothing is ever preselected.
describe('entity picker', () => {
  it('offers only current-class sensors for phase rows', () => {
    render(<EntityPicker id="p" label="L1 current" value={null} onChange={() => {}} entities={entities} filter={CURRENT} t={t} />);

    expect(options()).toContain('sensor.solaredge_m1_ac_current_a');
    expect(options()).not.toContain('sensor.solaredge_m1_ac_power_a');
    expect(options()).not.toContain('switch.grzalka_1');
    expect(options()).not.toContain('light.kitchen');
  });

  it('offers a unit-only sensor for a phase row', () => {
    // A YAML Modbus sensor often has a unit and no device class.
    const modbus: DiscoveredEntity = { ...entities[0]!, entityId: 'sensor.modbus_l1_amps', domain: 'sensor', deviceClass: null, unit: 'A', derived: null, disabled: false };

    expect(matchesFilter(modbus, CURRENT)).toBe(true);
    expect(matchesFilter({ ...modbus, unit: 'W' }, CURRENT)).toBe(false);
  });

  it('lists a derived sensor disabled with its platform as the reason', () => {
    render(<EntityPicker id="p" label="L1 current" value={null} onChange={() => {}} entities={entities} filter={{ domains: ['sensor'], deviceClasses: ['power'], units: ['W'] }} t={t} />);

    const usage = [...(screen.getByLabelText('L1 current') as HTMLSelectElement).options].find((o) => o.value === 'sensor.usage')!;
    expect(usage.disabled).toBe(true);
    expect(usage.textContent).toBe('sensor.usage — computed by template, not an input');
  });

  it('actuator picker lists declarable domains and never preselects', () => {
    const changed = vi.fn();
    render(<EntityPicker id="p" label="L1 current" value={null} onChange={changed} entities={entities} filter={RELAY} t={t} />);

    const select = screen.getByLabelText('L1 current') as HTMLSelectElement;
    expect(select.value).toBe('');
    expect(options().filter((o) => o !== '').every((o) => o.startsWith('switch.') || o.startsWith('input_boolean.'))).toBe(true);
    expect(options()).toContain('switch.grzalka_1');
    expect(changed).not.toHaveBeenCalled();

    fireEvent.change(select, { target: { value: 'switch.grzalka_1' } });
    expect(changed).toHaveBeenCalledWith('switch.grzalka_1');
  });

  it('keeps a value Home Assistant does not offer, and allows typing one by hand', () => {
    const changed = vi.fn();
    render(<EntityPicker id="p" label="L1 current" value="sensor.hand_written" onChange={changed} entities={entities} filter={CURRENT} t={t} />);

    expect((screen.getByLabelText('L1 current') as HTMLSelectElement).value).toBe('sensor.hand_written');
    fireEvent.click(screen.getByRole('button', { name: 'Enter the entity ID by hand' }));
    const input = screen.getByLabelText('L1 current') as HTMLInputElement;
    expect(input.classList.contains('mono')).toBe(true);
    fireEvent.input(input, { target: { value: ' sensor.meter_l1 ' } });
    expect(changed).toHaveBeenLastCalledWith('sensor.meter_l1');
  });

  it('falls back to manual entry when Home Assistant cannot be reached', () => {
    render(<EntityPicker id="p" label="L1 current" value={null} onChange={() => {}} entities={null} filter={CURRENT} t={t} />);

    expect(screen.getByLabelText('L1 current').tagName).toBe('INPUT');
    expect(screen.getByText('Home Assistant cannot be reached: enter the entity ID by hand.')).toBeTruthy();
  });
});
