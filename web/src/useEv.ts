import { useCallback } from 'preact/hooks';
import { deleteChargeNow, EvError, fetchEv, postChargeNow, postEvMode, type ChargeNowRequest, type Ev, type EvMode, type EvRefusal } from './api/ev';
import { withTimeout } from './api/http';
import { EV_CHANGE_TIMEOUT_MS, EV_REFRESH_INTERVAL_MS, EV_REQUEST_TIMEOUT_MS } from './config';
import { usePollControl, type PollView } from './usePoll';

export interface EvControl {
  view: PollView<Ev>;
  /** Each action resolves to null when the add-on took it, or to why it did not. The EV answer is read again at once. */
  setMode: (mode: EvMode) => Promise<EvRefusal | null>;
  chargeNow: (request: ChargeNowRequest) => Promise<EvRefusal | null>;
  cancelChargeNow: () => Promise<EvRefusal | null>;
}

/**
 * GET api/ev plus the manual control of the module: after a change the answer is read again at once, so
 * the page shows what the add-on now holds, not what was asked for.
 */
export function useEv(): EvControl {
  const { view, refresh } = usePollControl(fetchEv, EV_REFRESH_INTERVAL_MS, EV_REQUEST_TIMEOUT_MS);

  const send = useCallback(
    async (request: (signal: AbortSignal) => Promise<unknown>): Promise<EvRefusal | null> => {
      try {
        await withTimeout(request, EV_CHANGE_TIMEOUT_MS);
        refresh();
        return null;
      } catch (error) {
        return error instanceof EvError ? error.refusal : 'failed';
      }
    },
    [refresh],
  );

  const setMode = useCallback((mode: EvMode) => send((signal) => postEvMode(mode, signal)), [send]);
  const chargeNow = useCallback((request: ChargeNowRequest) => send((signal) => postChargeNow(request, signal)), [send]);
  const cancelChargeNow = useCallback(() => send((signal) => deleteChargeNow(signal)), [send]);

  return { view, setMode, chargeNow, cancelChargeNow };
}
