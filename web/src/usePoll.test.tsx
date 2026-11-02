import { act, renderHook } from '@testing-library/preact';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { usePollControl } from './usePoll';

const INTERVAL = 1000;
const TIMEOUT = 500;

interface Pending {
  signal: AbortSignal;
  resolve: (value: string) => void;
  reject: (error: Error) => void;
}

// Every request of the fetcher stays open until the test answers it, so the order of the answers is the test's to choose.
function deferredFetcher() {
  const requests: Pending[] = [];
  const fetcher = (signal: AbortSignal) =>
    new Promise<string>((resolve, reject) => {
      requests.push({ signal, resolve, reject });
    });
  return { requests, fetcher };
}

let hidden = false;

function setHidden(value: boolean): void {
  hidden = value;
  document.dispatchEvent(new Event('visibilitychange'));
}

beforeEach(() => {
  hidden = false;
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden });
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
});

afterEach(() => {
  vi.useRealTimers();
});

async function answer(request: Pending, value: string): Promise<void> {
  await act(async () => {
    request.resolve(value);
    await Promise.resolve();
  });
}

async function fail(request: Pending): Promise<void> {
  await act(async () => {
    request.reject(new Error('down'));
    await Promise.resolve();
  });
}

describe('usePollControl', () => {
  it('asks again one interval after the previous answer, never earlier', async () => {
    const { requests, fetcher } = deferredFetcher();
    renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    expect(requests).toHaveLength(1);

    await act(async () => void vi.advanceTimersByTime(5 * INTERVAL));
    expect(requests).toHaveLength(1); // a slow add-on never gets overlapping requests

    await answer(requests[0]!, 'a');
    await act(async () => void vi.advanceTimersByTime(INTERVAL));
    expect(requests).toHaveLength(2);
  });

  // The tab nobody looks at must not keep the add-on busy.
  it('stops asking while the tab is hidden and reads at once when it is visible again', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    await answer(requests[0]!, 'a');

    act(() => setHidden(true));
    await act(async () => void vi.advanceTimersByTime(10 * INTERVAL));
    expect(requests).toHaveLength(1);

    act(() => setHidden(false));
    expect(requests).toHaveLength(2); // immediately, not after an interval
    await answer(requests[1]!, 'b');
    expect(result.current.view).toMatchObject({ kind: 'live', data: 'b' });
  });

  it('does not schedule the next request when the answer arrives while hidden', async () => {
    const { requests, fetcher } = deferredFetcher();
    renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    act(() => setHidden(true));
    await answer(requests[0]!, 'a');
    await act(async () => void vi.advanceTimersByTime(10 * INTERVAL));
    expect(requests).toHaveLength(1);
  });

  // An answer that was asked for first but arrives last describes the older state of the add-on.
  it('ignores a response that arrives after a newer request was answered', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));

    act(() => result.current.refresh());
    expect(requests).toHaveLength(2);
    expect(requests[0]!.signal.aborted).toBe(true);

    await answer(requests[1]!, 'new');
    await answer(requests[0]!, 'old');
    expect(result.current.view).toMatchObject({ kind: 'live', data: 'new' });
  });

  it('ignores the failure of a request that a newer one replaced', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    act(() => result.current.refresh());
    await answer(requests[1]!, 'new');
    await fail(requests[0]!);
    expect(result.current.view.kind).toBe('live');
  });

  it('shows a pushed answer and drops the request that was already in flight', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));

    act(() => result.current.push('pushed'));
    await answer(requests[0]!, 'older');

    expect(result.current.view).toMatchObject({ kind: 'live', data: 'pushed' });
    await act(async () => void vi.advanceTimersByTime(INTERVAL));
    expect(requests).toHaveLength(2); // the loop goes on after a push
  });

  it('withdraws the value on a failure and keeps the time of the last success', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    await answer(requests[0]!, 'a');
    const succeededAt = (result.current.view as { updatedAt: Date }).updatedAt;

    await act(async () => void vi.advanceTimersByTime(INTERVAL));
    await fail(requests[1]!);
    expect(result.current.view).toEqual({ kind: 'unavailable', lastSuccessAt: succeededAt });
  });

  it('aborts a request that outlives its timeout', async () => {
    const { requests, fetcher } = deferredFetcher();
    renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    await act(async () => void vi.advanceTimersByTime(TIMEOUT));
    expect(requests[0]!.signal.aborted).toBe(true);
  });

  it('aborts the request in flight and stops asking when the component unmounts', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { unmount } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT));
    unmount();
    expect(requests[0]!.signal.aborted).toBe(true);

    await act(async () => void vi.advanceTimersByTime(10 * INTERVAL));
    act(() => setHidden(true));
    act(() => setHidden(false));
    expect(requests).toHaveLength(1);
  });

  it('starts over when the key changes and shows loading until the new key answers', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result, rerender } = renderHook(({ key }) => usePollControl(fetcher, INTERVAL, TIMEOUT, { key }), { initialProps: { key: 'a' } });
    await answer(requests[0]!, 'a');
    rerender({ key: 'b' });
    expect(requests[0]!.signal.aborted).toBe(true);
    expect(requests).toHaveLength(2);
    expect(result.current.view.kind).toBe('loading');
  });

  it('asks nothing while disabled', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT, { enabled: false }));
    await act(async () => void vi.advanceTimersByTime(10 * INTERVAL));
    expect(requests).toHaveLength(0);
    expect(result.current.view.kind).toBe('loading');
  });

  it('ends the polling when continueWhile says so, until a refresh reads again', async () => {
    const { requests, fetcher } = deferredFetcher();
    const { result } = renderHook(() => usePollControl(fetcher, INTERVAL, TIMEOUT, { continueWhile: (data) => data !== 'done' }));
    await answer(requests[0]!, 'done');
    await act(async () => void vi.advanceTimersByTime(10 * INTERVAL));
    act(() => setHidden(true));
    act(() => setHidden(false));
    expect(requests).toHaveLength(1);

    act(() => result.current.refresh());
    expect(requests).toHaveLength(2);
  });
});
