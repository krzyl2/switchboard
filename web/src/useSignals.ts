import { fetchSignals, type Signals } from './api/signals';
import { SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/signals: a frozen "fresh" next to a failure would read as a healthy meter, so a failure withdraws the values. */
export function useSignals(): PollView<Signals> {
  return usePoll(fetchSignals, SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS);
}
