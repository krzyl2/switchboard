import { fetchExcursions, type Excursions } from './api/excursions';
import { EXCURSIONS_REFRESH_INTERVAL_MS, EXCURSIONS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/** GET api/excursions: the last and the worst excursion per phase, for the Limits chart. */
export function useExcursions(): PollView<Excursions> {
  return usePoll(fetchExcursions, EXCURSIONS_REFRESH_INTERVAL_MS, EXCURSIONS_REQUEST_TIMEOUT_MS);
}
