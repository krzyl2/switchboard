import { useCallback, useEffect, useRef, useState } from 'preact/hooks';
import type { OverrideMode } from './api/decisions';
import { fetchDispatch, OverrideError, postOverride, type DispatchStatus, type OverrideRefusal } from './api/dispatch';
import { DISPATCH_REFRESH_INTERVAL_MS, DISPATCH_REQUEST_TIMEOUT_MS, OVERRIDE_REQUEST_TIMEOUT_MS } from './config';

export type DispatchView =
  | { kind: 'loading' }
  | { kind: 'live'; dispatch: DispatchStatus; updatedAt: Date }
  | { kind: 'unavailable'; lastSuccessAt: Date | null };

export interface DispatchControl {
  view: DispatchView;
  /** Sets a device's override; resolves to null when the add-on evaluated it, or to why it did not. */
  setOverride: (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null>;
}

/**
 * Polls GET api/dispatch the way useGuard polls the guard: one request at a time, the next one
 * scheduled after the previous settles; a failed, timed-out or unparseable answer withdraws every
 * value. An override's answer is the status after the add-on evaluated it, so it replaces the view
 * at once instead of waiting for the next poll.
 */
export function useDispatch(): DispatchControl {
  const [view, setView] = useState<DispatchView>({ kind: 'loading' });
  const disposed = useRef(false);

  useEffect(() => {
    disposed.current = false;
    let lastSuccessAt: Date | null = null;
    let nextPoll: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;

    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), DISPATCH_REQUEST_TIMEOUT_MS);
      try {
        const dispatch = await fetchDispatch(controller.signal);
        if (disposed.current) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', dispatch, updatedAt: lastSuccessAt });
      } catch {
        if (disposed.current) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      nextPoll = setTimeout(poll, DISPATCH_REFRESH_INTERVAL_MS);
    };

    void poll();

    return () => {
      disposed.current = true;
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
  }, []);

  const setOverride = useCallback(async (device: string, mode: OverrideMode): Promise<OverrideRefusal | null> => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), OVERRIDE_REQUEST_TIMEOUT_MS);
    try {
      const dispatch = await postOverride(device, mode, controller.signal);
      if (!disposed.current) setView({ kind: 'live', dispatch, updatedAt: new Date() });
      return null;
    } catch (error) {
      return error instanceof OverrideError ? error.refusal : 'failed';
    } finally {
      clearTimeout(timeout);
    }
  }, []);

  return { view, setOverride };
}
