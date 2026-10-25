import { DECISION_ACTIONS, REASON_CODES } from './decisions';
import type { DecisionAction, ReasonCode } from './decisions';
import { PHASES } from './signals';
import type { PhaseName } from './signals';
import { parsePresentationFile } from './statistics';
import type { PresentationFile } from './statistics';
import { bool, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/excursions (src/Switchboard.Host/Web/ExcursionsEndpoint.cs): per phase the
// last (or ongoing) and the worst excursion above the reserve threshold within the retention
// window, for the Limits chart. PresentationSpaContractTests compares the keys of
// excursions.sample.json with what the endpoint writes.

export interface ExcursionSample {
  at: Date;
  /** Measured, signed (import positive). */
  a: number;
}

export interface ExcursionDecision {
  id: string;
  at: Date;
  action: DecisionAction;
  reason: ReasonCode;
  device: string | null;
  commanded: boolean;
}

export interface Excursion {
  active: boolean;
  start: Date;
  end: Date | null;
  peakA: number;
  peakAt: Date;
  reserveThresholdA: number;
  ratingA: number;
  continuousA: number;
  /** The samples were thinned to the cap (the peak and every decision instant kept). */
  decimated: boolean;
  samples: ExcursionSample[];
  decisions: ExcursionDecision[];
}

export interface PhaseExcursions {
  phase: PhaseName;
  last: Excursion | null;
  worst: Excursion | null;
}

export interface Excursions {
  at: Date;
  stateFile: PresentationFile;
  phases: PhaseExcursions[];
}

const EXCURSIONS_URL = 'api/excursions';

function decision(raw: unknown, path: string): ExcursionDecision {
  const r = object(raw, path);
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    at: instant(field(r, 'at', path), `${path}.at`),
    action: oneOf(DECISION_ACTIONS, field(r, 'action', path), `${path}.action`),
    reason: oneOf(REASON_CODES, field(r, 'reason', path), `${path}.reason`),
    device: nullable(field(r, 'device', path), `${path}.device`, text),
    commanded: bool(field(r, 'commanded', path), `${path}.commanded`),
  };
}

function excursion(raw: unknown, path: string): Excursion {
  const r = object(raw, path);
  const n = (key: string) => number(field(r, key, path), `${path}.${key}`);
  const active = bool(field(r, 'active', path), `${path}.active`);
  const end = nullable(field(r, 'end', path), `${path}.end`, instant);
  // Ongoing exactly while it has no end.
  if (active !== (end === null)) fail(`${path}.end`, 'null exactly while the excursion is active');
  const samples = list(field(r, 'samples', path), `${path}.samples`, (v, p) => {
    const s = object(v, p);
    return { at: instant(field(s, 'at', p), `${p}.at`), a: number(field(s, 'a', p), `${p}.a`) };
  });
  samples.forEach((s, i) => {
    if (i > 0 && s.at < samples[i - 1]!.at) fail(`${path}.samples[${i}].at`, 'in time order');
  });
  return {
    active,
    start: instant(field(r, 'start', path), `${path}.start`),
    end,
    peakA: n('peakA'),
    peakAt: instant(field(r, 'peakAt', path), `${path}.peakAt`),
    reserveThresholdA: n('reserveThresholdA'),
    ratingA: n('ratingA'),
    continuousA: n('continuousA'),
    decimated: bool(field(r, 'decimated', path), `${path}.decimated`),
    samples,
    decisions: list(field(r, 'decisions', path), `${path}.decisions`, decision),
  };
}

/** Accepts only the exact shape the add-on promises; anything else is an error, never a guessed chart. */
export function parseExcursions(raw: unknown): Excursions {
  const r = object(raw, 'payload');
  return {
    at: instant(field(r, 'at', 'payload'), 'at'),
    stateFile: parsePresentationFile(field(r, 'stateFile', 'payload'), 'stateFile'),
    phases: list(field(r, 'phases', 'payload'), 'phases', (v, p) => {
      const ph = object(v, p);
      return {
        phase: oneOf(PHASES, field(ph, 'phase', p), `${p}.phase`),
        last: nullable(field(ph, 'last', p), `${p}.last`, excursion),
        worst: nullable(field(ph, 'worst', p), `${p}.worst`, excursion),
      };
    }),
  };
}

export async function fetchExcursions(signal: AbortSignal): Promise<Excursions> {
  const response = await fetch(EXCURSIONS_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Excursions request failed with HTTP ${response.status}.`);
  return parseExcursions(await response.json());
}
