import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { fetchDecisionHistory, parseDecisionHistory } from './decisionHistory';
import sample from './decision-history.sample.json';

// MODE-04: the decision log over its whole retention. The page pages with the add-on's opaque
// cursor and shows how far back the retained files go.
let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('decision history', () => {
  it('accepts the documented payload', () => {
    const parsed = parseDecisionHistory(structuredClone(sample));

    expect(parsed.items).toHaveLength(2);
    expect(parsed.next).toBe(sample.next);
    expect(parsed.retainedFrom).toBe('2026-10-22');
    expect(parsed.retentionDays).toBe(30);
    expect(parsed.partial).toBe(false);
    expect(parsed.logWritable).toBe(true);
  });

  it.each([
    ['a decision with an unknown reason', (p: any) => (p.items[0].reason = 'made_up')],
    ['a missing partial flag', (p: any) => delete p.partial],
    ['a negative retention', (p: any) => (p.retentionDays = -1)],
  ])('refuses %s', (_case, change) => {
    const payload = structuredClone(sample);
    change(payload);
    expect(() => parseDecisionHistory(payload)).toThrow();
  });

  it('asks with the filters, local-day bounds as UTC instants and the cursor', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: () => Promise.resolve(structuredClone(sample)) } as Response);

    await fetchDecisionHistory(new AbortController().signal, {
      device: 'heater_l1',
      kind: 'protection',
      from: new Date('2026-10-21T22:00:00Z'),
      to: new Date('2026-10-22T22:00:00Z'),
      cursor: '20261022:7',
    });

    expect(String(fetchMock.mock.calls[0]![0])).toBe(
      'api/decisions/history?device=heater_l1&kind=protection&from=2026-10-21T22%3A00%3A00.000Z&to=2026-10-22T22%3A00%3A00.000Z&cursor=20261022%3A7',
    );
  });
});
