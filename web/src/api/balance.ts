import { instant, bool, field, nullable, number, object, oneOf } from './strict';

// Contract of GET api/balance (src/Switchboard.Host/Web/BalanceEndpoint.cs): the balance of the
// running local hour (BAL-01) and what the allowance rule made of it in the last dispatch step
// (BAL-07). Wh and W, positive = export. BalanceSpaContractTests compares the closed lists with the
// backend enums and the keys of balance.sample.json with what the endpoint writes.

/** Why the hour's balance is not known (the tracker's own reasons; none while it is known). */
export const BALANCE_REASONS = ['none', 'unavailable', 'stale', 'no_baseline', 'counter_reset', 'clock_step_back'] as const;
/** Why the rule's figures are not known: the balance's own reason, or one of the rule's. */
export const BALANCE_UNKNOWN_REASONS = ['none', 'unavailable', 'stale', 'no_baseline', 'counter_reset', 'clock_step_back', 'balance_not_used', 'free_power_unknown', 'own_load_unknown', 'no_dispatch_step'] as const;

export type BalanceReason = (typeof BALANCE_REASONS)[number];
export type BalanceUnknownReason = (typeof BALANCE_UNKNOWN_REASONS)[number];

export interface BalanceBand {
  loWh: number;
  hiWh: number;
  /** Inside the burn window: the upper edge ramps down to the floor. */
  burning: boolean;
}

export interface HourlyBalance {
  /** False while no energy counters are mapped; everything else is then empty. */
  configured: boolean;
  /** Balance of the running hour in Wh, positive = net export; null while unknown. */
  wh: number | null;
  hourStart: Date | null;
  /** Seconds to the next local :00 as the monitor counts them. */
  secondsToNextHour: number | null;
  reason: BalanceReason;
  /** The rule had the balance, free power and own load: projection and band are real. */
  known: boolean;
  unknownReason: BalanceUnknownReason;
  freeW: number | null;
  loadW: number | null;
  projectionWh: number | null;
  band: BalanceBand | null;
  /** Seconds to the :00 the rule used in its step. */
  ruleSecondsToNextHour: number | null;
  ruleAt: Date | null;
}

const BALANCE_URL = 'api/balance';

export function parseBalance(raw: unknown): HourlyBalance {
  const path = 'balance';
  const r = object(raw, path);
  const n = (key: string) => nullable(field(r, key, path), `${path}.${key}`, number);
  return {
    configured: bool(field(r, 'configured', path), `${path}.configured`),
    wh: n('wh'),
    hourStart: nullable(field(r, 'hourStart', path), `${path}.hourStart`, instant),
    secondsToNextHour: n('secondsToNextHour'),
    reason: oneOf(BALANCE_REASONS, field(r, 'reason', path), `${path}.reason`),
    known: bool(field(r, 'known', path), `${path}.known`),
    unknownReason: oneOf(BALANCE_UNKNOWN_REASONS, field(r, 'unknownReason', path), `${path}.unknownReason`),
    freeW: n('freeW'),
    loadW: n('loadW'),
    projectionWh: n('projectionWh'),
    band: nullable(field(r, 'band', path), `${path}.band`, (v, p) => {
      const b = object(v, p);
      return { loWh: number(field(b, 'loWh', p), `${p}.loWh`), hiWh: number(field(b, 'hiWh', p), `${p}.hiWh`), burning: bool(field(b, 'burning', p), `${p}.burning`) };
    }),
    ruleSecondsToNextHour: n('ruleSecondsToNextHour'),
    ruleAt: nullable(field(r, 'ruleAt', path), `${path}.ruleAt`, instant),
  };
}

export async function fetchBalance(signal: AbortSignal): Promise<HourlyBalance> {
  const response = await fetch(BALANCE_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Balance request failed with HTTP ${response.status}.`);
  return parseBalance(await response.json());
}
