import { useEffect, useState } from 'preact/hooks';
import { fetchSignals, type Signals } from './api/signals';
import { SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS } from './config';

export type SignalsView =
  | { kind: 'loading' }
  | { kind: 'live'; signals: Signals; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

/**
 * Polls GET api/signals the way useStatus polls the status: one request at a time, the next one
 * scheduled after the previous settles. A failed, timed-out or unparseable answer withdraws every
 * value: a frozen "fresh" next to a failure would read as a healthy meter.
 */
export function useSignals(): SignalsView {
  const [view, setView] = useState<SignalsView>({ kind: 'loading' });

  useEffect(() => {
    let disposed = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), SIGNALS_REQUEST_TIMEOUT_MS);
      try {
        const signals = await fetchSignals(controller.signal);
        if (disposed) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', signals, updatedAt: lastSuccessAt });
      } catch {
        if (disposed) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      nextPoll = setTimeout(poll, SIGNALS_REFRESH_INTERVAL_MS);
    };

    void poll();

    return () => {
      disposed = true;
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
  }, []);

  return view;
}
