import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { confirmState, parseControl, setControlAllowed } from './control';
import sample from './control.sample.json';

// MODE-02: the global "control allowed" switch. The page must show it exactly as the add-on reports
// it: an "allowed" next to a lock, a lock without its reason, or a mode that disagrees with the
// switch would tell the user that commands go out when they do not (or the other way round).
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

function copy(change?: (p: any) => void): unknown {
  const payload = structuredClone(sample) as any;
  change?.(payload);
  return payload;
}

/** A running installation with control on: a failed command, a device that did not follow, one left reduced. */
function running(p: any): void {
  p.controlAllowed = true;
  p.changedAt = '2026-10-25T08:00:00+00:00';
  p.source = 'api';
  p.mode = { ...p.mode, ceiling: 'active', controlAllowed: true, configurationValid: true, effective: 'active', missing: [] };
  p.observation = { since: '2026-10-21T06:00:00+00:00', configurationErrors: 2, lastConfigurationErrorAt: '2026-10-23T10:00:00+00:00' };
  p.commands.lastAcknowledgedAt = '2026-10-25T08:01:00+00:00';
  p.commands.lastFailure = { device: 'ev_charger', at: '2026-10-25T08:01:30+00:00', outcome: 'timed_out', notSent: null, detail: 'no answer within 00:00:05' };
  p.commands.unconfirmed = [{ device: 'heater_l2', since: '2026-10-25T08:01:10+00:00' }];
  p.leftReduced = [{ device: 'heater_l1', level: { relays: [true], setpointA: null, enabled: true }, since: '2026-10-25T07:59:00+00:00' }];
}

describe('control', () => {
  it('accepts the documented payload: off after install, nothing locked', () => {
    const control = parseControl(copy());

    expect(control).toMatchObject({ controlAllowed: false, source: 'default', locked: false, lockReason: null });
    expect(control.mode.missing).toContain('control_off');
    expect(control.leftReduced).toEqual([]);
  });

  it('reads the command path and the devices left reduced', () => {
    const control = parseControl(copy(running));

    expect(control.mode.effective).toBe('active');
    expect(control.observation?.configurationErrors).toBe(2);
    expect(control.commands.lastFailure).toMatchObject({ device: 'ev_charger', outcome: 'timed_out', notSent: null });
    expect(control.commands.unconfirmed.map((u) => u.device)).toEqual(['heater_l2']);
    expect(control.leftReduced[0]).toMatchObject({ device: 'heater_l1', level: { relays: [true] } });
  });

  it('reads a lock after an untrusted state.json', () => {
    const control = parseControl(copy((p) => Object.assign(p, { locked: true, lockReason: 'state_file_corrupt', mode: { ...p.mode, stateConfirmed: false, missing: [...p.mode.missing, 'state_unconfirmed'] } })));

    expect(control).toMatchObject({ locked: true, lockReason: 'state_file_corrupt', controlAllowed: false });
  });

  it.each([
    ['allowed while locked', (p: any) => Object.assign(p, { locked: true, lockReason: 'state_file_corrupt', controlAllowed: true, mode: { ...p.mode, controlAllowed: true } })],
    ['a lock without its reason', (p: any) => (p.locked = true)],
    ['a reason without a lock', (p: any) => (p.lockReason = 'state_file_corrupt')],
    ['a mode that disagrees with the switch', (p: any) => (p.mode.controlAllowed = true)],
    ['an unknown source', (p: any) => (p.source = 'browser')],
    ['an unknown lock reason', (p: any) => Object.assign(p, { locked: true, lockReason: 'disk_full' })],
    ['a failure reported as acknowledged', (p: any) => {
      running(p);
      p.commands.lastFailure.outcome = 'acknowledged';
    }],
    ['an unsent command without its reason', (p: any) => {
      running(p);
      p.commands.lastFailure.outcome = 'not_sent';
    }],
    ['a reason next to a sent command', (p: any) => {
      running(p);
      p.commands.lastFailure.notSent = 'link_down';
    }],
    ['an unknown not-sent reason', (p: any) => {
      running(p);
      Object.assign(p.commands.lastFailure, { outcome: 'not_sent', notSent: 'busy' });
    }],
    ['a missing commands block', (p: any) => delete p.commands],
    ['a left-reduced device without its level', (p: any) => {
      running(p);
      delete p.leftReduced[0].level;
    }],
  ])('refuses %s', (_case, change) => {
    expect(() => parseControl(copy(change))).toThrow();
  });

  it('turns control on with a JSON post and returns the evaluated answer', async () => {
    fetchMock.mockReturnValue(respond(copy(running)));

    const change = await setControlAllowed(true);

    expect(change.kind).toBe('applied');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('api/control');
    expect(init?.method).toBe('POST');
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ allowed: true });
  });

  it('reports a refusal while the state waits for a confirmation', async () => {
    fetchMock.mockReturnValue(respond({ code: 'state_unconfirmed', message: 'confirm first' }, 409));

    expect(await setControlAllowed(true)).toEqual({ kind: 'refused', code: 'state_unconfirmed' });
  });

  it('confirms the state with a JSON post', async () => {
    fetchMock.mockReturnValue(respond(copy()));

    expect((await confirmState()).kind).toBe('applied');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe('api/control/confirm');
    expect(JSON.parse(String(init?.body))).toEqual({ confirm: true });
  });

  it('fails loudly on an unexpected status', async () => {
    fetchMock.mockReturnValue(respond({}, 500));

    await expect(setControlAllowed(false)).rejects.toThrow();
  });
});
