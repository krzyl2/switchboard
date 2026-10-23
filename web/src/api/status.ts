export type ConnectionState = 'connected' | 'connecting' | 'disconnected';
export type Mode = 'shadow' | 'active';

export interface Status {
  version: string;
  mode: Mode;
  homeAssistant: ConnectionState;
  mqtt: ConnectionState;
  healthy: boolean;
}

// Relative on purpose: ingress serves the page under a per-session prefix, and a leading slash would
// address Home Assistant itself instead of this add-on.
const STATUS_URL = 'api/status';

const MODES: readonly Mode[] = ['shadow', 'active'];
const CONNECTION_STATES: readonly ConnectionState[] = ['connected', 'connecting', 'disconnected'];

function isOneOf<T extends string>(allowed: readonly T[], value: unknown): value is T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value);
}

/**
 * Accepts only the exact shape the add-on promises. Anything else is an error rather than a guess,
 * so the page never shows a state the add-on did not report.
 */
export function parseStatus(raw: unknown): Status {
  if (typeof raw !== 'object' || raw === null) {
    throw new Error('Status payload is not an object.');
  }
  const r = raw as Record<string, unknown>;
  if (
    typeof r.version !== 'string' ||
    !isOneOf(MODES, r.mode) ||
    !isOneOf(CONNECTION_STATES, r.homeAssistant) ||
    !isOneOf(CONNECTION_STATES, r.mqtt) ||
    typeof r.healthy !== 'boolean'
  ) {
    throw new Error('Status payload has an unexpected shape.');
  }
  return {
    version: r.version,
    mode: r.mode,
    homeAssistant: r.homeAssistant,
    mqtt: r.mqtt,
    healthy: r.healthy,
  };
}

export async function fetchStatus(signal: AbortSignal): Promise<Status> {
  const response = await fetch(STATUS_URL, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw new Error(`Status request failed with HTTP ${response.status}.`);
  }
  return parseStatus(await response.json());
}
