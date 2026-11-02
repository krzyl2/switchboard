import { useCallback } from 'preact/hooks';
import type { OverrideMode } from './api/decisions';
import { fetchDispatch, OverrideError, postOverride, type DispatchStatus, type OverrideRefusal } from './api/dispatch';
import { withTimeout } from './api/http';
import { DISPATCH_REFRESH_INTERVAL_MS, DISPATCH_REQUEST_TIMEOUT_MS, OVERRIDE_REQUEST_TIMEOUT_MS } from './config';
import { usePollControl, type PollView } from './usePoll';

export interface DispatchControl {
  view: PollView<DispatchStatus>;
  /** Sets a device's override; resolves to null when the add-on evaluated it, or to why it did not. */
  setOverride: (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null>;
}

/**
 * GET api/dispatch. An override's answer is the status after the add-on evaluated it, so it replaces
 * the view at once instead of waiting for the next poll, and it outranks a poll that was already in
 * flight: that answer was asked before the change and would put the old state back.
 */
export function useDispatch(): DispatchControl {
  const { view, push } = usePollControl(fetchDispatch, DISPATCH_REFRESH_INTERVAL_MS, DISPATCH_REQUEST_TIMEOUT_MS);

  const setOverride = useCallback(
    async (device: string, mode: OverrideMode): Promise<OverrideRefusal | null> => {
      try {
        push(await withTimeout((signal) => postOverride(device, mode, signal), OVERRIDE_REQUEST_TIMEOUT_MS));
        return null;
      } catch (error) {
        return error instanceof OverrideError ? error.refusal : 'failed';
      }
    },
    [push],
  );

  return { view, setOverride };
}
