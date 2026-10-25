import { MODE_NOTES, OPERATING_MODES, type ModeNote, type OperatingMode } from './decisions';
import { field, object, oneOf, text } from './strict';

// Contract of GET and POST api/mode (src/Switchboard.Host/Web/ModeEndpoint.cs, MODE-02): the add-on
// option is the ceiling, the UI choice is persisted in state.json and takes effect without a
// restart, and the effective mode is shown honestly. In this version the effective mode is always
// shadow and the add-on refuses active (409 active_not_enabled): commanding is not enabled.

export interface Mode {
  ceiling: OperatingMode;
  uiChoice: OperatingMode;
  effective: OperatingMode;
  note: ModeNote;
}

export function parseMode(raw: unknown): Mode {
  const r = object(raw, 'payload');
  return {
    ceiling: oneOf(OPERATING_MODES, field(r, 'ceiling', 'payload'), 'ceiling'),
    uiChoice: oneOf(OPERATING_MODES, field(r, 'uiChoice', 'payload'), 'uiChoice'),
    effective: oneOf(OPERATING_MODES, field(r, 'effective', 'payload'), 'effective'),
    note: oneOf(MODE_NOTES, field(r, 'note', 'payload'), 'note'),
  };
}

export async function fetchMode(signal: AbortSignal): Promise<Mode> {
  const response = await fetch('api/mode', { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Mode request failed with HTTP ${response.status}.`);
  return parseMode(await response.json());
}

export type ModeChoice = { kind: 'chosen'; mode: Mode } | { kind: 'refused'; code: string };

/** POST api/mode as JSON (a cross-site form cannot send it without a preflight). */
export async function chooseMode(uiChoice: OperatingMode, signal?: AbortSignal): Promise<ModeChoice> {
  const response = await fetch('api/mode', {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ uiChoice }),
  });
  if (response.ok) return { kind: 'chosen', mode: parseMode(await response.json()) };
  if (response.status === 409) return { kind: 'refused', code: text(field(object(await response.json(), 'payload'), 'code', 'payload'), 'code') };
  throw new Error(`Choosing the mode failed with HTTP ${response.status}.`);
}
