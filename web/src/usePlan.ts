import { useCallback, useEffect, useRef, useState } from 'preact/hooks';
import { deleteGoal, GoalError, postGoal, type GoalRefusal, type WantNow } from './api/goals';
import { fetchPlan, type Plan } from './api/plan';
import { GOAL_REQUEST_TIMEOUT_MS, PLAN_REFRESH_INTERVAL_MS, PLAN_REQUEST_TIMEOUT_MS } from './config';
import type { PollView } from './usePoll';

export interface PlanControl {
  view: PollView<Plan>;
  /** Stores a manual goal; resolves to null when the add-on took it, or to why it did not. The plan is read again at once. */
  want: (goal: WantNow) => Promise<GoalRefusal | null>;
  /** Cancels the device's manual goal; resolves to null when it is gone, or to why it is not. */
  cancel: (device: string) => Promise<GoalRefusal | null>;
}

/**
 * Polls GET api/plan the way usePoll does (one request at a time, the next scheduled after the previous settles, a failed or unparseable
 * answer withdraws every value) and adds the manual goal: after "want now" or a cancel the plan is read again at once instead of waiting
 * for the next poll, so the row shows what the add-on now holds. The plan itself changes later, when the planner has looked again.
 */
export function usePlan(): PlanControl {
  const [view, setView] = useState<PollView<Plan>>({ kind: 'loading' });
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
      const timeout = setTimeout(() => controller.abort(), PLAN_REQUEST_TIMEOUT_MS);
      try {
        const data = await fetchPlan(controller.signal);
        if (disposed.current || inFlight !== controller) return;
        lastSuccessAt = new Date();
        setView({ kind: 'live', data, updatedAt: lastSuccessAt });
      } catch {
        if (disposed.current || inFlight !== controller) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      if (inFlight === controller) nextPoll = setTimeout(run, PLAN_REFRESH_INTERVAL_MS);
    };

    poll.current = () => void run();
    void run();

    return () => {
      disposed.current = true;
      clearTimeout(nextPoll);
      inFlight?.abort();
    };
  }, []);

  const send = useCallback(async (request: (signal: AbortSignal) => Promise<unknown>): Promise<GoalRefusal | null> => {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), GOAL_REQUEST_TIMEOUT_MS);
    try {
      await request(controller.signal);
      if (!disposed.current) poll.current();
      return null;
    } catch (error) {
      return error instanceof GoalError ? error.refusal : 'failed';
    } finally {
      clearTimeout(timeout);
    }
  }, []);

  const want = useCallback((goal: WantNow) => send((signal) => postGoal(goal, signal)), [send]);
  const cancel = useCallback((device: string) => send((signal) => deleteGoal(device, signal)), [send]);

  return { view, want, cancel };
}
