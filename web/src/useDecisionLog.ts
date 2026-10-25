import { fetchDecisionHistory, type DecisionHistoryPage, type DecisionHistoryQuery } from './api/decisionHistory';
import { DECISION_LOG_REFRESH_INTERVAL_MS, DECISION_LOG_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/**
 * GET api/decisions/history (MODE-04) with filters (device, kind) and the add-on's cursor for older
 * pages: the whole retained log, not only the memory window. A new query starts the polling over;
 * the newest page refreshes, an older page is a fixed position in the retained files.
 */
export function useDecisionLog(query: DecisionHistoryQuery): PollView<DecisionHistoryPage> {
  const key = JSON.stringify(query);
  return usePoll((signal) => fetchDecisionHistory(signal, query), DECISION_LOG_REFRESH_INTERVAL_MS, DECISION_LOG_REQUEST_TIMEOUT_MS, key);
}
