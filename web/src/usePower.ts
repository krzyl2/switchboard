import { fetchPower, type Power } from './api/power';
import { POWER_REFRESH_INTERVAL_MS, POWER_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/power, polled while a screen that shows it is open (dashboard, flows). */
export function usePower(): PollView<Power> {
  return usePoll(fetchPower, POWER_REFRESH_INTERVAL_MS, POWER_REQUEST_TIMEOUT_MS);
}
