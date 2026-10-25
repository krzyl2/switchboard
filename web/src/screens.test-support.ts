import { act } from '@testing-library/preact';
import { vi, type Mock } from 'vitest';

// Shared by the screen tests: a fetch double serving sample payloads by URL (anything else is a
// request the page must not make: 404), and a virtual clock advance that lets fetches settle.

export function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) } as Response);
}

export function serveSamples(fetchMock: Mock<typeof fetch>, samples: Record<string, unknown>): void {
  fetchMock.mockImplementation((input) => {
    const url = String(input).split('?')[0]!;
    return url in samples ? respond(structuredClone(samples[url])) : respond({}, 404);
  });
}

export async function advance(ms = 0): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}
