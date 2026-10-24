import { useEffect, useState } from 'preact/hooks';
import { fetchGuard, type GuardStatus } from './api/guard';
import { GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS } from './config';

export type GuardView =
  | { kind: 'loading' }
  | { kind: 'live'; guard: GuardStatus; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

/**
 * Polls GET api/guard the way useSignals polls the signals: one request at a time, the next one
 * scheduled after the previous settles. A failed, timed-out or unparseable answer withdraws every
 * value: a frozen "normal" or a frozen full budget next to a failure would read as a calm phase.
 */
export function useGuard(): GuardView {
  const [view, setView] = useState<GuardView>({ kind: 'loading' });

  useEffect(() => {
    let disposed = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), GUARD_REQUEST_TIMEOUT_MS);
      try {
        const guard = await fetchGuard(controller.signal);
        if (disposed) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', guard, updatedAt: lastSuccessAt });
      } catch {
        if (disposed) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      nextPoll = setTimeout(poll, GUARD_REFRESH_INTERVAL_MS);
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
