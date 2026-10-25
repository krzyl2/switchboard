import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import devicesSample from './api/devices.sample.json';
import excursionsSample from './api/excursions.sample.json';
import guardSample from './api/guard.sample.json';
import { GUARD_REFRESH_INTERVAL_MS } from './config';
import { localClock } from './localTime';
import { advance, respond } from './screens.test-support';

const START = new Date('2026-10-24T10:00:02Z');

let fetchMock: Mock<typeof fetch>;

function copy<T>(sample: T, change?: (p: any) => void): T {
  const payload = structuredClone(sample);
  change?.(payload);
  return payload;
}

type Responder = () => Promise<Response>;

/** Serves the three endpoints the page polls; each one swappable per test. Anything else is a 404. */
function serve({
  guard = () => respond(copy(guardSample)),
  excursions = () => respond(copy(excursionsSample)),
  devices = () => respond(copy(devicesSample)),
  post,
}: { guard?: Responder; excursions?: Responder; devices?: Responder; post?: (init: RequestInit | undefined) => Promise<Response> } = {}) {
  fetchMock.mockImplementation((input, init) => {
    if (input === 'api/guard') return guard();
    if (input === 'api/excursions') return excursions();
    if (input === 'api/devices') return devices();
    if (input === 'api/guard/kill-switch' && post !== undefined) return post(init);
    return respond({}, 404);
  });
}

async function open(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/guard';
  const view = render(<App lang={lang} />);
  await advance();
  return view;
}

function card(heading: string | RegExp): HTMLElement {
  return screen.getByRole('heading', { name: heading }).closest('section') as HTMLElement;
}

function rows(section: HTMLElement): string[][] {
  return [...section.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map((c) => c.textContent ?? ''));
}

function stateLabel(container: Element): { label: string; tone: string | null } {
  const label = container.querySelector('.state');
  return { label: label?.textContent ?? '', tone: label?.getAttribute('data-tone') ?? null };
}

function fact(container: HTMLElement, label: string): HTMLElement {
  const value = within(container).getByText(label, { selector: 'dt' }).nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error(`No value next to "${label}".`);
  return value;
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
  it('requests only the relative URLs api/guard, api/excursions and api/devices', async () => {
    serve();
    await open();

    // Relative, so the requests stay under the ingress prefix (the shell's mode banner polls api/mode
    // on every page: Shell.test).
    expect(fetchMock.mock.calls.map(([input]) => input).filter((input) => input !== 'api/mode')).toEqual(['api/guard', 'api/excursions', 'api/devices']);
  });

  it('shows the effective mode, the breaker and when the guard last evaluated', async () => {
    serve();
    await open();

    const overview = card('Limits and overloads');
    expect(overview.textContent).toContain('a thermal breaker allows them too');
    expect(fact(overview, 'Mode').textContent).toBe('ShadowComputes and logs, never switches.');
    expect(fact(overview, 'Breaker').textContent).toBe('25 A, characteristic B · continuous limit 28.25 A (1.13 × In)');
    expect(fact(overview, 'Last evaluation').querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-24T10:00:00.004Z');
    expect(within(overview).getByRole('link', { name: /decision log/ }).getAttribute('href')).toBe('#/log');
  });

  // MODE-01 rule from DECISIONS: an active add-on option is only a ceiling; this version stays shadow.
  it('says why an active add-on option still runs in shadow mode', async () => {
    serve({ guard: () => respond(copy(guardSample, (p) => (p.mode = { ceiling: 'active', uiChoice: 'active', effective: 'shadow', note: 'active_not_enabled' }))) });
    await open();

    expect(fact(card('Limits and overloads'), 'Mode').textContent).toContain('it stays in shadow mode');
  });

  // BRK-01 / docs/05: the tier table "with the amperes and kilowatts computed for this installation
  // rather than shown abstractly", the phases in each band, and why the durations are conservative.
  it('shows the tier table of this breaker in amperes and kilowatts with the phases in each band', async () => {
    serve();
    await open();

    const tiers = card('Breaker profile B25');
    expect(rows(tiers)).toEqual([
      ['up to 0.9×', '22.5 A', '5.18 kW', 'unlimited', 'may add load', 'L3'],
      ['0.9–1×', '25.0 A', '5.75 kW', 'unlimited', 'hold, add nothing', '—'],
      ['1–1.13×', '28.3 A', '6.50 kW', '15 min', 'reduce during the ordinary cycle', '—'],
      ['1.13–1.25×', '31.3 A', '7.19 kW', '3 min', 'reduce next cycle', '—'],
      ['1.25–1.45×', '36.3 A', '8.34 kW', '30 s', 'shed one device immediately', 'L1'],
      ['above 1.45×', '—', '—', '0 s', 'shed everything controlled on that phase', '—'],
    ]);
    expect(tiers.textContent).toContain('The durations are deliberately more conservative than the standard');
    expect(tiers.textContent).toContain('IEC 60898 only guarantees that a breaker does not trip within an hour at 1.13 × In');
    // Read-only in Stage 4: configuration editing is Stage 5, and the table says where it comes from.
    expect(tiers.textContent).toContain('Read-only in this version');
    expect(tiers.querySelector('input, select, button')).toBeNull();
  });

  // BRK-02 / BRK-04: per phase the budget as a bar, its trend and the time to full; the recovery rule
  // names the continuous current (recovery happens at or below it, not below the rating).
  it('shows each phase budget with its trend, recovery time and the phase state beside it', async () => {
    serve();
    await open();

    const budget = card('Overload budget');
    expect(budget.querySelector('.cardhead .sub')?.textContent).toBe(
      'thermal memory: recovers 1% per 6 s at or below the continuous current (28.3 A), from empty to full in 10 min',
    );
    const l1 = budget.querySelector('[data-phase="L1"]') as HTMLElement;
    expect(stateLabel(l1)).toEqual({ label: 'shedding', tone: 'crit' });
    expect(l1.querySelector('.lim-pct')?.textContent).toBe('62%');
    const bar = l1.querySelector('.budgetbar i') as HTMLElement;
    expect([bar.style.width, bar.getAttribute('data-tone')]).toEqual(['62%', 'crit']);
    expect(l1.textContent).toContain('being consumed · full again in 3 min');
    expect(l1.textContent).toContain('33.0 A (1.32×)');
    // The reading's age on the add-on clock: the guard evaluated 3.994 s after the report.
    expect(l1.querySelector('time.age')?.textContent).toBe('reported 3 s ago');
    expect(l1.textContent).toContain('reduction requested (reserve threshold)');
    // Tied to the recorded excursion of that phase (the mockup's "used at 09:14").
    expect(l1.textContent).toContain(`last above the reserve threshold at ${localClock(new Date('2026-10-22T09:20:00.01Z'), 'en')}, peak 19.1 A`);

    const l2 = budget.querySelector('[data-phase="L2"]') as HTMLElement;
    expect(stateLabel(l2)).toEqual({ label: 'safe state', tone: 'crit' });
    expect(l2.textContent).toContain('held: no valid data · full again in 60 s');
    expect(l2.textContent).toContain('current reading stale');
    expect((l2.querySelector('.budgetbar i') as HTMLElement).getAttribute('data-tone')).toBe('muted');
    const l3 = budget.querySelector('[data-phase="L3"]') as HTMLElement;
    expect(l3.textContent).toContain('-12.4 A');
    expect(l3.textContent).toContain('export');
    expect(l3.querySelector('.lim-pct')?.textContent).toBe('100%');
  });

  // DISP-02 / 04 §2 as the mockup lists it: steps in shed order, a modulating device reduced to its
  // minimum first, its value per kWh and how fast its draw changes; never-shed devices stay listed.
  it('lists the shed order as steps with names, value per kWh, shed speed and the state now', async () => {
    serve();
    await open();

    const shed = card('What Switchboard sheds first');
    expect(rows(shed).map((r) => r.slice(0, 6))).toEqual([
      ['1', 'boiler', 'L2, L3', 'stage 2: switch off', '0.35shed priority 0.1, instead of the value', '—'],
      ['2', 'boiler', 'L2, L3', 'stage 1: switch off', '0.35shed priority 0.1, instead of the value', '—'],
      ['3', 'Grzałka CWU L1heater_l1', 'L1', 'switch off', '0.35', '1 s'],
      ['4', 'Grzałka CWU L2heater_l2', 'L2', 'switch off', '0.35', '1 s'],
      ['5', 'Ładowarka go-eev_charger', 'L1', 'reduce to 6 A', '2.03', '20 s'],
      ['—', 'Ładowarka go-eev_charger', 'L1', 'cannot be switched off (no stop_actuator declared): held at its minimum at most', '2.03', '20 s'],
      ['—', 'heat_pump', 'L3', 'never shed (no_shed)', '1.2', '—'],
    ]);
    // The state now on each device's first row, with a text label and the reason.
    const heater = shed.querySelector('tr[data-shed-device="heater_l1"]') as HTMLElement;
    expect(stateLabel(heater)).toEqual({ label: 'held off', tone: 'crit' });
    expect(heater.textContent).toContain('Phase current above a tier of the breaker profile.');
    expect(shed.textContent).toContain('Shadow mode: limits are recorded in the decision log, nothing is switched.');
    expect(shed.textContent).toContain('power cannot move between phases');
  });

  it('numbers the shed order without the device names when the configuration cannot be read', async () => {
    serve({ devices: () => respond({}, 500) });
    await open();

    const shed = card('What Switchboard sheds first');
    expect(rows(shed)[2]?.slice(0, 6)).toEqual(['3', 'heater_l1', 'L1', 'switch off', '0.35', '—']);
    // Without the range the minimum is not known; the step still says what it does.
    expect(rows(shed)[4]?.[3]).toBe('reduce to its minimum');
  });

  // SAFE-01: the safe state is announced above everything else, with its cause, and in shadow mode
  // says that nothing is switched rather than claiming the devices are off.
  it('announces a safe state on some phases with its reason and what shadow mode would do', async () => {
    serve();
    await open();

    const alert = screen.getAllByRole('alert')[0] as HTMLElement;
    expect(alert.textContent).toContain('Safe state on L2');
    expect(alert.textContent).toContain('A signal the limits depend on is stale or unavailable.');
    expect(alert.textContent).toContain('In shadow mode nothing is switched: Switchboard would switch off every controlled device on these phases.');
  });

  it('announces a safe state everywhere before the guard has a configuration, without tables', async () => {
    serve({
      guard: () =>
        respond(
          copy(guardSample, (p) => {
            Object.assign(p, { breaker: null, phases: [], devices: [], decisions: [] });
            p.safeState = { active: true, scope: 'all', reason: 'safe_state', phases: [], since: p.at };
          }),
        ),
    });
    await open();

    expect(screen.getByRole('alert').textContent).toContain('Safe state on every phase');
    expect(screen.getByText(/No valid configuration/)).toBeTruthy();
    expect(screen.queryByRole('heading', { name: /Breaker profile/ })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'Overload budget' })).toBeNull();
    expect(screen.queryByRole('heading', { name: 'What Switchboard sheds first' })).toBeNull();
  });

  it('warns when the decision log cannot be written, and when state.json was set aside', async () => {
    serve({
      guard: () =>
        respond(
          copy(guardSample, (p) => {
            p.decisionLog = { writable: false, lastError: 'No space left on device', dropped: 7 };
            p.stateFile = { status: 'reset_corrupt', warning: 'schema_version is 9.' };
          }),
        ),
    });
    await open();

    const log = screen.getByText(/The decision log cannot be written/).closest('.banner') as HTMLElement;
    expect(log.textContent).toContain('Decisions lost while the log could not be written: 7');
    expect((screen.getByText(/state.json could not be read/).closest('.banner') as HTMLElement).textContent).toContain('schema_version is 9.');
  });

  // A frozen page must not look like a calm installation: on failure every guard value is withdrawn.
  it('hides every guard value after a failed fetch and shows the last successful update', async () => {
    let fail = false;
    serve({ guard: () => (fail ? Promise.reject(new TypeError('Failed to fetch')) : respond(copy(guardSample))) });
    await open();
    expect(card('Overload budget')).toBeTruthy();

    fail = true;
    await advance(GUARD_REFRESH_INTERVAL_MS);

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Limits unavailable');
    expect(alert.querySelector('time')?.getAttribute('dateTime')).toBe(START.toISOString());
    expect(screen.queryByRole('heading', { name: 'Overload budget' })).toBeNull();
    expect(screen.queryByRole('heading', { name: /Breaker profile/ })).toBeNull();
    expect(screen.queryByText(/62%/)).toBeNull();
    expect(screen.queryByRole('button', { name: /kill switch/ })).toBeNull();
  });

  it('treats a guard payload it does not understand as unavailable instead of guessing', async () => {
    serve({ guard: () => respond(copy(guardSample, (p) => (p.breaker.tiers[0].reaction = 'made_up'))) });
    await open();

    expect(screen.getByRole('alert').textContent).toContain('Limits unavailable');
    expect(screen.queryByText('22.5 A')).toBeNull();
  });
});

// docs/05: "a real recorded excursion rendered as a small chart: current over time, both thresholds
// marked, the shed moment annotated". The sample is the add-on serializer's record of the synthetic
// day's L1 excursion at 09:20.
describe('recorded excursion', () => {
  it('draws the recorded excursion with the reserve threshold, the breaker and the continuous limit', async () => {
    serve();
    await open();

    const chart = card('Recorded excursion');
    const from = localClock(new Date('2026-10-22T09:20:00.01Z'), 'en');
    const to = localClock(new Date('2026-10-22T09:23:00.01Z'), 'en');
    expect(chart.querySelector('.cardhead .sub')?.textContent).toBe(`L1, ${from}–${to}`);
    const svg = chart.querySelector('svg') as SVGElement;
    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.getAttribute('aria-label')).toContain('peak 19.1 A');
    expect([...svg.querySelectorAll('[data-limit] text')].map((t) => t.textContent)).toEqual([
      'breaker 25.0 A',
      'continuous limit 28.3 A',
      'reserve threshold 14.1 A',
    ]);
    expect(svg.querySelector('path.trace')?.getAttribute('d')).toMatch(/^M\d/);
  });

  // In shadow mode the would-shed moment is annotated as what Switchboard would have done; the
  // charger's reduction (a lowered setpoint, the main relief on L1) is a shed, not a footnote.
  it('annotates the would-shed moment and lists every decision with its time, reason and current', async () => {
    serve();
    await open();

    const chart = card('Recorded excursion');
    expect([...chart.querySelectorAll('svg .annot tspan')].map((t) => t.textContent)).toEqual(['would shed', 'Ładowarka go-e']);
    expect([...chart.querySelectorAll('svg .mark')].map((m) => m.getAttribute('data-kind'))).toEqual(['event', 'event', 'reduce']);
    const items = [...chart.querySelectorAll('.lim-marks li')];
    expect(items.map((li) => li.getAttribute('data-action'))).toEqual(['foreign_load_detected', 'request_reduction', 'set_setpoint']);
    expect(items[0]?.textContent).toContain('Foreign load detected');
    expect(items[0]?.textContent).toContain('Load appeared on the phase without a command of ours.');
    expect(items[2]?.textContent).toContain('would lower the current of Ładowarka go-e');
    // Reason list version 3: a reduction caused by a detected foreign load says so (version 2 said reserve_threshold).
    expect(items[2]?.textContent).toContain('Load appeared on the phase without a command of ours.');
    expect(items[2]?.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-22T09:21:00.000Z');
    expect(chart.textContent).toContain('Switchboard switched nothing (shadow mode)');
    expect(chart.textContent).toContain(`Peak 19.1 A at ${localClock(new Date('2026-10-22T09:20:10.01Z'), 'en')}; above the reserve threshold for 3 min.`);
    expect(chart.textContent).toContain('the peak and every decision moment are kept');
  });

  // A reduction deferred by min_on takes effect only at the lockout's expiry: the chart annotates
  // that moment, not the earlier deferral, which stays listed as waiting.
  it('annotates the reduction that took effect, not an earlier deferral', async () => {
    const deferral = { id: 'replay-000000038', at: '2026-10-22T09:20:30+00:00', action: 'deferred', reason: 'reserve_threshold', device: 'heater_l1', commanded: false };
    serve({ excursions: () => respond(copy(excursionsSample, (p) => p.phases[0].worst.decisions.splice(1, 0, deferral))) });
    await open();

    const chart = card('Recorded excursion');
    expect([...chart.querySelectorAll('svg .annot tspan')].map((t) => t.textContent)).toEqual(['would shed', 'Ładowarka go-e']);
    expect([...chart.querySelectorAll('.lim-marks li')].map((li) => li.getAttribute('data-action'))).toContain('deferred');
  });

  it('words a commanded shed as done, without the shadow sentence', async () => {
    serve({ excursions: () => respond(copy(excursionsSample, (p) => p.phases[0].worst.decisions.forEach((d: any) => (d.commanded = d.device !== null)))) });
    await open();

    const chart = card('Recorded excursion');
    expect([...chart.querySelectorAll('svg .annot tspan')].map((t) => t.textContent)).toEqual(['shed', 'Ładowarka go-e']);
    expect(chart.textContent).toContain('lowered the current of Ładowarka go-e');
    expect(chart.textContent).not.toContain('Switchboard switched nothing');
  });

  // Two records per phase (red-team correction): routine excursions never displace the worst one,
  // which is shown by default; the most recent one stays selectable.
  it('shows the worst excursion by default and lets the most recent one be chosen', async () => {
    serve({
      excursions: () =>
        respond(
          copy(excursionsSample, (p) => {
            const last = structuredClone(p.phases[0].worst);
            last.start = '2026-10-22T09:35:00.01+00:00';
            last.end = null;
            last.active = true;
            last.peakA = 15.2;
            last.peakAt = '2026-10-22T09:35:10.01+00:00';
            last.samples = [{ at: '2026-10-22T09:35:00.01+00:00', a: 15.2 }, { at: '2026-10-22T09:39:50.00+00:00', a: 14.9 }];
            last.decisions = [];
            last.decimated = false;
            p.phases[0].last = last;
          }),
        ),
    });
    await open();

    const chart = card('Recorded excursion');
    const select = within(chart).getByRole('combobox', { name: 'Excursion shown' }) as HTMLSelectElement;
    expect([...select.options].map((o) => o.textContent)).toEqual(['L1: highest peak (19.1 A)', 'L1: most recent (15.2 A)']);
    expect(select.value).toBe('L1:worst');

    await act(async () => {
      fireEvent.change(select, { target: { value: 'L1:last' } });
    });

    expect(chart.querySelector('.cardhead .sub')?.textContent).toBe(`L1, since ${localClock(new Date('2026-10-22T09:35:00.01Z'), 'en')} (still above)`);
    // An ongoing excursion says how old its last reading is, on the add-on's clock (09:40:02).
    expect(chart.textContent).toContain('above the reserve threshold for 5 min so far, last reading 12 s ago.');
    expect(chart.textContent).toContain('No decision was taken during this excursion.');
  });

  it('says so when no excursion is on record', async () => {
    serve({ excursions: () => respond(copy(excursionsSample, (p) => (p.phases[0].last = p.phases[0].worst = null))) });
    await open();

    const chart = card('Recorded excursion');
    expect(chart.textContent).toContain('No excursion above the reserve threshold is on record.');
    expect(chart.querySelector('svg')).toBeNull();
  });

  it('reports an excursions file that was set aside or cannot be written', async () => {
    serve({
      excursions: () =>
        respond(copy(excursionsSample, (p) => (p.stateFile = { status: 'reset_corrupt', warning: 'samples out of order', writeError: 'Read-only file system' }))),
    });
    await open();

    const chart = card('Recorded excursion');
    expect(chart.textContent).toContain('excursions.json could not be read and was set aside as excursions.json.corrupt');
    expect(chart.textContent).toContain('samples out of order');
    expect(chart.textContent).toContain('excursions.json cannot be written: Read-only file system');
  });

  // Only that card's values go when its request fails: the limits stay on screen.
  it('withdraws the chart when its request fails and keeps the limits', async () => {
    serve({ excursions: () => respond({}, 500) });
    await open();

    const chart = card('Recorded excursion');
    expect(within(chart).getByRole('alert').textContent).toContain('Unavailable');
    expect(chart.querySelector('svg')).toBeNull();
    expect(card('Breaker profile B25')).toBeTruthy();
  });
});

// SAFE-03 from the page: the kill switch's state with where it was set, and the control that
// engages or releases it through the add-on's own JSON endpoint (no Home Assistant entity is
// commanded by it).
describe('kill switch', () => {
  it('shows a released kill switch, where it was last set, and what engaging it does in shadow mode', async () => {
    serve();
    await open();

    const kill = card('Kill switch');
    expect(stateLabel(kill)).toEqual({ label: 'Kill switch released', tone: 'ok' });
    expect(kill.textContent).toContain('from Home Assistant, since');
    expect(kill.querySelector('time')?.getAttribute('dateTime')).toBe('2026-10-22T12:10:00.000Z');
    expect(kill.textContent).toContain('In shadow mode nothing is switched');
    // No banner claims an engagement that is not in force.
    expect([...document.querySelectorAll('.banner')].map((b) => b.textContent).join(' ')).not.toContain('Kill switch');
  });

  it('engages it with a JSON post and shows the evaluated answer at once', async () => {
    const engaged = copy(guardSample, (p) => {
      p.at = '2026-10-24T10:00:01.000+00:00';
      p.killSwitch = { engaged: true, changedAt: '2026-10-24T10:00:01.000+00:00', source: 'api', locked: false, switchesDevices: false };
    });
    serve({ post: () => respond(engaged) });
    await open();

    await act(async () => {
      fireEvent.click(within(card('Kill switch')).getByRole('button', { name: 'Engage the kill switch' }));
    });
    await advance();

    const post = fetchMock.mock.calls.find(([input]) => input === 'api/guard/kill-switch');
    expect(post?.[1]?.method).toBe('POST');
    expect(post?.[1]?.body).toBe('{"engaged":true}');
    expect((post?.[1]?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(stateLabel(card('Kill switch'))).toEqual({ label: 'Kill switch engaged', tone: 'crit' });
    expect(within(card('Kill switch')).getByRole('button', { name: 'Release the kill switch' })).toBeTruthy();
    const banner = screen.getAllByRole('alert')[0] as HTMLElement;
    expect(banner.textContent).toContain('Kill switch engaged');
    expect(banner.textContent).toContain('In shadow mode nothing is switched');
  });

  it('says when the change was made but not evaluated in time', async () => {
    serve({ post: () => respond({}, 503) });
    await open();

    await act(async () => {
      fireEvent.click(within(card('Kill switch')).getByRole('button', { name: 'Engage the kill switch' }));
    });
    await advance();

    expect(within(card('Kill switch')).getByRole('status').textContent).toBe('Changed, but not evaluated in time; the next refresh shows it.');
  });

  it('shows an engaged kill switch from Home Assistant as a banner above everything', async () => {
    serve({ guard: () => respond(copy(guardSample, (p) => (p.killSwitch = { engaged: true, changedAt: '2026-10-24T09:58:00.000+00:00', source: 'mqtt', locked: false, switchesDevices: false }))) });
    await open();

    const alert = screen.getAllByRole('alert')[0] as HTMLElement;
    expect(stateLabel(alert)).toEqual({ label: 'Kill switch engaged', tone: 'crit' });
    expect(alert.textContent).toContain('Every controlled device is held off. In shadow mode nothing is switched');
  });
});

describe('limits page in Polish', () => {
  it('renders headings, tables, the chart and the kill switch with the mockup vocabulary', async () => {
    serve();
    await open('pl');

    expect(screen.getByRole('heading', { name: 'Limity i przeciążenia' })).toBeTruthy();
    expect(rows(card('Profil bezpiecznika B25'))[0]).toEqual(['do 0,9×', '22,5 A', '5,18 kW', 'bez ograniczeń', 'wolno dokładać obciążenie', 'L3']);
    expect(card('Profil bezpiecznika B25').textContent).toContain('Wartości czasów są celowo ostrożniejsze niż norma.');
    expect(card('Budżet przeciążenia').querySelector('.cardhead .sub')?.textContent).toBe(
      'pamięć cieplna: odbudowa 1% na 6 s pracy na granicy pracy ciągłej (28,3 A) lub poniżej, od zera do pełna w 10 min',
    );
    const shed = card('Co system zrzuca najpierw');
    expect(shed.textContent).toContain('zejdź do 6 A');
    expect([...shed.querySelectorAll('th')].map((th) => th.textContent)).toEqual(['Kolejność', 'Urządzenie', 'Faza', 'Krok', 'Wartość kWh', 'Czas zrzutu', 'Teraz']);
    const chart = card('Zarejestrowany przebieg');
    expect(chart.querySelector('svg .annot')?.textContent).toBe('zrzut (niewykonany)Ładowarka go-e');
    expect(chart.textContent).toContain('obniżenie prądu (niewykonane): Ładowarka go-e');
    expect(chart.textContent).toContain('Switchboard niczego nie przełączył (tryb Obserwacja)');
    expect(stateLabel(card('Wstrzymanie sterowania')).label).toBe('Wstrzymanie sterowania wyłączone');
    expect(screen.getByRole('button', { name: 'Włącz wstrzymanie sterowania' })).toBeTruthy();
    expect(within(screen.getByRole('navigation')).getByText('Limity i przeciążenia').getAttribute('aria-current')).toBe('page');
    expect(screen.getByRole('note').textContent).toContain('To nie jest urządzenie zabezpieczające.');
  });
});
