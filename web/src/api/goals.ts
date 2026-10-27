import { bool, field, instant, number, object, oneOf, text } from './strict';
import { GOAL_KINDS, type GoalKind } from './plan';

// Contract of POST api/goals and DELETE api/goals/{device} (src/Switchboard.Host/Web/GoalsEndpoint.cs,
// PLAN-19): the "want now" action of the device card. It stores or cancels a manual goal in the add-on's
// own state and writes no entity. A JSON body is required: a cross-site form cannot send one without a CORS
// preflight, which the add-on never answers. PlanSpaContractTests compares the refusal codes with the backend's.

/** Why the add-on refused: a closed code, worded in the UI (the message sentence is for a person reading the answer). */
export const GOAL_REFUSALS = [
  'bad_request',
  'unknown_device',
  'no_configuration',
  'no_manual_goal',
  'soc_not_configured',
  'target_out_of_range',
  'deadline_passed',
  'deadline_beyond_horizon',
] as const;

export type GoalRefusal = (typeof GOAL_REFUSALS)[number] | 'failed';

export interface WantNow {
  device: string;
  kind: GoalKind;
  /** kWh for an energy goal, percent for a SoC goal. */
  target: number;
  deadline: Date;
  hard: boolean;
}

export interface StoredGoal {
  device: string;
  kind: GoalKind;
  target: number;
  deadline: Date;
  hard: boolean;
  since: Date;
  deliveredKwh: number;
  /** False when the same goal was already stored: nothing changed. */
  created: boolean;
}

export class GoalError extends Error {
  readonly refusal: GoalRefusal;

  constructor(refusal: GoalRefusal) {
    super(`Goal request refused: ${refusal}.`);
    this.refusal = refusal;
  }
}

export function parseStoredGoal(raw: unknown): StoredGoal {
  const path = 'goal';
  const r = object(raw, path);
  return {
    device: text(field(r, 'device', path), `${path}.device`),
    kind: oneOf(GOAL_KINDS, field(r, 'kind', path), `${path}.kind`),
    target: number(field(r, 'target', path), `${path}.target`),
    deadline: instant(field(r, 'deadline', path), `${path}.deadline`),
    hard: bool(field(r, 'hard', path), `${path}.hard`),
    since: instant(field(r, 'since', path), `${path}.since`),
    deliveredKwh: number(field(r, 'deliveredKwh', path), `${path}.deliveredKwh`),
    created: bool(field(r, 'created', path), `${path}.created`),
  };
}

/** The refusal an error answer names; any answer that is not the add-on's own refusal is a plain failure. */
async function refusal(response: Response): Promise<GoalRefusal> {
  try {
    const code = text(field(object(await response.json(), 'payload'), 'code', 'payload'), 'code');
    return (GOAL_REFUSALS as readonly string[]).includes(code) ? (code as GoalRefusal) : 'failed';
  } catch {
    return 'failed';
  }
}

// Relative on purpose: ingress serves the page under a per-session prefix.
const GOALS_URL = 'api/goals';

/** POST api/goals. Throws a GoalError with the add-on's refusal code. */
export async function postGoal(want: WantNow, signal?: AbortSignal): Promise<StoredGoal> {
  const response = await fetch(GOALS_URL, {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ device: want.device, kind: want.kind, target: want.target, deadline: want.deadline.toISOString(), hard: want.hard }),
  });
  if (!response.ok) throw new GoalError(await refusal(response));
  return parseStoredGoal(await response.json());
}

/** DELETE api/goals/{device}: cancels the device's manual goal. */
export async function deleteGoal(device: string, signal?: AbortSignal): Promise<void> {
  const response = await fetch(`${GOALS_URL}/${encodeURIComponent(device)}`, { method: 'DELETE', signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new GoalError(await refusal(response));
}
