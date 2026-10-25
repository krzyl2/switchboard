import { fetchStatistics, type Statistics } from './api/statistics';
import { STATISTICS_REFRESH_INTERVAL_MS, STATISTICS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/**
 * GET api/statistics, optionally only the buckets of [from, to): the UTC instants of the local day
 * (localTime.ts) the screen shows. A new range starts the polling over.
 */
export function useStatistics(range?: { from: Date; to: Date }): PollView<Statistics> {
  const key = range === undefined ? '' : `${range.from.toISOString()}/${range.to.toISOString()}`;
  return usePoll((signal) => fetchStatistics(signal, range), STATISTICS_REFRESH_INTERVAL_MS, STATISTICS_REQUEST_TIMEOUT_MS, key);
}
