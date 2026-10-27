import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import type { HistoryEntry } from './api/dispatch';
import devices from './api/devices.sample.json';
import dispatch from './api/dispatch.sample.json';
import economics from './api/economics.sample.json';
import plan from './api/plan.sample.json';
import power from './api/power.sample.json';
import { DISPATCH_REFRESH_INTERVAL_MS } from './config';
import { measurement } from './DeviceCard';
import { messages } from './i18n';
import { advance, respond, serveSamples } from './screens.test-support';

let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-24T12:01:21Z'));
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

/** A deep copy of a sample with one change applied. */
function changed<T>(sample: T, change: (copy: any) => void): T {
  const copy = structuredClone(sample);
  change(copy);
  return copy;
}

function serve(samples: Partial<Record<'api/devices' | 'api/dispatch' | 'api/power', unknown>> = {}) {
  serveSamples(fetchMock, { 'api/devices': devices, 'api/dispatch': dispatch, 'api/power': power, ...samples });
}

async function open(hash: string, lang: 'en' | 'pl' = 'en') {
  window.location.hash = hash;
  render(<App lang={lang} />);
  // Dispatch and the power picture are polled once the declared device is known: a second settle.
  await advance();
  await advance();
}

function card(heading: string): HTMLElement {
  return screen.getByRole('heading', { name: heading }).closest('section') as HTMLElement;
}

/** The value next to a field label inside a card. */
function field(container: HTMLElement, name: string): HTMLElement {
  const value = within(container).getByText(name, { selector: 'dt' }).nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error(`No value next to "${name}".`);
  return value;
}

function rows(): HTMLTableRowElement[] {
  return [...card('Command verification').querySelectorAll('tbody tr')] as HTMLTableRowElement[];
}

const cells = (row: HTMLTableRowElement) => [...row.cells].map((c) => c.textContent);

// Local wall-clock time, as the card renders it at the presentation edge (the CI machine's zone).
const clock = (iso: string) => new Intl.DateTimeFormat('en', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(new Date(iso));

const requested = () => fetchMock.mock.calls.map(([input]) => String(input));

// Screen 04 as the shell delivers it: #/devices/<id>, #/devices opening the first device, display
// names from the configuration.
describe('device card: routing', () => {
  it('#/devices opens the first declared device under its display name', async () => {
    serve();
    await open('#/devices', 'pl');

    expect(screen.getByRole('heading', { name: 'Grzałka CWU L1' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Ładowarka go-e' }).getAttribute('href')).toBe('#/devices/ev_charger');
  });

  it('#/devices/<id> opens that device', async () => {
    serve();
    await open('#/devices/ev_charger');

    expect(screen.getByRole('heading', { name: 'Ładowarka go-e' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Ładowarka go-e' }).getAttribute('aria-current')).toBe('page');
  });

  // Nothing to show means nothing to poll: dispatch and the power picture are requested only for a declared device.
  it('says so when no declared device has that id, and polls nothing else', async () => {
    serve();
    await open('#/devices/boiler');

    expect(screen.getByRole('alert').textContent).toBe('No declared device is called boiler.');
    // Besides the shell's mode banner (api/mode, on every page: Shell.test).
    expect(requested().filter((url) => url !== 'api/mode')).toEqual(['api/devices']);
  });

  it('says the configuration is not valid instead of claiming no device is declared', async () => {
    serve({ 'api/devices': { at: '2026-10-22T09:40:02+00:00', configuration: 'invalid', devices: [] } });
    await open('#/devices');

    expect(screen.getByText('The configuration is not valid, so no device is shown. The Signals page lists what is wrong.')).toBeTruthy();
    expect(screen.queryByText('No devices declared: there is no device card to show.')).toBeNull();
  });
});

// UI-12: connection — the actuator, phase, limits, reaction time, lockouts and how the state reports,
// exactly as switchboard.yaml declares them. Entity IDs always render in monospace (CLAUDE.md).
describe('device card: connection', () => {
  it('shows every declared entity of the charger in monospace, with its phase and current range', async () => {
    serve();
    await open('#/devices/ev_charger');
    const connection = card('Connection');

    const entities = [...connection.querySelectorAll('code.entity')].map((e) => e.textContent);
    expect(entities).toEqual(['number.goe_422752_amp', 'sensor.goe_422752_nrg_4', 'sensor.goe_422752_nrg_7']);
    for (const code of connection.querySelectorAll('code.entity')) expect(code.classList.contains('mono')).toBe(true);
    expect(field(connection, 'Phase').textContent).toBe('L1');
    expect(field(connection, 'Minimum current').textContent).toBe('6.0 A');
    expect(field(connection, 'Maximum current').textContent).toBe('20.0 Ain steps of 1.0 A, written to the entity in A');
    expect(field(connection, 'Reaction time').textContent).toContain('20 s');
    expect(field(connection, 'Lockouts').textContent).toBe('minimum on time 60 sminimum off time 60 s');
    // go-e reports only on change (DECISIONS 2026-10-25): no age limit, and the card says so.
    expect(field(connection, 'State reports').textContent).toBe('only on change: judged by availability, with no age limit');
    expect(field(connection, 'Draw signals').textContent).toBe('current L1: sensor.goe_422752_nrg_4power L1: sensor.goe_422752_nrg_7stale after 60 s without a report');
    // A modulating device without a stop actuator cannot be switched off: the card must not hide it.
    expect(field(connection, 'Stop actuator').textContent).toBe('none declared: it cannot be switched off, only held at its minimum');
  });

  it("shows a heater's rated draw, its rotation group, and how a relay without a draw signal is checked", async () => {
    serve();
    await open('#/devices/heater_l2');
    const connection = card('Connection');

    expect(field(connection, 'Actuator entity').querySelector('code.entity')?.textContent).toBe('switch.grzalka_1');
    // Shown in every mode: since 0.7.0 it must not claim nothing is ever commanded, only when.
    expect(field(connection, 'Actuator entity').textContent).toContain('it is commanded only while control is in force');
    expect(field(connection, 'Actuator entity').textContent).not.toContain('commands nothing');
    expect(field(connection, 'Rated draw').textContent).toBe('7.0 A · 1.70 kW');
    expect(field(connection, 'Lockouts').textContent).toBe('minimum on time 6 minminimum off time 90 s');
    expect(field(connection, 'Draw signals').textContent).toBe('none declared: a change is checked against the phase load and the rated power');
    expect(field(connection, 'Groups').querySelector('code.entity')?.textContent).toBe('cwu_heaters');
    // A relay has no stop actuator to declare.
    expect(within(connection).queryByText('Stop actuator')).toBeNull();
  });
});

// UI-12: economics — the value per kWh decides the order; the goal row reads api/plan (GoalRow.test.tsx) and says so when
// it cannot; allowed sources do not exist without an economics section and the card says so honestly instead of showing a made-up value.
describe('device card: energy value and goal', () => {
  it('shows the value per kWh, says the goal is not available while api/plan does not answer, and says sources are not available', async () => {
    serve();
    await open('#/devices/ev_charger');
    const economics = card('Energy value and goal');

    expect(field(economics, 'What 1 kWh is worth for this device').textContent).toContain('2.03 per kWh');
    expect(field(economics, 'Goal').textContent).toMatch(/^The goal is not available: the add-on does not answer\./);
    expect(field(economics, 'Allowed sources').textContent).toMatch(/^Not available in this version: .*grid power, at night too\.$/);
  });
});

// UI-12: current dispatch state — intended next to reported, why, what it waits for, the override.
describe('device card: dispatch state', () => {
  it('says plainly that Switchboard switches nothing, and when dispatch last evaluated', async () => {
    serve();
    await open('#/devices/heater_l2');
    const state = card('Dispatch state');

    expect(state.textContent).toContain('Control is not in force, so Switchboard switches nothing');
    expect(state.querySelector('.updated time')?.textContent).toBe(`Dispatch evaluated at ${clock('2026-10-24T12:01:20.004+00:00')}`);
  });

  it('shows intended next to reported, and an open settling window with the lockout left', async () => {
    serve();
    await open('#/devices/heater_l2');
    const state = card('Dispatch state');

    expect(field(state, 'Intended').textContent).toBe('ondiffers from the reported state: it would be changed');
    expect(field(state, 'Reported by Home Assistant').textContent).toBe('off');
    expect(field(state, 'Why').textContent).toBe('Headroom is available again.decided by dispatch');
    expect(field(state, 'Start order').textContent).toBe('3 in the start order');
    expect(field(state, 'Waiting').textContent).toBe(
      'Settling window open: waiting for the last change to show in the measurementsminimum on time, 5:48 left',
    );
  });

  // A device that does not respond must stand out: badge, since when, next try, mismatches; and a
  // state that is not fresh is never shown as a calm "off".
  it('marks an unresponsive device with a forced override, and never shows a stale state as off', async () => {
    serve();
    await open('#/devices/heater_l3');
    const state = card('Dispatch state');

    const badges = [...state.querySelectorAll('.cardhead .badge')].map((b) => b.textContent);
    expect(badges).toEqual(['Not responding', 'Manual override: Force on']);
    expect(field(state, 'Reported by Home Assistant').textContent).toBe('unknown: the state is not fresh, so it counts as on and relieves nothing');
    const waiting = field(state, 'Waiting').textContent;
    expect(waiting).toContain(`Not responding since ${clock('2026-10-24T11:40:00.004+00:00')}`);
    expect(waiting).toContain(`next try at ${clock('2026-10-24T12:10:00.004+00:00')}`);
    expect(waiting).toContain('3 mismatches in a row');
    expect(waiting).toContain('The last settling window timed out');
    expect(waiting).toContain('Increase waiting: A signal the limits depend on is stale or unavailable.');
    expect(field(state, 'Manual override').textContent).toContain('restored after a restart');
  });

  it('shows a deferred change with its countdown on the add-on clock and the reason it is wanted', async () => {
    serve();
    await open('#/devices/heater_l1');
    const waiting = field(card('Dispatch state'), 'Waiting').textContent;

    // dueAt 12:02:02.004 − evaluation 12:01:20.004 = 42 s.
    expect(waiting).toContain('Change to on deferred: Minimum off time not yet elapsed (lockout). (due in 0:42)');
    expect(waiting).toContain('Wanted because: Headroom is available again.');
    expect(waiting).toContain('minimum off time, 0:42 left');
  });

  // Every number that can be stale shows its age: a measured draw with its age on the add-on clock.
  it("shows the charger's measured power with its age and its own current", async () => {
    serve();
    await open('#/devices/ev_charger');
    const now = field(card('Dispatch state'), 'Power now');

    // power.at 09:21:02 − reported 09:20:57.502 = 4.5 s, shown truncated.
    expect(now.textContent).toBe('3.66 kW · measured 4 s agodraws 9.6 A');
    expect(now.querySelector('time.age')?.getAttribute('dateTime')).toBe('2026-10-22T09:20:57.502Z');
  });

  // An idle go-e stops reporting (DECISIONS 2026-10-25): its draw is not trusted, and the card says
  // "unknown", never 0 W.
  it('says a stale draw is unknown and relieves nothing, never 0 W', async () => {
    serve({
      'api/dispatch': changed(dispatch, (p) => (p.devices[3].draw = { currentA: null, powerW: null, fresh: false })),
      'api/power': changed(power, (p) => {
        p.devices[3].totalW = null;
        p.devices[3].source = 'unknown';
        p.devices[3].phases[0] = { phase: 'L1', powerW: null, source: 'unknown', reportedAt: null };
      }),
    });
    await open('#/devices/ev_charger');
    const now = field(card('Dispatch state'), 'Power now');

    expect(now.textContent).toBe('unknown: its draw is not freshdraw not fresh: it relieves nothing and gets no increase');
    expect(now.textContent).not.toContain('0.00 kW');
  });

  it('says dispatch has no state for a declared device it does not list yet', async () => {
    serve({ 'api/dispatch': changed(dispatch, (p) => (p.devices = p.devices.filter((d: { id: string }) => d.id !== 'heater_l1'))) });
    await open('#/devices/heater_l1');

    expect(card('Dispatch state').textContent).toContain('Dispatch has no state for this device yet.');
    // Without a state there is no history either: "nothing happened" would be a claim, not a fact.
    expect(rows()).toHaveLength(0);
    expect(card('Command verification').textContent).not.toContain('No change and no switching seen');
  });

  // A failed poll withdraws every dispatch value: a frozen intention next to a failure would read as calm.
  it('withdraws the state and the history when dispatch stops answering, and keeps the configuration', async () => {
    serve();
    await open('#/devices/heater_l3');
    expect(rows()).toHaveLength(2);

    fetchMock.mockImplementation((input) => {
      const url = String(input);
      if (url === 'api/dispatch') return respond({}, 500);
      return respond(structuredClone(url === 'api/devices' ? devices : power));
    });
    await advance(DISPATCH_REFRESH_INTERVAL_MS);

    expect(within(card('Dispatch state')).getByRole('alert').textContent).toContain('Unavailable — last update');
    expect(within(card('Dispatch state')).queryByText('Intended', { selector: 'dt' })).toBeNull();
    expect(rows()).toHaveLength(0);
    expect(card('Command verification').textContent).toContain('Unavailable — last update');
    expect(card('Connection').querySelector('code.entity')?.textContent).toBe('switch.grzalka_3');
  });
});

// SAFE-04 from the device card: a JSON POST, answered with dispatch's status after the add-on
// evaluated the change, which replaces the view at once. It outranks dispatch, never protection.
describe('device card: manual override', () => {
  it('posts the chosen mode and shows the evaluated answer at once', async () => {
    fetchMock.mockImplementation((input, init) => {
      const url = String(input);
      if (url === 'api/devices/ev_charger/override' && init?.method === 'POST') {
        return respond(changed(dispatch, (p) => (p.devices[3].override = { mode: 'auto', changedAt: null, source: null })));
      }
      if (url === 'api/devices') return respond(structuredClone(devices));
      if (url === 'api/dispatch') return respond(structuredClone(dispatch));
      if (url === 'api/power') return respond(structuredClone(power));
      return respond({}, 404);
    });
    await open('#/devices/ev_charger');
    const select = screen.getByRole('combobox', { name: 'Manual override of Ładowarka go-e' }) as HTMLSelectElement;
    expect(select.value).toBe('force_off');
    expect(field(card('Dispatch state'), 'Manual override').textContent).toContain('outranks dispatch, never protection');

    await act(async () => {
      fireEvent.change(select, { target: { value: 'auto' } });
    });
    await advance();

    const post = fetchMock.mock.calls.find(([input]) => input === 'api/devices/ev_charger/override');
    expect(post?.[1]?.body).toBe('{"mode":"auto"}');
    expect((post?.[1]?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect((screen.getByRole('combobox', { name: 'Manual override of Ładowarka go-e' }) as HTMLSelectElement).value).toBe('auto');
    expect(card('Dispatch state').querySelector('.cardhead .badge')).toBeNull();
  });

  it('says why an override was refused and keeps the mode the add-on reports', async () => {
    fetchMock.mockImplementation((input, init) => {
      const url = String(input);
      if (init?.method === 'POST') return respond({}, 409);
      if (url === 'api/devices') return respond(structuredClone(devices));
      if (url === 'api/dispatch') return respond(structuredClone(dispatch));
      return respond(structuredClone(power));
    });
    await open('#/devices/heater_l2');
    const select = screen.getByRole('combobox', { name: 'Manual override of Grzałka CWU L2' }) as HTMLSelectElement;

    await act(async () => {
      fireEvent.change(select, { target: { value: 'force_on' } });
    });
    await advance();

    expect(within(card('Dispatch state')).getByRole('status').textContent).toBe('No valid configuration; fix switchboard.yaml first.');
    expect((screen.getByRole('combobox', { name: 'Manual override of Grzałka CWU L2' }) as HTMLSelectElement).value).toBe('auto');
  });
});

// UI-12, DISP-07: the command verification table — time, command, expected, measured, verdict. In
// shadow mode Switchboard's own changes are "not applied"; switching by someone else is measured
// and labelled as such.
describe('device card: command verification', () => {
  it("reads a shadow command as not applied, with nothing claimed as measured", async () => {
    serve();
    await open('#/devices/ev_charger');

    expect(rows().map(cells)).toEqual([
      [
        `${clock('2026-10-24T12:00:15.004+00:00')}checked ${clock('2026-10-24T12:00:45.004+00:00')}`,
        '16 A → 6 Adecided by Switchboard',
        '—',
        '—',
        'not applied (shadow mode)',
      ],
    ]);
    expect(card('Command verification').textContent).toContain('entries since the add-on last started: 1, newest first');
  });

  it('labels switching by someone else and shows the measured change against the expected one', async () => {
    serve();
    await open('#/devices/heater_l1');

    const [row] = rows();
    expect(row!.getAttribute('data-kind')).toBe('observed');
    expect(cells(row!).slice(1)).toEqual(['on → offswitched by someone else', '-1.70 kW', '-1.65 kW', 'match']);
  });

  it('shows a mismatch as no response, newest first', async () => {
    serve();
    await open('#/devices/heater_l3');

    expect(rows().map((r) => r.getAttribute('data-verdict'))).toEqual(['mismatch', 'match']);
    expect(cells(rows()[0]!).slice(2)).toEqual(['+1.70 kW', '+0.01 kW', 'no response']);
    expect(rows()[0]!.querySelector('.badge')?.getAttribute('data-tone')).toBe('warn');
  });

  it('says so when nothing happened since the add-on started', async () => {
    serve();
    await open('#/devices/heater_l2');

    expect(rows()).toHaveLength(0);
    expect(card('Command verification').textContent).toContain('No change and no switching seen since the add-on last started.');
  });

  it('uses the mockup vocabulary in Polish', async () => {
    serve();
    await open('#/devices/heater_l1', 'pl');

    for (const heading of ['Podłączenie', 'Wartość energii i cel', 'Stan rozdziału mocy', 'Kontrola wykonania']) {
      expect(screen.getByRole('heading', { name: heading })).toBeTruthy();
    }
    const table = card('Kontrola wykonania');
    expect([...table.querySelectorAll('th')].map((th) => th.textContent)).toEqual(['Czas', 'Komenda', 'Oczekiwano', 'Zmierzono', 'Wynik']);
    expect(cells(table.querySelector('tbody tr') as HTMLTableRowElement).slice(1)).toEqual([
      'włączone → wyłączoneprzełączone poza systemem',
      '-1,70 kW',
      '-1,65 kW',
      'zgodne',
    ]);
    expect(within(card('Podłączenie')).getByText('Encja sterująca', { selector: 'dt' })).toBeTruthy();
  });
});

// The add-on writes 0 for values it never measured (shadow, timeout, no fresh sample). A placeholder
// 0 must never be shown as a measured 0: "the car drew 0 A" would send the user looking for a fault.
describe('measurement', () => {
  const t = messages.en;
  const entry = (patch: Partial<HistoryEntry>): HistoryEntry => ({
    at: new Date('2026-10-24T12:00:45Z'),
    kind: 'command',
    expected: 0,
    measured: 0,
    unit: 'watts',
    verdict: 'match',
    commandedAt: null,
    from: null,
    to: null,
    ...patch,
  });

  it.each(['not_applied', 'timed_out', 'unverifiable'] as const)('%s claims nothing was measured', (verdict) => {
    expect(measurement(entry({ verdict, expected: 6, measured: 9.6, unit: 'amperes' }), true, 'en', t)).toEqual({ expected: '—', measured: '—' });
  });

  it('an inconclusive setpoint check without a fresh draw shows neither the ceiling nor a draw', () => {
    expect(measurement(entry({ verdict: 'inconclusive', unit: 'amperes' }), true, 'en', t)).toEqual({ expected: '—', measured: '—' });
  });

  // A raised setpoint the car does not take is inconclusive with a real draw of 0 A: shown as measured.
  it('an inconclusive raised setpoint keeps its real 0 A draw', () => {
    expect(measurement(entry({ verdict: 'inconclusive', unit: 'amperes', expected: 16, measured: 0 }), true, 'en', t)).toEqual({
      expected: 'at most 16.0 A',
      measured: '0.0 A',
    });
  });

  it('an inconclusive step without a fresh sample keeps the expected change and claims no measured one', () => {
    expect(measurement(entry({ verdict: 'inconclusive', expected: 1700, measured: 0 }), false, 'en', t)).toEqual({ expected: '+1.70 kW', measured: '—' });
  });

  it('a step in amperes is a signed change, a setpoint a ceiling against the draw', () => {
    expect(measurement(entry({ verdict: 'mismatch', unit: 'amperes', expected: 7, measured: 0.2 }), false, 'en', t)).toEqual({ expected: '+7.0 A', measured: '+0.2 A' });
    expect(measurement(entry({ verdict: 'mismatch', unit: 'amperes', expected: 6, measured: 9.6 }), true, 'pl', messages.pl)).toEqual({
      expected: 'najwyżej 6,0 A',
      measured: '9,6 A',
    });
  });
});

// UI-12 (value and source part, ECO-13): the value per kWh now, the sources in words and the energy
// class open now with dispatch's own reason sentence. Zone ids are monospace like entity ids. The goal
// and history rows stay what they were, and without an economics: section the old words stay.
describe('device card: energy value, sources and what is allowed now (ECO-13)', () => {
  const serveEconomics = (answer: unknown, dispatchChange?: (p: any) => void) =>
    serveSamples(fetchMock, {
      'api/devices': devices,
      'api/dispatch': dispatchChange === undefined ? dispatch : changed(dispatch, dispatchChange),
      'api/power': power,
      'api/economics': answer,
    });

  async function openCard(id: string, lang: 'en' | 'pl' = 'en') {
    window.location.hash = `#/devices/${id}`;
    render(<App lang={lang} />);
    await advance();
    await advance();
    await advance();
    return card(lang === 'en' ? 'Energy value and goal' : 'Wartość energii i cel');
  }

  const VALUE = 'What 1 kWh is worth for this device';

  it('shows the static value and the value now with what it follows, with the currency label of the data', async () => {
    serveEconomics(economics);
    const section = await openCard('ev_charger');

    expect(field(section, VALUE).textContent).toContain('Static value: 2.03 EUR per kWh');
    expect(field(section, VALUE).textContent).toContain('Value now: 1.80 EUR per kWh');
    expect(field(section, VALUE).textContent).toContain('follows the import price × 3');
    expect(field(section, VALUE).textContent).toContain('the followed price is in use');
    // The 0.9 hint ("has no currency in this version") would now be untrue.
    expect(section.textContent).not.toContain('no currency in this version');
  });

  it('says a followed price that is not available is not the followed value', async () => {
    serveEconomics(changed(economics, (p) => Object.assign(p.devices[1], { valueBasis: 'fallback' })));
    const section = await openCard('ev_charger');

    expect(field(section, VALUE).textContent).toContain('the followed price is not available now, so the static value or the fallback price applies');
  });

  it('puts the sources in words: surplus only, surplus and named zones in monospace, any', async () => {
    serveEconomics(economics);
    const heater = await openCard('heater_l1');
    expect(field(heater, 'Allowed sources').textContent).toBe('surplus only: never energy from the grid');
    cleanup();

    const car = await openCard('ev_charger');
    expect(field(car, 'Allowed sources').textContent).toBe('surplus and these tariff zones: offpeak');
    expect([...field(car, 'Allowed sources').querySelectorAll('.mono')].map((e) => e.textContent)).toEqual(['offpeak']);
    cleanup();

    serveEconomics(changed(economics, (p) => Object.assign(p.devices[1], { sources: 'any', sourcesDeclared: 'any' })));
    expect(field(await openCard('ev_charger'), 'Allowed sources').textContent).toBe('any: surplus and every tariff zone');
  });

  it('says a declared zone that is not in the tariff was dropped, and shows what was declared', async () => {
    serveEconomics(changed(economics, (p) => Object.assign(p.devices[1], { sources: 'pv_and_zone:offpeak', sourcesDeclared: 'pv_and_zone:ghost,offpeak' })));
    const sources = field(await openCard('ev_charger'), 'Allowed sources');

    expect(sources.textContent).toContain('Declared as pv_and_zone:ghost,offpeak.');
    expect(sources.textContent).toContain('was dropped');
    expect(sources.querySelector('.mono')).not.toBeNull();
  });

  it('says a device without declared sources is judged as before, and claims no class for it', async () => {
    serveEconomics(changed(economics, (p) => Object.assign(p.devices[0], { sources: null, sourcesDeclared: null, class: 'none' })));
    const section = await openCard('heater_l1');

    expect(field(section, 'Allowed sources').textContent).toContain('Not declared: judged as before');
    expect(within(section).queryByText('Allowed now', { selector: 'dt' })).toBeNull();
  });

  it('shows the open energy class and dispatch’s reason sentence as allowed now', async () => {
    serveEconomics(economics, (p) => {
      p.devices.find((d: any) => d.id === 'ev_charger').reason = 'zone_allowed';
      p.devices.find((d: any) => d.id === 'heater_l1').reason = 'source_not_allowed';
    });
    const car = field(await openCard('ev_charger'), 'Allowed now');

    expect(car.textContent).toContain('the active tariff zone offpeak');
    expect(car.textContent).toContain('The active tariff zone is one of the device’s sources and its value covers the price, so it may run.');
    expect(car.querySelector('.mono')?.textContent).toBe('offpeak');
    cleanup();

    const heater = field(await openCard('heater_l1'), 'Allowed now');
    expect(heater.textContent).toContain('no energy class is open to this device now');
    expect(heater.textContent).toContain('None of the energy available now (surplus, the active tariff zone) is among the device’s sources.');
  });

  it('names the surplus class, and says dispatch has not run a step when the class is not known yet', async () => {
    serveEconomics(changed(economics, (p) => (p.devices[0].class = 'pv')));
    expect(field(await openCard('heater_l1'), 'Allowed now').textContent).toContain('surplus: energy that would otherwise be exported');
    cleanup();

    serveEconomics(changed(economics, (p) => (p.devices[0].class = null)));
    expect(field(await openCard('heater_l1'), 'Allowed now').textContent).toContain('dispatch has not evaluated a step yet');
  });

  it('says it all in Polish', async () => {
    serveEconomics(economics, (p) => (p.devices.find((d: any) => d.id === 'ev_charger').reason = 'zone_allowed'));
    const section = await openCard('ev_charger', 'pl');

    expect(field(section, 'Ile warta jest 1 kWh dla tego urządzenia').textContent).toContain('Wartość teraz: 1,80 EUR za kWh');
    expect(field(section, 'Dozwolone źródła').textContent).toBe('nadwyżka i te strefy taryfy: offpeak');
    expect(field(section, 'Dozwolone teraz').textContent).toContain('obowiązująca strefa taryfy offpeak');
  });

  it('shows the goal row from api/plan, and the history card as it was', async () => {
    serveSamples(fetchMock, { 'api/devices': devices, 'api/dispatch': dispatch, 'api/power': power, 'api/economics': economics, 'api/plan': plan });
    const section = await openCard('ev_charger');

    expect(field(section, 'Goal').textContent).toContain('reach 90 % state of charge');
    expect(card('Command verification')).toBeTruthy();
  });

  it('keeps the old words without an economics section: a static value without a currency, sources not available', async () => {
    serveEconomics(changed(economics, (p) => Object.assign(p, { configured: false, currency: null, tariff: null, settlement: null, savings: null })));
    const section = await openCard('ev_charger');

    expect(field(section, VALUE).textContent).toContain('2.03 per kWh');
    expect(field(section, VALUE).textContent).toContain('no currency in this version');
    expect(field(section, 'Allowed sources').textContent).toContain('Not available in this version');
    expect(within(section).queryByText('Allowed now', { selector: 'dt' })).toBeNull();
  });
});
