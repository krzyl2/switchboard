import { MODE_CONDITIONS, MODE_NOTES, OPERATING_MODES, type ModeCondition, type ModeNote, type OperatingMode } from './decisions';
import { bool, fail, field, list, object, oneOf } from './strict';

// Contract of GET api/mode (src/Switchboard.Host/Web/ModeEndpoint.cs, MODE-02): the add-on option
// is the ceiling; commands need it active, the global control switch on (api/control), a valid
// configuration and a confirmed state. Whatever is missing keeps the effective mode in shadow and is
// named in `missing`. The mode is changed only through api/control; api/mode is read-only.

export interface Mode {
  ceiling: OperatingMode;
  controlAllowed: boolean;
  configurationValid: boolean;
  stateConfirmed: boolean;
  effective: OperatingMode;
  missing: ModeCondition[];
  note: ModeNote;
}

export function parseMode(raw: unknown, path = 'payload'): Mode {
  const r = object(raw, path);
  const mode: Mode = {
    ceiling: oneOf(OPERATING_MODES, field(r, 'ceiling', path), `${path}.ceiling`),
    controlAllowed: bool(field(r, 'controlAllowed', path), `${path}.controlAllowed`),
    configurationValid: bool(field(r, 'configurationValid', path), `${path}.configurationValid`),
    stateConfirmed: bool(field(r, 'stateConfirmed', path), `${path}.stateConfirmed`),
    effective: oneOf(OPERATING_MODES, field(r, 'effective', path), `${path}.effective`),
    missing: list(field(r, 'missing', path), `${path}.missing`, (v, p) => oneOf(MODE_CONDITIONS, v, p)),
    note: oneOf(MODE_NOTES, field(r, 'note', path), `${path}.note`),
  };
  // Active means nothing is missing; an "active" next to a missing condition must never be rendered.
  if ((mode.effective === 'active') !== (mode.missing.length === 0)) {
    fail(`${path}.effective`, 'active exactly when nothing is missing');
  }
  return mode;
}

export async function fetchMode(signal: AbortSignal): Promise<Mode> {
  const response = await fetch('api/mode', { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Mode request failed with HTTP ${response.status}.`);
  return parseMode(await response.json());
}
