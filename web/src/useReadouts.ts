import { fetchReadouts, type Readouts } from './api/setup';
import { SETUP_READOUTS_REFRESH_INTERVAL_MS, SETUP_READOUTS_REQUEST_TIMEOUT_MS } from './config';
import { usePoll, type PollView } from './usePoll';

/**
 * Live readouts of the entities a step shows (UI-04): each poll renews the add-on's lease on them,
 * so the subscription lasts while the step is on screen and lapses on its own after. Nothing is
 * asked while no entity is picked.
 */
export function useReadouts(entities: readonly string[]): PollView<Readouts> {
  const wanted = [...new Set(entities.filter((e) => e !== ''))].sort();
  const key = wanted.join(',');
  return usePoll(
    (signal) => (wanted.length === 0 ? Promise.resolve({ at: new Date(), items: [] }) : fetchReadouts(signal, wanted)),
    SETUP_READOUTS_REFRESH_INTERVAL_MS,
    SETUP_READOUTS_REQUEST_TIMEOUT_MS,
    key,
  );
}
