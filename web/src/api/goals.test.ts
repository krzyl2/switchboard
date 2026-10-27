import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { deleteGoal, GOAL_REFUSALS, GoalError, parseStoredGoal, postGoal } from './goals';

// PLAN-19: the "want now" action. The request must be exactly what the add-on accepts (one JSON body of five keys, an ISO instant, nothing
// else), and a refusal must reach the screen as the add-on's own closed code so it can be worded; any other failure is a plain failure.
let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const stored = { device: 'ev_charger', kind: 'energy', target: 8, deadline: '2026-10-27T05:30:00Z', hard: true, since: '2026-10-26T17:00:00Z', deliveredKwh: 0, created: true };

const answer = (body: unknown, status = 200) => Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) } as Response);

const wish = { device: 'ev_charger', kind: 'energy' as const, target: 8, deadline: new Date('2026-10-27T05:30:00Z'), hard: true };

describe('postGoal', () => {
  it('posts the five keys as JSON to the relative url and parses what was stored', async () => {
    fetchMock.mockReturnValue(answer(stored));

    const goal = await postGoal(wish);

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('api/goals');
    expect(init!.method).toBe('POST');
    expect((init!.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(String(init!.body))).toEqual({ device: 'ev_charger', kind: 'energy', target: 8, deadline: '2026-10-27T05:30:00.000Z', hard: true });
    expect(goal).toMatchObject({ device: 'ev_charger', kind: 'energy', target: 8, hard: true, created: true });
    expect(goal.deadline.toISOString()).toBe('2026-10-27T05:30:00.000Z');
  });

  it.each(GOAL_REFUSALS)('turns the add-on refusal %s into a GoalError with that code', async (code) => {
    fetchMock.mockReturnValue(answer({ code, message: 'why' }, 422));

    await expect(postGoal({ ...wish, kind: 'soc', target: 80, hard: false })).rejects.toMatchObject({ refusal: code });
  });

  it('treats a refusal with a code it does not know, or an answer that is not JSON, as a plain failure', async () => {
    fetchMock.mockReturnValueOnce(answer({ code: 'teapot', message: 'x' }, 418));
    await expect(postGoal(wish)).rejects.toMatchObject({ refusal: 'failed' });

    fetchMock.mockReturnValueOnce(Promise.resolve({ ok: false, status: 502, json: () => Promise.reject(new Error('html')) } as Response));
    await expect(postGoal(wish)).rejects.toBeInstanceOf(GoalError);
  });

  it('refuses an answer that is not what was stored', async () => {
    fetchMock.mockReturnValue(answer({ ...stored, kind: 'power' }));

    await expect(postGoal(wish)).rejects.toThrow();
  });
});

describe('deleteGoal', () => {
  it('sends a DELETE for the device and resolves when it is gone', async () => {
    fetchMock.mockReturnValue(answer({ device: 'ev_charger', ended: true }));

    await deleteGoal('ev_charger');

    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('api/goals/ev_charger');
    expect(init!.method).toBe('DELETE');
  });

  it('rejects with the refusal code when there is nothing to cancel', async () => {
    fetchMock.mockReturnValue(answer({ code: 'no_manual_goal', message: 'none' }, 404));

    await expect(deleteGoal('ev_charger')).rejects.toMatchObject({ refusal: 'no_manual_goal' });
  });
});

describe('parseStoredGoal', () => {
  it('rejects a missing key', () => {
    const { created: _created, ...without } = stored;
    expect(() => parseStoredGoal(without)).toThrow();
  });
});
