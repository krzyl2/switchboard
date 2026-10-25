import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { chooseMode, parseMode } from './mode';
import sample from './mode.sample.json';

// MODE-02: the mode is shown honestly (ceiling, UI choice, effective, note); active is refused by
// the add-on in this version, and the page must say so rather than pretend it was chosen.
let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function respond(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) } as Response);
}

describe('mode', () => {
  it('accepts the documented payload', () => {
    expect(parseMode(structuredClone(sample))).toEqual({ ceiling: 'shadow', uiChoice: 'shadow', effective: 'shadow', note: 'none' });
  });

  it.each([
    ['an unknown mode', (p: any) => (p.effective = 'live')],
    ['an unknown note', (p: any) => (p.note = 'later')],
    ['a missing ceiling', (p: any) => delete p.ceiling],
  ])('refuses %s', (_case, change) => {
    const payload = structuredClone(sample);
    change(payload);
    expect(() => parseMode(payload)).toThrow();
  });

  it('posts the choice as JSON and reports a refusal of active', async () => {
    fetchMock.mockReturnValue(respond({ code: 'active_not_enabled', message: 'not in this version' }, 409));

    expect(await chooseMode('active')).toEqual({ kind: 'refused', code: 'active_not_enabled' });
    const [, init] = fetchMock.mock.calls[0]!;
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ uiChoice: 'active' });
  });
});
