import { fetchGuard, type GuardStatus } from './api/guard';
import { GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/guard: a frozen "normal" or a frozen full budget next to a failure would read as a calm phase, so a failure withdraws the values. */
export function useGuard(): PollView<GuardStatus> {
  return usePoll(fetchGuard, GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS);
}
