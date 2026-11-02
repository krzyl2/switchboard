import { afterEach, describe, expect, it, vi } from 'vitest';
import { getJson, request, withTimeout } from './http';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

function stub(status: number, body: unknown = {}) {
  const fetchMock = vi.fn(() => Promise.resolve(new Response(JSON.stringify(body), { status })));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

// Every API client goes through here: the add-on answers JSON, must never be served from a cache, and
// accepts a body only as JSON (a cross-site form cannot send that without a preflight).
describe('request', () => {
  it('reads without a cache, asking for JSON, with no method and no body', async () => {
    const fetchMock = stub(200);
    await request('api/x');
    expect(fetchMock).toHaveBeenCalledWith('api/x', { signal: undefined, cache: 'no-store', headers: { Accept: 'application/json' } });
  });

  it('sends a body as JSON with its content type', async () => {
    const fetchMock = stub(200);
    await request('api/x', { method: 'POST', body: { a: 1 } });
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.headers).toEqual({ Accept: 'application/json', 'Content-Type': 'application/json' });
    expect(init.body).toBe('{"a":1}');
  });
});

describe('getJson', () => {
  it('returns the body of a 2xx answer', async () => {
    stub(200, { ok: true });
    expect(await getJson('api/x', undefined, 'X')).toEqual({ ok: true });
  });

  it('names what was asked and the status of any other answer', async () => {
    stub(503);
    await expect(getJson('api/x', undefined, 'Plan')).rejects.toThrow('Plan request failed with HTTP 503.');
  });
});

describe('withTimeout', () => {
  it('aborts a request that has not settled in time and clears its timer when it has', async () => {
    vi.useFakeTimers();
    let seen: AbortSignal | undefined;
    const slow = withTimeout((signal) => {
      seen = signal;
      return new Promise<void>(() => undefined);
    }, 100);
    void slow;
    vi.advanceTimersByTime(100);
    expect(seen?.aborted).toBe(true);

    await withTimeout(() => Promise.resolve(1), 100);
    expect(vi.getTimerCount()).toBe(0);
  });
});
