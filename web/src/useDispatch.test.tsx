import { act, renderHook } from '@testing-library/preact';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import sample from './api/dispatch.sample.json';
import { useDispatch } from './useDispatch';

// The mark is the status' timestamp: the page keeps it as a Date, so a view is told apart by it.
function status(mark: string) {
  const copy = JSON.parse(JSON.stringify(sample));
  copy.at = mark;
  return copy;
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

// The bug: a poll asked before an override and answered after it put the state from before the override
// back on the card, until the next poll. The override's answer is newer than any request already in flight.
describe('useDispatch', () => {
  it('keeps the answer to an override when an older poll answers after it', async () => {
    let answerPoll: (response: Response) => void = () => undefined;
    const fetchMock = vi.fn((_input: RequestInfo | URL, init?: RequestInit) =>
      init?.method === 'POST'
        ? Promise.resolve(json(status('2026-10-24T12:05:00+00:00')))
        : new Promise<Response>((resolve) => {
            answerPoll = resolve;
          }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const { result } = renderHook(() => useDispatch());
    let refusal: unknown;
    await act(async () => {
      refusal = await result.current.setOverride('ev_charger', 'force_off');
    });
    expect(refusal).toBeNull();
    expect(result.current.view).toMatchObject({ kind: 'live', data: { at: new Date('2026-10-24T12:05:00+00:00') } });

    await act(async () => {
      answerPoll(json(status('2026-10-24T12:00:00+00:00')));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(result.current.view).toMatchObject({ kind: 'live', data: { at: new Date('2026-10-24T12:05:00+00:00') } });
  });
});
