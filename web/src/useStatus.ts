import { useEffect, useState } from 'preact/hooks';
import { fetchStatus, type Status } from './api/status';
import { STATUS_REFRESH_INTERVAL_MS, STATUS_REQUEST_TIMEOUT_MS } from './config';

export type StatusView =
  | { kind: 'loading' }
  | { kind: 'live'; status: Status; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

/**
 * Polls the add-on status. The next request is scheduled only after the previous one settles, so a
 * slow add-on never accumulates overlapping requests. A failed or timed-out request drops the last
 * known values: showing a stale "connected" next to a failure would read as a calm system.
 */
export function useStatus(): StatusView {
  const [view, setView] = useState<StatusView>({ kind: 'loading' });

  useEffect(() => {
    let disposed = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), STATUS_REQUEST_TIMEOUT_MS);
      try {
        const status = await fetchStatus(controller.signal);
        if (disposed) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', status, updatedAt: lastSuccessAt });
      } catch {
        if (disposed) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      nextPoll = setTimeout(poll, STATUS_REFRESH_INTERVAL_MS);
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
