import { fetchDecisions, type DecisionQuery, type DecisionsPage } from './api/decisions';
import { DECISION_LOG_REFRESH_INTERVAL_MS, DECISION_LOG_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/**
 * GET api/decisions with filters (device, kind) and a cursor for older pages; a new query starts
 * the polling over. The newest page refreshes; an older page is a fixed slice of the memory window.
 */
export function useDecisionLog(query: DecisionQuery): PollView<DecisionsPage> {
  const key = JSON.stringify(query);
  return usePoll((signal) => fetchDecisions(signal, query), DECISION_LOG_REFRESH_INTERVAL_MS, DECISION_LOG_REQUEST_TIMEOUT_MS, key);
}
