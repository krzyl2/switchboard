import { describe, expect, it } from 'vitest';
import { parseMode } from './mode';
import sample from './mode.sample.json';

// MODE-02: the mode is shown honestly: the add-on option, the control switch, the configuration and
// the state confirmation, and every condition that keeps it in shadow. An "active" the add-on did
// not report, or one next to a missing condition, must never reach the page.
describe('mode', () => {
  it('accepts the documented payload', () => {
    expect(parseMode(structuredClone(sample))).toEqual({
      ceiling: 'shadow',
      controlAllowed: false,
      configurationValid: false,
      stateConfirmed: true,
      effective: 'shadow',
      missing: ['option_shadow', 'control_off', 'configuration_invalid'],
      note: 'none',
    });
  });

  it('accepts active with nothing missing', () => {
    const payload = { ...structuredClone(sample), ceiling: 'active', controlAllowed: true, configurationValid: true, effective: 'active', missing: [] };
    expect(parseMode(payload).effective).toBe('active');
  });

  it.each([
    ['an unknown mode', (p: any) => (p.effective = 'live')],
    ['an unknown note', (p: any) => (p.note = 'later')],
    ['an unknown condition', (p: any) => (p.missing = ['no_reason'])],
    ['a missing ceiling', (p: any) => delete p.ceiling],
    ['a missing control switch', (p: any) => delete p.controlAllowed],
    ['active next to a missing condition', (p: any) => (p.effective = 'active')],
    ['shadow with nothing missing', (p: any) => (p.missing = [])],
  ])('refuses %s', (_case, change) => {
    const payload = structuredClone(sample);
    change(payload);
    expect(() => parseMode(payload)).toThrow();
  });
});
