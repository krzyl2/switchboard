import { useEffect, useState } from 'preact/hooks';

export type PollView<T> =
  | { kind: 'loading' }
  | { kind: 'live'; data: T; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

/**
 * Polls one endpoint the way the Stage 0-3 pages do: one request at a time, the next one scheduled
 * after the previous settles, each bounded by a timeout. A failed, timed-out or unparseable answer
 * withdraws every value (a frozen number next to a failure would read as a calm system) and keeps
 * the time of the last success. A new <paramref name="key"/> (filters, a range) starts over.
 */
export function usePoll<T>(fetcher: (signal: AbortSignal) => Promise<T>, intervalMs: number, timeoutMs: number, key = ''): PollView<T> {
  const [view, setView] = useState<PollView<T>>({ kind: 'loading' });

  useEffect(() => {
    let disposed = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;
    setView({ kind: 'loading' });

    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), timeoutMs);
      try {
        const data = await fetcher(controller.signal);
        if (disposed) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', data, updatedAt: lastSuccessAt });
      } catch {
        if (disposed) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      nextPoll = setTimeout(poll, intervalMs);
    };

    void poll();

    return () => {
      disposed = true;
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
    // The fetcher is recreated on every render by callers; the key says when its meaning changed.
  }, [key, intervalMs, timeoutMs]);

  return view;
}
