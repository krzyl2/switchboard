import { parseDecision, type Decision, type DecisionKind } from './decisions';
import { bool, count, field, list, nullable, object, text } from './strict';

// Contract of GET api/decisions/history (src/Switchboard.Host/Web/DecisionHistoryEndpoint.cs,
// MODE-04): the decision log over its whole retention, newest first, read from the daily files.
// `next` is an opaque cursor; a page cut by the add-on's search budget is `partial` and still has
// one. The server is timezone-free: the browser sends local-day bounds as UTC instants.

export interface DecisionHistoryPage {
  items: Decision[];
  /** Where the next (older) page starts; null when the retained history is exhausted. */
  next: string | null;
  /** The UTC date (yyyy-MM-dd) of the oldest retained daily file; null when there is none. */
  retainedFrom: string | null;
  retentionDays: number;
  partial: boolean;
  logWritable: boolean;
}

export interface DecisionHistoryQuery {
  device?: string;
  kind?: DecisionKind;
  /** A previous page's next. */
  cursor?: string;
  from?: Date;
  to?: Date;
  limit?: number;
}

export function parseDecisionHistory(raw: unknown): DecisionHistoryPage {
  const r = object(raw, 'payload');
  return {
    items: list(field(r, 'items', 'payload'), 'items', parseDecision),
    next: nullable(field(r, 'next', 'payload'), 'next', text),
    retainedFrom: nullable(field(r, 'retainedFrom', 'payload'), 'retainedFrom', text),
    retentionDays: count(field(r, 'retentionDays', 'payload'), 'retentionDays'),
    partial: bool(field(r, 'partial', 'payload'), 'partial'),
    logWritable: bool(field(r, 'logWritable', 'payload'), 'logWritable'),
  };
}

export async function fetchDecisionHistory(signal: AbortSignal, query: DecisionHistoryQuery = {}): Promise<DecisionHistoryPage> {
  const parameters = new URLSearchParams();
  if (query.device !== undefined) parameters.set('device', query.device);
  if (query.kind !== undefined) parameters.set('kind', query.kind);
  if (query.from !== undefined) parameters.set('from', query.from.toISOString());
  if (query.to !== undefined) parameters.set('to', query.to.toISOString());
  if (query.limit !== undefined) parameters.set('limit', String(query.limit));
  if (query.cursor !== undefined) parameters.set('cursor', query.cursor);
  const search = parameters.toString();
  const response = await fetch(search === '' ? 'api/decisions/history' : `api/decisions/history?${search}`, {
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new Error(`Decision history request failed with HTTP ${response.status}.`);
  return parseDecisionHistory(await response.json());
}
