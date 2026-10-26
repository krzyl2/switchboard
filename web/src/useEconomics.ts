import { fetchEconomics, type Economics } from './api/economics';
import { ECONOMICS_REFRESH_INTERVAL_MS, ECONOMICS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/economics: the tariff now, the settlement, each device's value and sources, and the savings. */
export function useEconomics(): PollView<Economics> {
  return usePoll(fetchEconomics, ECONOMICS_REFRESH_INTERVAL_MS, ECONOMICS_REQUEST_TIMEOUT_MS);
}
