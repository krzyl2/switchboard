import { fetchStatus, type Status } from './api/status';
import { STATUS_REFRESH_INTERVAL_MS, STATUS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/status: the add-on's version, mode and connections. */
export function useStatus(): PollView<Status> {
  return usePoll(fetchStatus, STATUS_REFRESH_INTERVAL_MS, STATUS_REQUEST_TIMEOUT_MS);
}
