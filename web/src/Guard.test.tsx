import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import sample from './api/guard.sample.json';
import { GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS } from './config';

const START = new Date('2026-10-24T10:00:02Z');

let fetchMock: Mock<typeof fetch>;

function respond(body: unknown, code = 200): Promise<Response> {
  return Promise.resolve({
    ok: code >= 200 && code < 300,
    status: code,
    json: () => Promise.resolve(body),
  } as Response);
}

function payload(change?: (p: any) => void): unknown {
  const copy = JSON.parse(JSON.stringify(sample));
  change?.(copy);
  return copy;
}

/** Serves api/guard (swappable per test); anything else would be a request the page must not make. */
function serve(guard: () => Promise<Response>) {
  fetchMock.mockImplementation((input) => (input === 'api/guard' ? guard() : respond({}, 404)));
}

function guardRequests(): number {
  return fetchMock.mock.calls.filter(([input]) => input === 'api/guard').length;
}

async function advance(ms = 0): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

async function openGuard(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/guard';
  const view = render(<App lang={lang} />);
  await advance();
  return view;
}

function phaseBlock(phase: string): HTMLElement {
  const block = document.querySelector(`[data-phase="${phase}"]`);
  if (!(block instanceof HTMLElement)) throw new Error(`No block for phase ${phase}.`);
  return block;
}

/** The value next to a row label inside an element (a phase block, the overview). */
function fact(container: HTMLElement, label: string): HTMLElement {
  const value = within(container).getByText(label, { selector: 'dt' }).nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error(`No value next to "${label}".`);
  return value;
}

function deviceRow(id: string): HTMLTableRowElement {
  const row = document.querySelector(`tr[data-device="${id}"]`);
  if (!(row instanceof HTMLTableRowElement)) throw new Error(`No row for device ${id}.`);
  return row;
}

function decisionRows(): HTMLTableRowElement[] {
  return [...document.querySelectorAll<HTMLTableRowElement>('tr[data-reason]')];
}

function stateLabel(container: Element): { label: string; tone: string | null } {
  const label = container.querySelector('.state');
  return { label: label?.textContent ?? '', tone: label?.getAttribute('data-tone') ?? null };
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(START);
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

describe('limits page', () => {
  it('requests the relative URL api/guard and nothing else', async () => {
    serve(() => respond(payload()));
    await openGuard();

    // Relative, so the request stays under the ingress prefix; one request carries the whole page.
    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual(['api/guard']);
  });

  it('shows the effective mode, the breaker and when the guard last evaluated', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const overview = screen.getByRole('heading', { name: 'Limits and overloads' }).closest('section') as HTMLElement;
    expect(fact(overview, 'Mode').textContent).toBe('ShadowComputes and logs, never switches.');
    expect(fact(overview, 'Breaker').textContent).toBe('25 A, characteristic B · continuous limit 28.25 A (1.13 × In)');
    expect(fact(overview, 'Last evaluation').querySelector('time')?.getAttribute('dateTime')).toBe(
      '2026-10-24T10:00:00.004Z',
    );
  });

  // MODE-01 rule from DECISIONS: an active add-on option is only a ceiling; this version stays shadow.
  it('says why an active add-on option still runs in shadow mode', async () => {
    serve(() =>
      respond(payload((p) => (p.mode = { ceiling: 'active', uiChoice: 'active', effective: 'shadow', note: 'active_unavailable_before_stage3' }))),
    );
    await openGuard();

    const overview = screen.getByRole('heading', { name: 'Limits and overloads' }).closest('section') as HTMLElement;
    expect(fact(overview, 'Mode').textContent).toContain('Shadow');
    expect(fact(overview, 'Mode').textContent).toContain('it stays in shadow mode');
  });

  // BRK-01/02/04: per phase the current against its limits, the tier it is in, the overload budget
  // with its recovery time and the headroom, with a text label for every state.
  it('shows a shedding phase with its tier, budget, recovery time and headroom', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const l1 = phaseBlock('L1');
    expect(stateLabel(l1)).toEqual({ label: 'shedding', tone: 'crit' });
    expect(l1.querySelector('.pval')?.textContent).toBe('33 Aimport');
    expect(fact(l1, 'Tier').textContent).toBe(
      '1.25–1.45× (1.32×)shed one device immediately26.4 A as if the limits already decided were applied',
    );
    expect(fact(l1, 'Overload budget').textContent).toBe('62%being consumedfull again in 3 min');
    expect(fact(l1, 'Headroom').textContent).toBe(
      '-18.87 A (-4,340 W)to the reserve threshold-4.75 Ato the continuous limit',
    );
    expect(fact(l1, 'Adding load').textContent).toBe('not allowedreduction requested (reserve threshold)');
    expect(l1.querySelector('.legend')?.textContent).toBe(
      'reserve threshold 14.13 Abreaker 25 Acontinuous limit 28.25 A',
    );
    // The reading's age, measured on the add-on clock: the guard evaluated 3.994 s after the report.
    expect(fact(l1, 'Meter data').textContent).toBe('validage 3 s');
  });

  it('draws the current against the reserve threshold, the breaker and the continuous limit', async () => {
    serve(() => respond(payload()));
    await openGuard();

    // Scale: the top of the last finite band, 36.25 A.
    const meter = phaseBlock('L1').querySelector('.meter') as HTMLElement;
    expect((meter.querySelector('.fill') as HTMLElement).style.width).toBe(`${(33 / 36.25) * 100}%`);
    expect((meter.querySelector('.tick.reserve') as HTMLElement).style.left).toBe(`${(14.13 / 36.25) * 100}%`);
    expect((meter.querySelector('.tick.rating') as HTMLElement).style.left).toBe(`${(25 / 36.25) * 100}%`);
    expect((meter.querySelector('.tick.continuous') as HTMLElement).style.left).toBe(`${(28.25 / 36.25) * 100}%`);
    expect(meter.getAttribute('aria-hidden')).toBe('true');
  });

  it('shows a phase without valid data as safe state, with its reason and a held budget', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const l2 = phaseBlock('L2');
    expect(stateLabel(l2)).toEqual({ label: 'safe state', tone: 'crit' });
    expect(fact(l2, 'Meter data').textContent).toBe('invalidcurrent reading stale');
    expect(fact(l2, 'Overload budget').textContent).toBe('90%held: no valid datafull again in 60 s');
    expect(fact(l2, 'Headroom').textContent).toBe('—to the reserve threshold');
    // No meter drawn without a current: an empty bar would read as 0 A.
    expect(l2.querySelector('.meter')).toBeNull();
    expect(within(l2).queryByText('Tier', { selector: 'dt' })).toBeNull();
  });

  it('shows an exporting phase with its direction and a full budget', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const l3 = phaseBlock('L3');
    expect(stateLabel(l3)).toEqual({ label: 'normal', tone: 'ok' });
    expect(l3.querySelector('.pval')?.textContent).toBe('-12.4 Aexport');
    expect(fact(l3, 'Overload budget').textContent).toBe('100%full');
    expect(fact(l3, 'Headroom').textContent).toContain('26.53 A (6,102 W)');
  });

  // SAFE-01: the safe state is announced above everything else, with its cause, and in shadow mode
  // says that nothing is switched rather than claiming the devices are off.
  it('announces a safe state on some phases with its reason and what shadow mode would do', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const alert = screen.getAllByRole('alert')[0] as HTMLElement;
    expect(alert.textContent).toContain('Safe state on L2');
    expect(alert.textContent).toContain('A signal the limits depend on is stale or unavailable.');
    expect(alert.textContent).toContain(
      'In shadow mode nothing is switched: Switchboard would switch off every controlled device on these phases.',
    );
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T09:59:40.011Z');
  });

  it('announces a safe state everywhere before the guard has a configuration', async () => {
    serve(() =>
      respond(
        payload((p) => {
          Object.assign(p, { breaker: null, phases: [], devices: [], decisions: [] });
          p.safeState = { active: true, scope: 'all', reason: 'safe_state', phases: [], since: p.at };
        }),
      ),
    );
    await openGuard();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Safe state on every phase');
    expect(alert.textContent).toContain('Switchboard would switch off every controlled device.');
    expect(screen.getByText(/No valid configuration/)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Phases' })).toBeNull();
    expect(screen.queryByRole('heading', { name: /Breaker profile/ })).toBeNull();
  });

  it('shows no safe-state banner when there is none', async () => {
    serve(() => respond(payload((p) => (p.safeState = { active: false, scope: 'none', reason: null, phases: [], since: null }))));
    await openGuard();

    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText(/Safe state on/)).toBeNull();
  });

  // SAFE-03: the kill switch state is visible on the page, with where it came from.
  it('shows an engaged kill switch, where it was engaged and that shadow mode switches nothing', async () => {
    serve(() =>
      respond(payload((p) => (p.killSwitch = { engaged: true, changedAt: '2026-10-24T09:58:00.000+00:00', source: 'mqtt' }))),
    );
    await openGuard();

    const alert = screen.getAllByRole('alert')[0] as HTMLElement;
    expect(stateLabel(alert)).toEqual({ label: 'Kill switch engaged', tone: 'crit' });
    expect(alert.textContent).toContain('from Home Assistant, since');
    expect(alert.textContent).toContain('Every controlled device is held off. In shadow mode nothing is switched');
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T09:58:00.000Z');
  });

  it('shows no kill switch banner while it is released', async () => {
    serve(() => respond(payload()));
    await openGuard();

    // The decisions table may still name an earlier engagement; no banner claims one now.
    const banners = [...document.querySelectorAll('.banner')].map((b) => b.textContent).join(' ');
    expect(banners).not.toContain('Kill switch');
  });

  it('warns when the decision log cannot be written, and how many decisions were lost', async () => {
    serve(() => respond(payload((p) => (p.decisionLog = { writable: false, lastError: 'No space left on device', dropped: 7 }))));
    await openGuard();

    const status = screen.getByText(/The decision log cannot be written/).closest('.banner') as HTMLElement;
    expect(status.textContent).toContain('No space left on device');
    expect(status.textContent).toContain('Decisions lost while the log could not be written: 7');
  });

  it('warns when state.json was corrupt and the run started fresh', async () => {
    serve(() => respond(payload((p) => (p.stateFile = { status: 'reset_corrupt', warning: 'schema_version is 9.' }))));
    await openGuard();

    const status = screen.getByText(/state.json could not be read/).closest('.banner') as HTMLElement;
    expect(status.textContent).toContain('schema_version is 9.');
  });

  // DISP-02 / 04 §2: the devices in shed order, each with its limit, reason and why it may differ.
  it('lists devices in shed order with their limit, its reason and a text label', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const ids = [...document.querySelectorAll('tr[data-device]')].map((r) => r.getAttribute('data-device'));
    expect(ids).toEqual(['boiler', 'heater_l1', 'heater_l2', 'ev_charger', 'heat_pump']);
    expect(stateLabel(deviceRow('heater_l1').cells[2] as Element)).toEqual({ label: 'held off', tone: 'crit' });
    expect(deviceRow('heater_l1').cells[2]?.textContent).toContain('Phase current above a tier of the breaker profile.');
    expect(stateLabel(deviceRow('ev_charger').cells[2] as Element)).toEqual({ label: 'held at minimum', tone: 'warn' });
    expect(stateLabel(deviceRow('heat_pump').cells[2] as Element)).toEqual({ label: 'not limited', tone: 'ok' });
    expect(deviceRow('heat_pump').cells[4]?.textContent).toContain('never shed (no_shed)');
    expect(deviceRow('boiler').cells[4]?.textContent).toBe('1value 0.35 per kWhshed priority 0.1, instead of the value');
  });

  // A modulating device has no stop path before Stage 3; the page never claims it would be off.
  it('says that a modulating device cannot be switched off yet', async () => {
    serve(() => respond(payload()));
    await openGuard();

    expect(deviceRow('ev_charger').cells[2]?.textContent).toContain('cannot be switched off yet');
    expect(deviceRow('heater_l1').cells[2]?.textContent).not.toContain('cannot be switched off');
  });

  // SIG-05: a device with a stale signal is unsafe, counted as on, and the page names the signal.
  it('shows an unsafe device, the signals that make it unsafe and its last known state', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const boiler = deviceRow('boiler');
    expect(stateLabel(boiler.cells[2] as Element)).toEqual({ label: 'unsafe: state unknown', tone: 'crit' });
    expect(boiler.cells[3]?.textContent).toContain('stage 2: unknown, counted as on');
    expect(boiler.cells[3]?.textContent).toContain('state report stale');
    expect(boiler.cells[3]?.textContent).toContain('last known: on');
    expect(boiler.cells[3]?.textContent).toContain('stage 1: off');
    for (const signal of ['device_boiler_stage2', 'grid_current_l2']) {
      expect(within(boiler.cells[6] as HTMLElement).getByText(signal).classList.contains('mono')).toBe(true);
    }
    expect(deviceRow('heater_l1').cells[6]?.textContent).toBe('—');
  });

  it('shows the reported state and what protection holds each stage to', async () => {
    serve(() => respond(payload()));
    await openGuard();

    expect(deviceRow('heater_l1').cells[3]?.textContent).toBe('onheld off');
    expect(deviceRow('ev_charger').cells[3]?.textContent).toBe('16 Aheld at minimum');
    expect(deviceRow('heat_pump').cells[3]?.textContent).toBe('on');
  });

  // SAFE-07: lockouts restart at process start; their remaining time is shown, never hidden.
  it('shows the lockout time left', async () => {
    serve(() => respond(payload()));
    await openGuard();

    expect(deviceRow('heater_l1').cells[5]?.textContent).toBe('minimum on time, 42 s left');
    expect(deviceRow('boiler').cells[5]?.textContent).toBe('minimum off time, 75 s left');
    expect(deviceRow('heat_pump').cells[5]?.textContent).toBe('—');
  });

  // MODE-03 on screen: the recent decisions, newest first, with the reason as a sentence.
  it('lists recent decisions with time, subject, change and reason sentence', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const rows = decisionRows();
    expect(rows).toHaveLength(sample.decisions.length);
    const [shed, chargerOff, chargerMin, safe, exporting, release] = rows;
    expect(shed?.cells[0]?.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T10:00:00.004Z');
    expect(shed?.cells[1]?.textContent).toBe('heater_l1L1');
    expect(shed?.cells[2]?.textContent).toBe('Switch offNot commanded: Shadow mode: computed and logged, nothing switched.');
    expect(shed?.cells[3]?.textContent).toContain('Phase current above a tier of the breaker profile.');
    expect(shed?.cells[3]?.textContent).toContain('Takes precedence over: Minimum on time not yet elapsed (lockout).');
    expect(shed?.cells[3]?.textContent).toContain('L1 33 A · 1.32 × In · budget 62%');
    expect(chargerOff?.cells[3]?.textContent).toContain('cannot be switched off yet');
    expect(chargerMin?.cells[2]?.textContent).toContain('Reduce to minimum');
    expect(safe?.cells[1]?.textContent).toBe('L2');
    expect(safe?.cells[2]?.textContent).toBe('Safe state entered');
    expect(exporting?.cells[2]?.textContent).toBe('Nothing to reduce: the phase is exporting');
    expect(release?.cells[1]?.textContent).toBe('boilerstage 2L3');
    expect(rows.at(-1)?.cells[1]?.textContent).toBe('whole installation');
  });

  it('dates a decision from another day than the evaluation', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const rows = decisionRows();
    expect(rows[0]?.cells[0]?.querySelector('.sub')).toBeNull();
    expect(rows.at(-1)?.cells[0]?.querySelector('.sub')?.textContent).toBe(
      new Intl.DateTimeFormat('en', { day: '2-digit', month: '2-digit' }).format(new Date('2026-10-22T12:00:00Z')),
    );
  });

  // BRK-01: the tier table for this breaker, in amperes, with the phases currently in each band.
  it('shows the tier table for this breaker with the phases in each band', async () => {
    serve(() => respond(payload()));
    await openGuard();

    const table = screen.getByRole('heading', { name: 'Breaker profile B25' }).closest('section') as HTMLElement;
    const rows = [...table.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map((c) => c.textContent));
    expect(rows).toEqual([
      ['up to 0.9×', '22.5 A', 'unlimited', 'may add load', 'L3'],
      ['0.9–1×', '25 A', 'unlimited', 'hold, add nothing', '—'],
      ['1–1.13×', '28.25 A', '15 min', 'reduce during the ordinary cycle', '—'],
      ['1.13–1.25×', '31.25 A', '3 min', 'reduce next cycle', '—'],
      ['1.25–1.45×', '36.25 A', '30 s', 'shed one device immediately', 'L1'],
      ['above 1.45×', '—', '0 s', 'shed everything controlled on that phase', '—'],
    ]);
    expect(table.textContent).toContain('Switchboard treats 1.13 × In as the limit for continuous operation');
  });

  // A frozen page must not look like a calm installation: on failure every value is withdrawn.
  it('hides every value after a failed fetch and shows the last successful update', async () => {
    let fail = false;
    serve(() => (fail ? Promise.reject(new TypeError('Failed to fetch')) : respond(payload())));
    await openGuard();
    expect(screen.getByText('heater_l1', { selector: 'tr[data-device] span' })).toBeTruthy();

    fail = true;
    await advance(GUARD_REFRESH_INTERVAL_MS);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Limits unavailable');
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe(START.toISOString());
    expect(document.querySelector('[data-phase]')).toBeNull();
    expect(screen.queryByText('shedding')).toBeNull();
    expect(screen.queryByText(/62%/)).toBeNull();
  });

  it('treats a payload it does not understand as unavailable instead of guessing', async () => {
    serve(() => respond(payload((p) => (p.decisions[0].reason = 'made_up_code'))));
    await openGuard();

    expect(screen.getByRole('alert').textContent).toContain('Limits unavailable');
    expect(screen.queryByText('heater_l1')).toBeNull();
  });

  it('refreshes on its interval and gives up on a request after its timeout', async () => {
    serve(() => respond(payload()));
    await openGuard();
    expect(guardRequests()).toBe(1);

    await advance(GUARD_REFRESH_INTERVAL_MS - 1);
    expect(guardRequests()).toBe(1);
    await advance(1);
    expect(guardRequests()).toBe(2);

    serve(
      () =>
        new Promise<Response>((_resolve, reject) => {
          const signal = fetchMock.mock.calls.at(-1)?.[1]?.signal;
          signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
        }),
    );
    await advance(GUARD_REFRESH_INTERVAL_MS);
    expect(screen.queryByText('Limits unavailable')).toBeNull();
    await advance(GUARD_REQUEST_TIMEOUT_MS);
    expect(screen.getByRole('alert').textContent).toContain('Limits unavailable');
  });

  // SAFE-06 / product constraint: the statement stays on this page too.
  it('keeps the not-a-protective-device statement on the page', async () => {
    serve(() => respond(payload()));
    await openGuard();

    expect(screen.getByRole('note').textContent).toContain('Not a protective device.');
  });
});

describe('limits page in Polish', () => {
  it('renders headings, states, sentences and numbers in Polish', async () => {
    serve(() =>
      respond(payload((p) => (p.killSwitch = { engaged: true, changedAt: '2026-10-24T09:58:00.000+00:00', source: 'api' }))),
    );
    await openGuard('pl');

    expect(screen.getByRole('heading', { name: 'Limity i przeciążenia' })).toBeTruthy();
    expect(screen.getByRole('heading', { name: 'Profil bezpiecznika B25' })).toBeTruthy();
    expect(screen.getByText('Wstrzymanie sterowania włączone')).toBeTruthy();
    expect(screen.getByText(/Stan bezpieczny na L2/)).toBeTruthy();
    const l1 = phaseBlock('L1');
    expect(stateLabel(l1).label).toBe('zrzut');
    expect(fact(l1, 'Próg').textContent).toContain('1,25–1,45×');
    expect(fact(l1, 'Próg').textContent).toContain('natychmiastowy zrzut jednego odbiornika');
    expect(fact(l1, 'Budżet przeciążenia').textContent).toBe('62%zużywanypełna odbudowa za 3 min');
    expect(fact(l1, 'Wolne').textContent).toContain('do progu zapasu');
    expect(deviceRow('ev_charger').cells[2]?.textContent).toContain('nie da się go jeszcze wyłączyć');
    expect(decisionRows()[0]?.cells[3]?.textContent).toContain('Prąd fazy powyżej progu z profilu bezpiecznika.');
    expect(decisionRows()[0]?.cells[2]?.textContent).toContain('Bez komendy: Tryb Obserwacja');
    expect(within(screen.getByRole('navigation')).getByText('Limity').getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('note').textContent).toContain('To nie jest urządzenie zabezpieczające.');
  });
});

describe('navigation to the limits page', () => {
  it('opens from the navigation, and only the page on screen polls', async () => {
    fetchMock.mockImplementation((input) =>
      input === 'api/guard'
        ? respond(payload())
        : respond({ version: '0.2.0', mode: 'shadow', homeAssistant: 'connected', mqtt: 'connected', healthy: true }),
    );
    render(<App lang="en" />);
    await advance();
    const nav = screen.getByRole('navigation');

    await act(async () => {
      // jsdom navigates to the fragment on its own task, as a browser does; wait for that.
      const navigated = new Promise((resolve) => window.addEventListener('hashchange', resolve, { once: true }));
      fireEvent.click(within(nav).getByText('Limits'));
      await navigated;
    });
    await advance();

    expect(within(nav).getByText('Limits').getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('heading', { name: 'Limits and overloads' })).toBeTruthy();
    const statusBefore = fetchMock.mock.calls.filter(([input]) => input === 'api/status').length;
    await advance(GUARD_REFRESH_INTERVAL_MS * 2);
    expect(fetchMock.mock.calls.filter(([input]) => input === 'api/status').length).toBe(statusBefore);
    expect(guardRequests()).toBe(3);
  });
});
