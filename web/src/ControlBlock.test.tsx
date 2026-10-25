import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import controlSample from './api/control.sample.json';
import type { Status } from './api/status';
import { CONTROL_REFRESH_INTERVAL_MS } from './config';
import { advance, respond } from './screens.test-support';

// MODE-02 on the Status page: the control block shows the add-on option (read-only), the global
// "control allowed" switch, the mode in force and exactly which condition keeps it in shadow, the
// observation advice, the lock after an untrusted state.json and the command path. The rules under
// test: allowing control takes a confirmation and nothing is sent before it; switching off is sent
// at once and leaves the devices as they are; the page never shows a mode the add-on did not report;
// a locked switch cannot be turned on and the lock is released only by the explicit confirmation;
// the week in shadow is advice, never enforced.

const START = new Date('2026-10-25T10:00:00Z');
const DAY_MS = 86_400_000;

const STATUS: Status = { version: '0.7.0', mode: 'shadow', homeAssistant: 'connected', mqtt: 'connected', healthy: true };

type Payload = typeof controlSample & Record<string, unknown>;

let fetchMock: Mock<typeof fetch>;
let control: Payload;
let posts: { url: string; body: unknown; contentType: string | null }[];
let answerPost: (url: string, body: unknown) => Promise<Response>;

/** The sample (fresh install: option shadow, control off, configuration invalid), changed per test. */
function payload(change?: (p: any) => void): Payload {
  const p = structuredClone(controlSample) as any;
  change?.(p);
  return p;
}

/** Option active, valid configuration, control off: the switch is the only thing missing. */
function optionActive(p: any): void {
  p.mode = { ...p.mode, ceiling: 'active', configurationValid: true, missing: ['control_off'] };
}

/** Everything holds: commands go out. */
function inForce(p: any): void {
  p.controlAllowed = true;
  p.changedAt = '2026-10-25T09:00:00+00:00';
  p.source = 'api';
  p.mode = { ...p.mode, ceiling: 'active', controlAllowed: true, configurationValid: true, effective: 'active', missing: [] };
}

/** state.json could not be trusted at start: locked off, kill switch on, until confirmed. */
function locked(p: any): void {
  p.locked = true;
  p.changedAt = '2026-10-25T09:59:00+00:00';
  p.lockReason = 'state_file_corrupt';
  p.mode = { ...p.mode, ceiling: 'active', configurationValid: true, stateConfirmed: false, missing: ['control_off', 'state_unconfirmed'] };
}

/** Serves the Status page (api/status, api/control) and the shell's banner (api/mode); POSTs go to answerPost. */
function serve() {
  fetchMock.mockImplementation((input, init) => {
    const url = String(input);
    if (init?.method === 'POST') {
      const body = JSON.parse(String(init.body)) as unknown;
      posts.push({ url, body, contentType: new Headers(init.headers).get('Content-Type') });
      return answerPost(url, body);
    }
    if (url === 'api/status') return respond(structuredClone(STATUS));
    if (url === 'api/control') return respond(structuredClone(control));
    if (url === 'api/mode') return respond(structuredClone(control.mode));
    return respond({}, 404);
  });
}

async function open(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/status';
  render(<App lang={lang} />);
  await advance();
}

function block(): HTMLElement {
  return screen.getByRole('heading', { name: /^(Control|Sterowanie)$/ }).closest('section') as HTMLElement;
}

function fact(name: string): HTMLElement {
  return block().querySelector(`[data-fact="${name}"]`) as HTMLElement;
}

const controlSwitch = () => within(block()).getByRole('switch', { name: 'Control allowed' }) as HTMLButtonElement;
const banner = () => document.querySelector('.pagehead [data-mode]') as HTMLElement;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(START);
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
  control = payload();
  posts = [];
  answerPost = () => respond({}, 500);
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('mode in force', () => {
  // The UI states exactly which condition is missing, each in words, in the add-on's order.
  it('names every condition that keeps the mode in shadow', async () => {
    await open();

    expect(block().getAttribute('data-effective')).toBe('shadow');
    expect(fact('effective').textContent).toContain('Shadow');
    expect(fact('effective').textContent).toContain('Computes and logs, never switches.');
    const missing = [...block().querySelectorAll('[data-condition]')];
    expect(missing.map((m) => m.getAttribute('data-condition'))).toEqual(['option_shadow', 'control_off', 'configuration_invalid']);
    expect(missing[0]?.textContent).toContain('Set it to active in the add-on’s Configuration tab in Home Assistant and restart the add-on.');
    expect(missing[1]?.textContent).toBe('Control allowed is off.');
    expect(within(missing[2] as HTMLElement).getByRole('link', { name: 'Signals' }).getAttribute('href')).toBe('#/signals');
  });

  it('reads active only when the add-on reports it, with nothing missing', async () => {
    control = payload(inForce);
    await open();

    expect(block().getAttribute('data-effective')).toBe('active');
    expect(fact('effective').textContent).toContain('Active');
    expect(fact('effective').textContent).toContain('Sends commands to the declared actuators.');
    expect(block().querySelector('[data-condition]')).toBeNull();
  });

  // Control allowed with an invalid configuration is not "nothing is sent": the add-on keeps sending
  // the safe-state switch-offs to the devices of the last valid file, and the block says so.
  it('says only switch-offs go out when control is allowed but the configuration is invalid', async () => {
    control = payload((p) => {
      inForce(p);
      p.mode = { ...p.mode, configurationValid: false, effective: 'shadow', missing: ['configuration_invalid'] };
    });
    await open();

    expect(block().getAttribute('data-effective')).toBe('offs_only');
    expect(fact('effective').textContent).toContain('Switch-offs only');
    expect(block().querySelector('[data-condition="configuration_invalid"]')?.textContent).toContain(
      'Until it is fixed, Switchboard sends only switch-offs and minimum setpoints to the devices of the last valid configuration, and raises nothing.',
    );
  });

  it('shows the add-on option as reported, read-only', async () => {
    control = payload(optionActive);
    await open();

    // The option's own value, not "Active": an active option alone is not active control.
    expect(fact('option').textContent).toContain('mode: active');
    expect(fact('option').textContent).not.toContain('Active');
    expect(fact('option').textContent).toContain('read-only here');
    expect(within(fact('option')).queryByRole('button')).toBeNull();
    expect(within(fact('option')).queryByRole('combobox')).toBeNull();
  });

  it('never shows a mode the add-on did not report: an unreadable answer withdraws the block', async () => {
    control = payload((p) => {
      p.mode = { ...p.mode, effective: 'active' };
    });
    await open();

    expect(within(block()).getByRole('alert').textContent).toContain('Unavailable');
    expect(block().hasAttribute('data-effective')).toBe(false);
    expect(within(block()).queryByRole('switch')).toBeNull();
  });
});

describe('control allowed switch', () => {
  it('allowing control asks first, and nothing is sent until it is confirmed', async () => {
    control = payload(optionActive);
    await open();
    expect(controlSwitch().getAttribute('aria-checked')).toBe('false');

    fireEvent.click(controlSwitch());
    await advance();

    const confirm = within(block()).getByRole('group', { name: 'Allow Switchboard to switch your devices?' });
    expect(confirm.textContent).toContain('sends commands to the actuators you declared and to nothing else');
    expect(confirm.textContent).toContain('Switchboard is not a protective device');
    expect(document.activeElement).toBe(within(confirm).getByRole('button', { name: 'Allow control' }));
    expect(posts).toEqual([]);

    fireEvent.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    await advance();
    expect(within(block()).queryByRole('group')).toBeNull();
    expect(controlSwitch().getAttribute('aria-checked')).toBe('false');
    expect(posts).toEqual([]);
  });

  it('a confirmed allow sends exactly {"allowed":true} as JSON, and the answer shows at once, in the block and the banner', async () => {
    control = payload(optionActive);
    answerPost = () => respond(payload(inForce));
    await open();
    expect(banner().getAttribute('data-mode')).toBe('shadow');

    fireEvent.click(controlSwitch());
    await advance();
    fireEvent.click(within(block()).getByRole('button', { name: 'Allow control' }));
    await advance();

    expect(posts).toEqual([{ url: 'api/control', body: { allowed: true }, contentType: 'application/json' }]);
    // The answer was evaluated by the add-on: shown without waiting for the next poll.
    expect(controlSwitch().getAttribute('aria-checked')).toBe('true');
    expect(block().getAttribute('data-effective')).toBe('active');
    expect(banner().getAttribute('data-mode')).toBe('active');
    expect(fact('switch').textContent).toContain('changed in this panel or through the API');
  });

  // Off is the direction that sends nothing to any device, so it needs no confirmation, and the
  // block says the devices stay exactly where they are.
  it('switching off is sent at once, and says it leaves every device as it is', async () => {
    control = payload(inForce);
    answerPost = () => respond(payload(optionActive));
    await open();
    expect(fact('switch').textContent).toContain('Switching control off sends nothing: every device stays exactly as it is');

    fireEvent.click(controlSwitch());
    await advance();

    expect(within(block()).queryByRole('group')).toBeNull();
    expect(posts).toEqual([{ url: 'api/control', body: { allowed: false }, contentType: 'application/json' }]);
    expect(controlSwitch().getAttribute('aria-checked')).toBe('false');
    expect(block().getAttribute('data-effective')).toBe('shadow');
  });

  it('under a shadow option the confirmation says nothing is switched until the option is active', async () => {
    control = payload((p) => {
      p.mode = { ...p.mode, configurationValid: true, missing: ['option_shadow', 'control_off'] };
    });
    await open();

    fireEvent.click(controlSwitch());
    await advance();

    expect(within(block()).getByRole('group').textContent).toContain('The add-on option is shadow, so nothing is switched until it is set to active');
  });

  it('a refused allow says why and reads the lock that refused it again', async () => {
    control = payload(optionActive);
    answerPost = () => {
      control = payload(locked);
      return respond({ code: 'state_unconfirmed', message: 'confirm first' }, 409);
    };
    await open();

    fireEvent.click(controlSwitch());
    await advance();
    fireEvent.click(within(block()).getByRole('button', { name: 'Allow control' }));
    await advance();

    expect(block().querySelector('[data-notice]')?.textContent).toBe('Not changed: confirm the state first (below). Control stays off until then.');
    expect(within(block()).getByRole('alert').textContent).toContain('state.json could not be read at start');
    expect(controlSwitch().disabled).toBe(true);
  });

  it('a change that cannot be sent says so, and the switch keeps what the add-on reports', async () => {
    control = payload(optionActive);
    answerPost = () => respond({}, 500);
    await open();

    fireEvent.click(controlSwitch());
    await advance();
    fireEvent.click(within(block()).getByRole('button', { name: 'Allow control' }));
    await advance();

    expect(block().querySelector('[data-notice="failed"]')?.textContent).toContain('The change could not be sent.');
    expect(controlSwitch().getAttribute('aria-checked')).toBe('false');
  });

  it('a switch changed from Home Assistant shows at the next poll, with who changed it', async () => {
    control = payload(optionActive);
    await open();

    control = payload((p) => {
      inForce(p);
      p.source = 'mqtt';
    });
    await advance(CONTROL_REFRESH_INTERVAL_MS);

    expect(controlSwitch().getAttribute('aria-checked')).toBe('true');
    expect(fact('switch').textContent).toContain('changed from Home Assistant');
    expect(fact('switch').querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-25T09:00:00.000Z');
  });

  it('a switch never changed reads off since the install', async () => {
    await open();

    expect(fact('switch').textContent).toContain('never changed: off since the install');
    expect(fact('switch').querySelector('time')).toBeNull();
  });
});

// Maintainer survey: a corrupt or unreadable state.json starts with control off and the kill switch
// on until the user confirms in the UI (an explicit, recorded action); confirming does not allow control.
describe('state lock', () => {
  it('locks the switch off until the state is confirmed, and confirming does not allow control', async () => {
    control = payload(locked);
    answerPost = (url) => {
      control = payload(optionActive);
      return url === 'api/control/confirm' ? respond(structuredClone(control)) : respond({}, 500);
    };
    await open();

    const lock = within(block()).getByRole('alert');
    expect(lock.textContent).toContain('state.json could not be read at start and was set aside as state.json.corrupt');
    expect(lock.textContent).toContain('the kill switch holds every controlled device off until you confirm');
    expect(controlSwitch().disabled).toBe(true);
    expect(fact('switch').textContent).toContain('Locked off until you confirm the state');
    // Set off by the add-on at the start, not "never changed".
    expect(fact('switch').textContent).not.toContain('never changed');
    expect(fact('switch').querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-25T09:59:00.000Z');
    expect(block().querySelector('[data-condition="state_unconfirmed"]')).not.toBeNull();

    fireEvent.click(within(lock).getByRole('button', { name: 'Confirm the state' }));
    await advance();

    expect(posts).toEqual([{ url: 'api/control/confirm', body: { confirm: true }, contentType: 'application/json' }]);
    expect(within(block()).queryByRole('alert')).toBeNull();
    expect(controlSwitch().disabled).toBe(false);
    expect(controlSwitch().getAttribute('aria-checked')).toBe('false');
  });

  it('a confirmation nothing waits for says so', async () => {
    control = payload(locked);
    answerPost = () => {
      control = payload(optionActive);
      return respond({ code: 'not_locked', message: 'Nothing waits for a confirmation.' }, 409);
    };
    await open();

    fireEvent.click(within(block()).getByRole('button', { name: 'Confirm the state' }));
    await advance();

    expect(block().querySelector('[data-notice="not_locked"]')?.textContent).toBe('Nothing waits for a confirmation any more.');
    expect(within(block()).queryByRole('alert')).toBeNull();
  });
});

// The week in shadow is advice: the block shows how long the installation has observed and whether
// configuration errors occurred, and allowing control stays possible before a week.
describe('observation advice', () => {
  it('counts whole days in the browser from the first start, with the configuration errors, and enforces nothing', async () => {
    control = payload((p) => {
      optionActive(p);
      p.observation = { since: new Date(START.getTime() - 2.5 * DAY_MS).toISOString(), configurationErrors: 2, lastConfigurationErrorAt: '2026-10-24T08:00:00+00:00' };
    });
    await open();

    const observing = fact('observing');
    expect(observing.textContent).toContain('2 days, since');
    expect(observing.querySelector('.state')?.getAttribute('data-tone')).toBe('warn');
    expect(observing.textContent).toContain('configuration errors: 2, last on');
    expect(observing.textContent).toContain('Advice, not a requirement');
    expect(controlSwitch().disabled).toBe(false);
  });

  it.each([
    [0.2, 'en', 'less than a day'],
    [1.5, 'en', '1 day'],
    [1.5, 'pl', '1 dzień'],
    [8, 'pl', '8 dni'],
  ] as const)('%s days in %s read "%s"', async (days, lang, text) => {
    control = payload((p) => {
      p.observation = { since: new Date(START.getTime() - days * DAY_MS).toISOString(), configurationErrors: 0, lastConfigurationErrorAt: null };
    });
    await open(lang);

    expect(fact('observing').textContent?.startsWith(`${text}, `)).toBe(true);
  });

  it('shows no observation row when the add-on has not recorded one', async () => {
    await open();
    expect(fact('observing')).toBeNull();
  });
});

describe('devices left reduced and the command path', () => {
  // Switching control off leaves shed devices off: the user sees which ones and to what level
  // Switchboard would bring them back.
  it('lists the devices Switchboard left reduced, and says they stay so while control is not in force', async () => {
    control = payload((p) => {
      optionActive(p);
      p.leftReduced = [
        { device: 'heater_l1', level: { relays: [true], setpointA: null, enabled: true }, since: '2026-10-25T09:30:00+00:00' },
        { device: 'ev_charger', level: { relays: null, setpointA: 16, enabled: true }, since: '2026-10-25T09:31:00+00:00' },
      ];
    });
    await open();

    const reduced = block().querySelector('[data-left-reduced]') as HTMLElement;
    expect(reduced.getAttribute('data-left-reduced')).toBe('heater_l1 ev_charger');
    expect(reduced.textContent).toContain('Control is not in force, so they stay as they are');
    expect(reduced.textContent).toContain('back to on, reduced since');
    expect(reduced.textContent).toContain('back to 16 A, reduced since');
    expect(within(reduced).getByRole('link', { name: 'heater_l1' }).getAttribute('href')).toBe('#/devices/heater_l1');
  });

  it('shows the link, the queue, the last accepted command, the last failure and devices that did not follow', async () => {
    control = payload((p) => {
      inForce(p);
      p.commands = {
        linkReady: true,
        pending: 1,
        lastAcknowledgedAt: '2026-10-25T09:58:00+00:00',
        lastFailure: { device: 'ev_charger', at: '2026-10-25T09:59:00+00:00', outcome: 'not_sent', notSent: 'link_down', detail: null },
        unconfirmed: [{ device: 'heater_l2', since: '2026-10-25T09:58:30+00:00' }],
      };
    });
    await open();

    const commands = screen.getByRole('heading', { name: 'Commands to Home Assistant' }).parentElement as HTMLElement;
    expect(commands.querySelector('[data-link]')?.textContent).toContain('connection ready');
    expect(commands.textContent).toContain('waiting to be sent or answered: 1');
    expect(commands.textContent).toContain('last accepted at');
    expect(commands.querySelector('[data-failure="not_sent"]')?.textContent).toContain('not sent: no connection to Home Assistant');
    const unconfirmed = commands.querySelector('[data-unconfirmed]') as HTMLElement;
    expect(unconfirmed.getAttribute('data-unconfirmed')).toBe('heater_l2');
    expect(unconfirmed.textContent).toContain('Home Assistant accepted the command, but the state the device reports has not changed.');
  });

  it('a fresh install has accepted no command', async () => {
    await open();

    const commands = screen.getByRole('heading', { name: 'Commands to Home Assistant' }).parentElement as HTMLElement;
    expect(commands.textContent).toContain('none accepted yet');
    expect(commands.querySelector('[data-failure]')).toBeNull();
  });
});

describe('Polish', () => {
  it('uses the mockup vocabulary: Sterowanie dozwolone, Obserwacja, Sterowanie aktywne', async () => {
    control = payload(optionActive);
    answerPost = () => respond(payload(inForce));
    await open('pl');

    expect(within(block()).getByRole('switch', { name: 'Sterowanie dozwolone' })).toBeTruthy();
    expect(fact('effective').textContent).toContain('Obserwacja');
    expect(block().querySelector('[data-condition="control_off"]')?.textContent).toBe('Przełącznik „Sterowanie dozwolone” jest wyłączony.');

    fireEvent.click(within(block()).getByRole('switch'));
    await advance();
    fireEvent.click(within(block()).getByRole('button', { name: 'Dopuść sterowanie' }));
    await advance();
    expect(fact('effective').textContent).toContain('Sterowanie aktywne');
  });
});
