import { useCallback, useEffect, useRef, useState } from 'preact/hooks';
import { deleteChargeNow, EvError, fetchEv, postChargeNow, postEvMode, type ChargeNowRequest, type Ev, type EvMode, type EvRefusal } from './api/ev';
import { EV_CHANGE_TIMEOUT_MS, EV_REFRESH_INTERVAL_MS, EV_REQUEST_TIMEOUT_MS } from './config';
import type { PollView } from './usePoll';

export interface EvControl {
  view: PollView<Ev>;
  /** Each action resolves to null when the add-on took it, or to why it did not. The EV answer is read again at once. */
  setMode: (mode: EvMode) => Promise<EvRefusal | null>;
  chargeNow: (request: ChargeNowRequest) => Promise<EvRefusal | null>;
  cancelChargeNow: () => Promise<EvRefusal | null>;
}

/**
 * Polls GET api/ev the way usePlan polls the plan (one request at a time, the next scheduled after the previous settles, a failed or
 * unparseable answer withdraws every value) and adds the manual control of the module: after a change the answer is read again at once, so
 * the page shows what the add-on now holds, not what was asked for.
 */
export function useEv(): EvControl {
  const [view, setView] = useState<PollView<Ev>>({ kind: 'loading' });
  const disposed = useRef(false);
  const poll = useRef<() => void>(() => undefined);

  useEffect(() => {
    disposed.current = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const run = async () => {
      clearTimeout(nextPoll);
      inFlight?.abort();
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), EV_REQUEST_TIMEOUT_MS);
      try {
        const data = await fetchEv(controller.signal);
        if (disposed.current || inFlight !== controller) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', data, updatedAt: lastSuccessAt });
      } catch {
        if (disposed.current || inFlight !== controller) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      if (inFlight === controller) nextPoll = setTimeout(run, EV_REFRESH_INTERVAL_MS);
    };

    poll.current = () => void run();
    void run();

    return () => {
      disposed.current = true;
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
  }, []);

  const send = useCallback(async (request: (signal: AbortSignal) => Promise<unknown>): Promise<EvRefusal | null> => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), EV_CHANGE_TIMEOUT_MS);
    try {
      await request(controller.signal);
      if (!disposed.current) poll.current();
      return null;
    } catch (error) {
      return error instanceof EvError ? error.refusal : 'failed';
    } finally {
      clearTimeout(timeout);
    }
  }, []);

  const setMode = useCallback((mode: EvMode) => send((signal) => postEvMode(mode, signal)), [send]);
  const chargeNow = useCallback((request: ChargeNowRequest) => send((signal) => postChargeNow(request, signal)), [send]);
  const cancelChargeNow = useCallback(() => send((signal) => deleteChargeNow(signal)), [send]);

  return { view, setMode, chargeNow, cancelChargeNow };
}
