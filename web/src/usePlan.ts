import { useCallback } from 'preact/hooks';
import { deleteGoal, GoalError, postGoal, type GoalRefusal, type WantNow } from './api/goals';
import { withTimeout } from './api/http';
import { fetchPlan, type Plan } from './api/plan';
import { GOAL_REQUEST_TIMEOUT_MS, PLAN_REFRESH_INTERVAL_MS, PLAN_REQUEST_TIMEOUT_MS } from './config';
import { usePollControl, type PollView } from './usePoll';

export interface PlanControl {
  view: PollView<Plan>;
  /** Stores a manual goal; resolves to null when the add-on took it, or to why it did not. The plan is read again at once. */
  want: (goal: WantNow) => Promise<GoalRefusal | null>;
  /** Cancels the device's manual goal; resolves to null when it is gone, or to why it is not. */
  cancel: (device: string) => Promise<GoalRefusal | null>;
}

/**
 * GET api/plan plus the manual goal: after "want now" or a cancel the plan is read again at once instead of waiting
 * for the next poll, so the row shows what the add-on now holds. The plan itself changes later, when the planner has looked again.
 */
export function usePlan(): PlanControl {
  const { view, refresh } = usePollControl(fetchPlan, PLAN_REFRESH_INTERVAL_MS, PLAN_REQUEST_TIMEOUT_MS);

  const send = useCallback(
    async (request: (signal: AbortSignal) => Promise<unknown>): Promise<GoalRefusal | null> => {
      try {
        await withTimeout(request, GOAL_REQUEST_TIMEOUT_MS);
        refresh();
        return null;
      } catch (error) {
        return error instanceof GoalError ? error.refusal : 'failed';
      }
    },
    [refresh],
  );

  const want = useCallback((goal: WantNow) => send((signal) => postGoal(goal, signal)), [send]);
  const cancel = useCallback((device: string) => send((signal) => deleteGoal(device, signal)), [send]);

  return { view, want, cancel };
}
