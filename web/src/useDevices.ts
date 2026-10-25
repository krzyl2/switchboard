import { fetchDevices, type Devices } from './api/devices';
import { DEVICES_REFRESH_INTERVAL_MS, DEVICES_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/devices: the declared devices with their entities, names and limits. */
export function useDevices(): PollView<Devices> {
  return usePoll(fetchDevices, DEVICES_REFRESH_INTERVAL_MS, DEVICES_REQUEST_TIMEOUT_MS);
}
