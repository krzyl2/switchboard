import { useCallback, useEffect, useRef, useState } from 'preact/hooks';

export type PollView<T> =
  | { kind: 'loading' }
  | { kind: 'live'; data: T; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

export interface PollOptions<T> {
  /** A new key (filters, a range, the entities asked for) starts the polling over. */
  key?: string;
  /** False asks nothing and shows `loading`. Default true. */
  enabled?: boolean;
  /** Asked after every answer; false ends the polling (a finished test), until `refresh` reads again. Default: always. */
  continueWhile?: (data: T) => boolean;
}

export interface PollControl<T> {
  view: PollView<T>;
  /** Reads at once, replacing a request still in flight (after a change the page made itself). */
  refresh: () => void;
  /** Shows an answer the page received another way (the response to a change); it is newer than any request in flight. */
  push: (data: T) => void;
}

const idle = { refresh: () => undefined, push: () => undefined };

/**
 * The one polling loop of the page: one request at a time, the next one scheduled after the previous
 * settles, each bounded by a timeout. A failed, timed-out or unparseable answer withdraws every value
 * (a frozen number next to a failure would read as a calm system) and keeps the time of the last
 * success. Every request carries a sequence number and only the newest one may show: a slow answer
 * that arrives after a newer one (a refresh, a pushed answer) is dropped. While the tab is hidden
 * nothing is asked; becoming visible reads at once. Unmounting aborts the request in flight.
 */
export function usePollControl<T>(fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, timeoutMs: number, options: PollOptions<T> = {}): PollControl<T> {
  const { enabled = true } = options;
  const [view, setView] = useState<PollView<T>>({ kind: 'loading' });
  // The fetcher and the predicate are recreated on every render by callers; the key says when their meaning changed.
  const latest = useRef({ fetcher, continueWhile: options.continueWhile });
  latest.current = { fetcher, continueWhile: options.continueWhile };
  const actions = useRef<Pick<PollControl<T>, 'refresh' | 'push'>>(idle);

  useEffect(() => {
    setView({ kind: 'loading' });
    if (!enabled) return;

    let disposed = false;
    let stopped = false;
    let sequence = 0;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const schedule = () => {
      clearTimeout(nextPoll);
      if (!stopped && !document.hidden) nextPoll = setTimeout(run, intervalMs);
    };

    const run = async () => {
      clearTimeout(nextPoll);
      inFlight?.abort();
      const mine = ++sequence;
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const data = await latest.current.fetcher(controller.signal);
        if (disposed || mine !== sequence) return;
        lastSuccessAt = new Date();
        stopped = latest.current.continueWhile?.(data) === false;
        setView({ kind: 'live', data, updatedAt: lastSuccessAt });
      } catch {
        if (disposed || mine !== sequence) return;
        stopped = false;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      schedule();
    };

    const onVisibility = () => {
      if (document.hidden) clearTimeout(nextPoll);
      else if (!stopped) void run();
    };

    actions.current = {
      refresh: () => {
        stopped = false;
        void run();
      },
      push: (data) => {
        if (disposed) return;
        sequence++;
        inFlight?.abort();
        lastSuccessAt = new Date();
        setView({ kind: 'live', data, updatedAt: lastSuccessAt });
        schedule();
      },
    };

    document.addEventListener('visibilitychange', onVisibility);
    void run();

    return () => {
      disposed = true;
      actions.current = idle;
      document.removeEventListener('visibilitychange', onVisibility);
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
  }, [options.key, enabled, intervalMs, timeoutMs]);

  const refresh = useCallback(() => actions.current.refresh(), []);
  const push = useCallback((data: T) => actions.current.push(data), []);
  return { view, refresh, push };
}

export function usePoll<T>(fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, timeoutMs: number, key = ''): PollView<T> {
  return usePollControl(fetcher, intervalMs, timeoutMs, { key }).view;
}
