import { CONTROL_SWITCH_SOURCES, parseLevel, type ControlSwitchSource, type Level } from './decisions';
import { parseMode, type Mode } from './mode';
import { bool, count, fail, field, instant, list, nullable, object, oneOf, text } from './strict';

// Contract of GET and POST api/control and POST api/control/confirm
// (src/Switchboard.Host/Web/ControlEndpoint.cs, MODE-02): the global "control allowed" switch,
// persisted in state.json and off after install. Commands go out only while the add-on option is
// active, this switch is on, the configuration is valid and the state is confirmed (mode.missing
// names what is not). Switching it off sends nothing: devices stay where they are, and those
// Switchboard left reduced are listed. ControlSpaContractTests compares the keys and closed lists.

/** Why control is locked off: state.json could not be trusted at start (the kill switch is on until confirmed). */
export const CONTROL_LOCK_REASONS = ['state_file_corrupt'] as const;
export const COMMAND_OUTCOMES = ['acknowledged', 'failed', 'rejected', 'timed_out', 'not_sent'] as const;
export const COMMAND_NOT_SENT_REASONS = ['link_down', 'not_declared', 'superseded', 'control_off', 'own_entity'] as const;

export type ControlLockReason = (typeof CONTROL_LOCK_REASONS)[number];
export type CommandOutcome = (typeof COMMAND_OUTCOMES)[number];
export type CommandNotSentReason = (typeof COMMAND_NOT_SENT_REASONS)[number];

export interface CommandFailure {
  device: string;
  at: Date;
  /** Never acknowledged: a command that was answered is not a failure. */
  outcome: Exclude<CommandOutcome, 'acknowledged'>;
  notSent: CommandNotSentReason | null;
  detail: string | null;
}

export interface Control {
  controlAllowed: boolean;
  changedAt: Date | null;
  source: ControlSwitchSource;
  locked: boolean;
  lockReason: ControlLockReason | null;
  mode: Mode;
  /** How long this installation has been observing (advice only, never enforced). */
  observation: { since: Date; configurationErrors: number; lastConfigurationErrorAt: Date | null } | null;
  commands: {
    linkReady: boolean;
    pending: number;
    lastAcknowledgedAt: Date | null;
    lastFailure: CommandFailure | null;
    /** Devices whose acknowledged command has not shown in their reported state. */
    unconfirmed: { device: string; since: Date }[];
  };
  /** Devices Switchboard holds lower than before (restore_only baselines), with the level it would bring back. */
  leftReduced: { device: string; level: Level; since: Date }[];
}

export function parseControl(raw: unknown): Control {
  const r = object(raw, 'payload');
  const commands = object(field(r, 'commands', 'payload'), 'commands');
  const control: Control = {
    controlAllowed: bool(field(r, 'controlAllowed', 'payload'), 'controlAllowed'),
    changedAt: nullable(field(r, 'changedAt', 'payload'), 'changedAt', instant),
    source: oneOf(CONTROL_SWITCH_SOURCES, field(r, 'source', 'payload'), 'source'),
    locked: bool(field(r, 'locked', 'payload'), 'locked'),
    lockReason: nullable(field(r, 'lockReason', 'payload'), 'lockReason', (v, p) => oneOf(CONTROL_LOCK_REASONS, v, p)),
    mode: parseMode(field(r, 'mode', 'payload'), 'mode'),
    observation: nullable(field(r, 'observation', 'payload'), 'observation', (v, p) => {
      const o = object(v, p);
      return {
        since: instant(field(o, 'since', p), `${p}.since`),
        configurationErrors: count(field(o, 'configurationErrors', p), `${p}.configurationErrors`),
        lastConfigurationErrorAt: nullable(field(o, 'lastConfigurationErrorAt', p), `${p}.lastConfigurationErrorAt`, instant),
      };
    }),
    commands: {
      linkReady: bool(field(commands, 'linkReady', 'commands'), 'commands.linkReady'),
      pending: count(field(commands, 'pending', 'commands'), 'commands.pending'),
      lastAcknowledgedAt: nullable(field(commands, 'lastAcknowledgedAt', 'commands'), 'commands.lastAcknowledgedAt', instant),
      lastFailure: nullable(field(commands, 'lastFailure', 'commands'), 'commands.lastFailure', (v, p) => {
        const f = object(v, p);
        const outcome = oneOf(['failed', 'rejected', 'timed_out', 'not_sent'] as const, field(f, 'outcome', p), `${p}.outcome`);
        const notSent = nullable(field(f, 'notSent', p), `${p}.notSent`, (n, np) => oneOf(COMMAND_NOT_SENT_REASONS, n, np));
        // Only an unsent command says why it was not sent.
        if ((outcome === 'not_sent') !== (notSent !== null)) fail(`${p}.notSent`, 'present exactly for an unsent command');
        return {
          device: text(field(f, 'device', p), `${p}.device`),
          at: instant(field(f, 'at', p), `${p}.at`),
          outcome,
          notSent,
          detail: nullable(field(f, 'detail', p), `${p}.detail`, text),
        };
      }),
      unconfirmed: list(field(commands, 'unconfirmed', 'commands'), 'commands.unconfirmed', (v, p) => {
        const u = object(v, p);
        return { device: text(field(u, 'device', p), `${p}.device`), since: instant(field(u, 'since', p), `${p}.since`) };
      }),
    },
    leftReduced: list(field(r, 'leftReduced', 'payload'), 'leftReduced', (v, p) => {
      const d = object(v, p);
      return {
        device: text(field(d, 'device', p), `${p}.device`),
        level: parseLevel(field(d, 'level', p), `${p}.level`),
        since: instant(field(d, 'since', p), `${p}.since`),
      };
    }),
  };
  // The add-on cannot report control allowed while it is locked, nor a lock without its reason.
  if (control.locked !== (control.lockReason !== null)) fail('lockReason', 'present exactly while locked');
  if (control.locked && control.controlAllowed) fail('controlAllowed', 'false while locked');
  if (control.mode.controlAllowed !== control.controlAllowed) fail('mode.controlAllowed', 'the same as controlAllowed');
  return control;
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const CONTROL_URL = 'api/control';

export async function fetchControl(signal: AbortSignal): Promise<Control> {
  const response = await fetch(CONTROL_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Control request failed with HTTP ${response.status}.`);
  return parseControl(await response.json());
}

export type ControlChange = { kind: 'applied'; control: Control } | { kind: 'refused'; code: string };

async function post(url: string, body: unknown, signal?: AbortSignal): Promise<ControlChange> {
  const response = await fetch(url, {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (response.ok) return { kind: 'applied', control: parseControl(await response.json()) };
  if (response.status === 409) return { kind: 'refused', code: text(field(object(await response.json(), 'payload'), 'code', 'payload'), 'code') };
  throw new Error(`Control request failed with HTTP ${response.status}.`);
}

/**
 * POST api/control as JSON (a cross-site form cannot send it without a preflight). Refused with
 * state_unconfirmed while state.json waits for the user's confirmation.
 */
export function setControlAllowed(allowed: boolean, signal?: AbortSignal): Promise<ControlChange> {
  return post(CONTROL_URL, { allowed }, signal);
}

/** POST api/control/confirm: the user confirms the state after an untrusted state.json; refused with not_locked when nothing waits. */
export function confirmState(signal?: AbortSignal): Promise<ControlChange> {
  return post(`${CONTROL_URL}/confirm`, { confirm: true }, signal);
}
