import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { confirmState, fetchControl, setControlAllowed, type Control, type ControlChange } from './api/control';
import { fetchMode, type Mode } from './api/mode';
import {
  CONTROL_CHANGE_TIMEOUT_MS,
  CONTROL_REFRESH_INTERVAL_MS,
  CONTROL_REQUEST_TIMEOUT_MS,
  MODE_REFRESH_INTERVAL_MS,
  MODE_REQUEST_TIMEOUT_MS,
} from './config';
import { usePoll, type PollView } from './usePoll';

// MODE-02 in the page: the control block (Status page, setup step 5) polls api/control and changes
// the switch; the banner every page shows polls api/mode. Both only ever show what the add-on
// answered. An answer to a change (or a refresh) is newer than the last poll, so it is shown at once,
// and handed to the banner, which would otherwise keep the old mode until its own next poll.

type Listener = (mode: Mode, at: number) => void;

const modeListeners = new Set<Listener>();

function publishMode(mode: Mode, at: number): void {
  for (const listener of modeListeners) listener(mode, at);
}

/**
 * A poll's view, or a newer answer the page received another way (a change, a refresh). The time a
 * view was first rendered stands for when it arrived, so a failed poll after the answer wins again.
 */
function useNewest<T>(view: PollView<T>, pushed: { data: T; at: number } | null): PollView<T> {
  const seenAt = useMemo(() => Date.now(), [view]);
  return useMemo(
    () => (pushed === null || pushed.at < seenAt ? view : { kind: 'live', data: pushed.data, updatedAt: new Date(pushed.at) }),
    [view, pushed, seenAt],
  );
}

function withTimeout<T>(run: (signal: AbortSignal) => Promise<T>, timeoutMs: number): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  return run(controller.signal).finally(() => clearTimeout(timeout));
}

export type ControlNotice = { kind: 'refused'; code: string } | { kind: 'failed' } | null;

export interface ControlState {
  view: PollView<Control>;
  busy: 'idle' | 'changing' | 'confirming';
  /** Why the last change did not apply; cleared by the next one. */
  notice: ControlNotice;
  /** POST api/control; a refusal is read again so the lock that caused it shows. */
  setAllowed: (allowed: boolean) => Promise<void>;
  /** POST api/control/confirm after an untrusted state.json. */
  confirm: () => Promise<void>;
  /** Reads api/control at once (after a save, which may change the configuration's validity). */
  refresh: () => Promise<Control | null>;
}

export function useControl(): ControlState {
  const polled = usePoll(fetchControl, CONTROL_REFRESH_INTERVAL_MS, CONTROL_REQUEST_TIMEOUT_MS);
  const [pushed, setPushed] = useState<{ data: Control; at: number } | null>(null);
  const [busy, setBusy] = useState<ControlState['busy']>('idle');
  const [notice, setNotice] = useState<ControlNotice>(null);
  const mounted = useRef(true);
  const view = useNewest(polled, pushed);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  useEffect(() => {
    if (view.kind === 'live') publishMode(view.data.mode, view.updatedAt.getTime());
  }, [view]);

  const push = (control: Control) => {
    if (mounted.current) setPushed({ data: control, at: Date.now() });
  };

  const refresh = async (): Promise<Control | null> => {
    try {
      const control = await withTimeout(fetchControl, CONTROL_REQUEST_TIMEOUT_MS);
      push(control);
      return control;
    } catch {
      return null;
    }
  };

  const change = async (kind: 'changing' | 'confirming', send: (signal: AbortSignal) => Promise<ControlChange>) => {
    setBusy(kind);
    setNotice(null);
    try {
      const result = await withTimeout(send, CONTROL_CHANGE_TIMEOUT_MS);
      if (result.kind === 'applied') {
        push(result.control);
      } else {
        if (mounted.current) setNotice({ kind: 'refused', code: result.code });
        await refresh();
      }
    } catch {
      if (mounted.current) setNotice({ kind: 'failed' });
    } finally {
      if (mounted.current) setBusy('idle');
    }
  };

  return {
    view,
    busy,
    notice,
    setAllowed: (allowed) => change('changing', (signal) => setControlAllowed(allowed, signal)),
    confirm: () => change('confirming', confirmState),
    refresh,
  };
}

/**
 * The mode for the banner on every page (shadow or active, honestly): api/mode, or a newer mode the
 * control block just received. Unknown while loading or when the add-on does not answer, never
 * guessed.
 */
export function useModeBanner(): PollView<Mode> {
  const polled = usePoll(fetchMode, MODE_REFRESH_INTERVAL_MS, MODE_REQUEST_TIMEOUT_MS);
  const [pushed, setPushed] = useState<{ data: Mode; at: number } | null>(null);

  useEffect(() => {
    const listener: Listener = (mode, at) => setPushed({ data: mode, at });
    modeListeners.add(listener);
    return () => {
      modeListeners.delete(listener);
    };
  }, []);

  return useNewest(polled, pushed);
}
