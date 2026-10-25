import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import type { Status } from './api/status';
import sample from './api/signals.sample.json';
import { SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS, STATUS_REFRESH_INTERVAL_MS } from './config';

const START = new Date('2026-10-24T10:00:00Z');

const status: Status = {
  version: '0.1.0',
  mode: 'shadow',
  homeAssistant: 'connected',
  mqtt: 'connected',
  healthy: true,
};

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

/** Serves the status and the signals endpoints; the signals answer can be swapped per test. */
function serve(signals: () => Promise<Response>) {
  fetchMock.mockImplementation((input) => (input === 'api/signals' ? signals() : respond(status)));
}

function signalsRequests(): number {
  return fetchMock.mock.calls.filter(([input]) => input === 'api/signals').length;
}

function statusRequests(): number {
  return fetchMock.mock.calls.filter(([input]) => input === 'api/status').length;
}

async function advance(ms = 0): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

async function openSignals(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/signals';
  const view = render(<App lang={lang} />);
  await advance();
  return view;
}

/** The table row that shows the given text (an entity ID, a role label). */
function rowWith(text: string): HTMLTableRowElement {
  const row = screen.getByText(text).closest('tr');
  if (!(row instanceof HTMLTableRowElement)) throw new Error(`No table row shows "${text}".`);
  return row;
}

function phaseRow(phase: string): HTMLElement {
  const row = document.querySelector(`tr[data-phase="${phase}"]`);
  if (!(row instanceof HTMLElement)) throw new Error(`No row for phase ${phase}.`);
  return row;
}

/** Text and tone of the state label in a row's state cell (the sixth column of the signals table). */
function stateOf(row: HTMLTableRowElement): { label: string; tone: string | null } {
  const label = row.cells[5]?.querySelector('.state');
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

describe('signals page', () => {
  it('requests the relative URL api/signals and nothing else', async () => {
    serve(() => respond(payload()));
    await openSignals();

    // Relative, so the request stays under the ingress prefix; the status page is not polled (the
    // shell's mode banner polls api/mode on every page: Shell.test).
    expect(fetchMock.mock.calls.map(([input]) => input).filter((input) => input !== 'api/mode')).toEqual(['api/signals']);
  });

  // Every state must be readable without colour: a text label, with the tone as a second channel.
  it('renders stale, rejected, unavailable and unmapped with text labels and tones', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(stateOf(rowWith('sensor.meter_current_a'))).toEqual({ label: 'fresh', tone: 'ok' });
    expect(stateOf(rowWith('sensor.meter_current_b'))).toEqual({ label: 'stale', tone: 'crit' });
    // A sample refused by the balance check leaves the held value in use: fresh, not rejected.
    expect(stateOf(rowWith('sensor.meter_power_c'))).toEqual({ label: 'fresh', tone: 'ok' });
    // An unusable value takes the signal out of use, as bad as no data.
    expect(stateOf(rowWith('sensor.meter_voltage_bn'))).toEqual({ label: 'rejected', tone: 'crit' });
    expect(stateOf(rowWith('sensor.meter_voltage_an'))).toEqual({ label: 'unavailable', tone: 'crit' });
    expect(stateOf(rowWith('phase power · L2'))).toEqual({ label: 'unmapped', tone: 'muted' });
  });

  it('says why a signal is in its state', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(rowWith('sensor.meter_current_b').textContent).toContain('no report within max_age');
    expect(rowWith('sensor.meter_power_c').textContent).toContain('latest sample failed the balance check');
    expect(rowWith('sensor.meter_voltage_bn').textContent).toContain('unit differs from the declared one');
    expect(rowWith('sensor.meter_power_c').textContent).toContain('rejected: 2');
    expect(rowWith('sensor.meter_voltage_an').textContent).toContain('Home Assistant reports unavailable');
  });

  it('renders the same states in Polish', async () => {
    serve(() => respond(payload()));
    await openSignals('pl');

    expect(stateOf(rowWith('sensor.meter_current_a')).label).toBe('świeży');
    expect(stateOf(rowWith('sensor.meter_current_b')).label).toBe('przeterminowany');
    expect(stateOf(rowWith('sensor.meter_voltage_bn')).label).toBe('odrzucony');
    expect(rowWith('sensor.meter_power_c').textContent).toContain('ostatnia próbka nie przeszła kontroli bilansu');
    expect(phaseRow('L2').textContent).toContain('wiek 5 min');
    expect(stateOf(rowWith('sensor.meter_voltage_an')).label).toBe('niedostępny');
    expect(stateOf(rowWith('moc na fazie · L2')).label).toBe('nieprzypisany');
    expect(screen.getByRole('heading', { name: 'Stan sygnałów' })).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'Encja' })).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'Wiek' })).toBeTruthy();
    expect(screen.getByRole('note').textContent).toContain('To nie jest urządzenie zabezpieczające.');
  });

  it('shows entity ids and logical names in monospace', async () => {
    serve(() => respond(payload()));
    await openSignals();

    for (const entity of ['sensor.meter_power_a', 'sensor.meter_current_a', 'binary_sensor.charger_car']) {
      expect(screen.getByText(entity).classList.contains('mono')).toBe(true);
    }
    expect(screen.getByText('grid_current_l1').classList.contains('mono')).toBe(true);
  });

  it('shows normalized value, raw value, age, max_age and refresh per signal', async () => {
    serve(() => respond(payload()));
    await openSignals();

    // The meter reports 1085.5 W of export as positive; the page shows it import-positive.
    const power = rowWith('sensor.meter_power_a');
    expect(power.cells[2]?.textContent).toBe('-1,086 W');
    expect(power.cells[3]?.textContent).toBe('1085.5 W');
    expect(power.cells[4]?.textContent).toBe('13 smax 90 s');
    expect(power.cells[6]?.textContent).toBe('30 sn = 12');
    // 200 s without a report reads as minutes, not as a small number of seconds.
    expect(rowWith('sensor.meter_current_b').cells[4]?.textContent).toContain('3 min');
    expect(rowWith('sensor.meter_current_b').cells[6]?.textContent).toContain('not detected yet');
    expect(rowWith('binary_sensor.charger_car').cells[2]?.textContent).toBe('on');
    expect(rowWith('binary_sensor.charger_car').cells[6]?.textContent).toContain('2.5 s');
    // An unmapped slot has nothing to show, and says so rather than showing zero.
    expect(rowWith('phase power · L2').cells[2]?.textContent).toBe('—');
  });

  it('labels a direction that is not known as cautious mode, and names where a known one came from', async () => {
    serve(() => respond(payload()));
    await openSignals();

    const pending = rowWith('sensor.meter_current_c').cells[7];
    expect(pending?.querySelector('.state')?.getAttribute('data-tone')).toBe('warn');
    expect(pending?.textContent).toContain('unknown — cautious mode');
    expect(pending?.textContent).toContain('waiting for the power of the same reading, counted as import');
    const known = rowWith('sensor.meter_current_a').cells[7];
    expect(known?.textContent).toBe('exportfrom grid_power_l1');
  });

  it('shows per phase whether meter data is valid, and why not', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(within(phaseRow('L1')).getByText('valid').closest('.state')?.getAttribute('data-tone')).toBe('ok');
    const l2 = phaseRow('L2');
    expect(within(l2).getByText('invalid').closest('.state')?.getAttribute('data-tone')).toBe('crit');
    expect(l2.textContent).toContain('current reading stale');
    expect(phaseRow('L1').textContent).toContain('-5.47 A');
    expect(phaseRow('L1').textContent).toContain('export');
  });

  // 05-UI-SPEC: every number that can be stale shows its age. A residual from minutes ago next to
  // dashes for grid and load would otherwise look like a live check.
  it('shows the age of each phase balance residual, measured on the add-on clock', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(phaseRow('L1').children[6]?.textContent).toBe('9 Wage 13 srejected: 0');
    expect(phaseRow('L2').children[6]?.textContent).toContain('age 5 min');
    expect(phaseRow('L3').children[6]?.textContent).toBe('—rejected: 2');
  });

  it('flags a reserve below its derived minimum on that phase only', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(phaseRow('L3').textContent).toContain('below the derived minimum');
    expect(phaseRow('L3').textContent).toContain('min. 632 W');
    expect(phaseRow('L1').textContent).not.toContain('below the derived minimum');
    expect(phaseRow('L2').textContent).toContain('max_age 90 s, refresh not detected');
    expect(phaseRow('L1').textContent).toContain('refresh 30 s, detected');
  });

  // SIG-05 on screen: which devices a signal protects, and that they are held as unsafe while it is not fresh.
  it('lists the devices that depend on a signal and marks them unsafe while it is not fresh', async () => {
    serve(() => respond(payload()));
    await openSignals();

    const fresh = rowWith('sensor.meter_current_a').cells[8];
    expect(within(fresh as HTMLElement).getByText('heater_l1').classList.contains('mono')).toBe(true);
    expect(fresh?.textContent).toContain('ev_charger');
    expect(fresh?.textContent).not.toContain('held as unsafe');
    const stale = rowWith('sensor.meter_current_b').cells[8];
    expect(stale?.textContent).toContain('heater_l2');
    expect(stale?.querySelector('.state')?.getAttribute('data-tone')).toBe('crit');
    expect(stale?.textContent).toContain('held as unsafe');
    expect(rowWith('sensor.meter_power_a').cells[8]?.textContent).toBe('—');
    expect(screen.getByRole('columnheader', { name: 'Dependent devices' })).toBeTruthy();
  });

  // A stale device draw holds nothing off: the copy must not claim a protection state that does not exist.
  it('marks the device of a stale draw as not trusted, never as held unsafe', async () => {
    serve(() =>
      respond(
        payload((p) => {
          const row = p.signals.find((s: any) => s.entity === 'sensor.meter_current_b');
          row.role = 'device_current';
          row.dependentDevices = ['ev_charger'];
        }),
      ),
    );
    await openSignals();

    const cell = rowWith('sensor.meter_current_b').cells[8];
    expect(cell?.textContent).toContain('ev_charger');
    expect(cell?.textContent).toContain('not trusted: no relief, no increases');
    expect(cell?.textContent).not.toContain('held as unsafe');
    expect(cell?.querySelector('.state')?.getAttribute('data-tone')).toBe('warn');
  });

  it('labels the applied state of a declared device as its own role', async () => {
    serve(() => respond(payload()));
    await openSignals();

    const row = rowWith('switch.heater_2');
    expect(row.cells[0]?.textContent).toBe('device_heater_l2device state');
    expect(stateOf(row)).toEqual({ label: 'stale', tone: 'crit' });
  });

  it('counts rejected samples', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(screen.getByText(/Rejected samples/).textContent).toBe(
      'Rejected samples: 3 (balance check 2, invalid value 1)',
    );
  });

  it('warns that replayed values are not live', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(screen.getByText('Replay of recorded data: these are not live values.')).toBeTruthy();
  });

  it('says that no meter data is valid while Home Assistant is disconnected', async () => {
    serve(() => respond(payload((p) => (p.source = { kind: 'home_assistant', status: 'disconnected' }))));
    await openSignals();

    expect(screen.getByRole('alert').textContent).toContain(
      'Not connected to Home Assistant: no meter data counts as valid.',
    );
    expect(screen.queryByText(/Replay of recorded data/)).toBeNull();
  });

  it('shows configuration errors with their translated meaning, path, line and the log message', async () => {
    serve(() =>
      respond(
        payload((p) => {
          p.configuration.state = 'invalid';
          p.configuration.errors = [
            {
              code: 'unknown_key',
              path: 'signals.grid_current_l1.max_agee',
              line: 12,
              column: 5,
              message: "Unknown key 'max_agee'.",
            },
          ];
          p.connection = null;
          p.signals = [];
          for (const phase of p.phases) {
            Object.assign(phase, { protectionDataValid: false, invalidReason: 'configuration_invalid', currentA: null });
          }
        }),
      ),
    );
    await openSignals();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('No meter data counts as valid until they are fixed');
    expect(alert.textContent).toContain('Unknown key; check the spelling.');
    expect(within(alert).getByText('signals.grid_current_l1.max_agee').classList.contains('mono')).toBe(true);
    expect(alert.textContent).toContain('line 12, column 5');
    expect(alert.textContent).toContain("Unknown key 'max_agee'.");
    expect(phaseRow('L1').textContent).toContain('configuration invalid');
    expect(screen.getByText('No signals: the configuration declares none or is not loaded.')).toBeTruthy();
  });

  it('says where it looks when no configuration is loaded', async () => {
    serve(() =>
      respond(
        payload((p) => {
          Object.assign(p.configuration, { state: 'missing', loadedAt: null, errors: [], warnings: [] });
          p.signals = [];
        }),
      ),
    );
    await openSignals();

    expect(screen.getByRole('alert').textContent).toContain('No configuration is loaded.');
    expect(screen.getByText('/config/switchboard.yaml').classList.contains('mono')).toBe(true);
  });

  it('lists warnings of a valid configuration', async () => {
    serve(() => respond(payload()));
    await openSignals();

    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText(/max_age is short for the detected refresh/)).toBeTruthy();
    expect(screen.getByText('signals.charger_current_l1.max_age')).toBeTruthy();
  });

  // A frozen page must not look like a calm installation: on failure every value is withdrawn.
  it('hides every value after a failed fetch and shows the last successful update', async () => {
    let fail = false;
    serve(() => (fail ? Promise.reject(new TypeError('Failed to fetch')) : respond(payload())));
    await openSignals();
    expect(screen.getByText('sensor.meter_current_a')).toBeTruthy();

    fail = true;
    await advance(SIGNALS_REFRESH_INTERVAL_MS);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Signals unavailable');
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe(START.toISOString());
    expect(screen.queryByText('sensor.meter_current_a')).toBeNull();
    expect(screen.queryByText('fresh')).toBeNull();
  });

  it('treats a payload it does not understand as unavailable instead of guessing', async () => {
    serve(() => respond(payload((p) => (p.signals[0].state = 'Fresh'))));
    await openSignals();

    expect(screen.getByRole('alert').textContent).toContain('Signals unavailable');
    expect(screen.queryByText('sensor.meter_power_a')).toBeNull();
  });

  it('refreshes on its interval and gives up on a request after its timeout', async () => {
    serve(() => respond(payload()));
    await openSignals();
    expect(signalsRequests()).toBe(1);

    await advance(SIGNALS_REFRESH_INTERVAL_MS - 1);
    expect(signalsRequests()).toBe(1);
    await advance(1);
    expect(signalsRequests()).toBe(2);

    serve(
      () =>
        new Promise<Response>((_resolve, reject) => {
          const signal = fetchMock.mock.calls.at(-1)?.[1]?.signal;
          signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
        }),
    );
    await advance(SIGNALS_REFRESH_INTERVAL_MS);
    expect(screen.queryByRole('alert')).toBeNull();
    await advance(SIGNALS_REQUEST_TIMEOUT_MS);
    expect(screen.getByRole('alert').textContent).toContain('Signals unavailable');
  });
});

describe('navigation', () => {
  it('navigates between status and signals, and only the page on screen polls', async () => {
    serve(() => respond(payload()));
    window.location.hash = '#/status';
    render(<App lang="en" />);
    await advance();
    const nav = screen.getByRole('navigation');
    expect(within(nav).getByText('Status').getAttribute('aria-current')).toBe('page');
    expect(statusRequests()).toBe(1);
    expect(signalsRequests()).toBe(0);

    await act(async () => {
      // jsdom navigates to the fragment on its own task, as a browser does; wait for that.
      const navigated = new Promise((resolve) => window.addEventListener('hashchange', resolve, { once: true }));
      fireEvent.click(within(nav).getByText('Signals'));
      await navigated;
    });
    await advance();
    expect(within(nav).getByText('Signals').getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('heading', { name: 'Signal status' })).toBeTruthy();
    expect(signalsRequests()).toBe(1);
    await advance(STATUS_REFRESH_INTERVAL_MS * 2);
    expect(statusRequests()).toBe(1);
    // The statement stays on every page.
    expect(screen.getByRole('note').textContent).toContain('Not a protective device.');

    await act(async () => {
      const navigated = new Promise((resolve) => window.addEventListener('hashchange', resolve, { once: true }));
      fireEvent.click(within(nav).getByText('Status'));
      await navigated;
    });
    await advance();
    expect(within(nav).getByText('Status').getAttribute('aria-current')).toBe('page');
    expect(screen.queryByRole('heading', { name: 'Signal status' })).toBeNull();
    expect(statusRequests()).toBe(2);
  });

  it('links point at hash routes, so they stay under the ingress prefix', () => {
    serve(() => respond(payload()));
    render(<App lang="en" />);

    const links = within(screen.getByRole('navigation')).getAllByRole('link');
    // The mockup's screens (the day plan is not a link: not in this version), then diagnostics.
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['#/', '#/flows', '#/setup', '#/devices', '#/guard', '#/log', '#/signals', '#/status']);
  });
});
