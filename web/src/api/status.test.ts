import { describe, expect, it } from 'vitest';
import { parseStatus } from './status';

const valid = { version: '0.1.0', mode: 'shadow', homeAssistant: 'connected', mqtt: 'connecting', healthy: true };

// The page must never display a state the add-on did not report, so anything outside the exact
// contract of GET api/status is rejected and shown as "status unavailable".
describe('parseStatus', () => {
  it('accepts the documented payload', () => {
    expect(parseStatus(valid)).toEqual(valid);
  });

  it('drops fields the page does not know about', () => {
    expect(parseStatus({ ...valid, token: 'must-not-travel' })).toEqual(valid);
  });

  it.each([
    ['null', null],
    ['an array', []],
    ['mode in the wrong case', { ...valid, mode: 'Active' }],
    ['an unknown mode', { ...valid, mode: 'manual' }],
    ['a missing mode', { ...valid, mode: undefined }],
    ['an unknown connection state', { ...valid, homeAssistant: 'online' }],
    ['a string instead of a boolean for healthy', { ...valid, healthy: 'true' }],
    ['a numeric version', { ...valid, version: 1 }],
  ])('rejects %s', (_case, payload) => {
    expect(() => parseStatus(payload)).toThrow();
  });
});
