import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import devices from './api/devices.sample.json';
import dispatch from './api/dispatch.sample.json';
import ev from './api/ev.sample.json';
import plan from './api/plan.sample.json';
import power from './api/power.sample.json';
import { messages } from './i18n';
import { advance, respond } from './screens.test-support';

// EV-10: the EV card of the day plan and the charger row of the device card. The card says what the add-on reports and nothing more: the
// module's reason in words, what it asks for (hard or soft, spread, from which classes), the car, the next trip, the calendar events it ignored
// and the warning that one may hide a trip, the inputs that are not fresh. Without an ev: section there is no card. The manual control exists
// only when the add-on has it (S6), "charge now" only at planner.dispatch: full, and a refusal is worded; the only writes are POST and DELETE
// api/ev/..., the module's own state, never an entity.
let fetchMock: Mock<typeof fetch>;
let evAnswer: unknown;
let evStatus: number;
let writes: { url: string; method: string; body: unknown }[];
let writeStatus: number;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-26T17:02:00Z'));
  evAnswer = ev;
  evStatus = 200;
  writes = [];
  writeStatus = 200;
  fetchMock = vi.fn<typeof fetch>();
  fetchMock.mockImplementation((input, init) => {
    const url = String(input).split('?')[0]!;
    if (init?.method === 'POST' || init?.method === 'DELETE') {
      writes.push({ url, method: init.method, body: init.body === undefined ? undefined : JSON.parse(String(init.body)) });
      return respond({}, writeStatus);
    }
    const samples: Record<string, unknown> = { 'api/devices': devices, 'api/dispatch': dispatch, 'api/power': power, 'api/plan': plan };
    if (url === 'api/ev') return respond(structuredClone(evAnswer), evStatus);
    return url in samples ? respond(structuredClone(samples[url])) : respond({}, 404);
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  window.location.hash = '';
});

function changed<T>(sample: T, change: (copy: any) => void): T {
  const copy = structuredClone(sample);
  change(copy);
  return copy;
}

const MANUAL_IDLE = { mode: 'auto', chargeNow: { active: false, targetPct: null, until: null } };

async function openPlan(lang: 'en' | 'pl' = 'en') {
  window.location.hash = '#/plan';
  render(<App lang={lang} />);
  await advance();
  await advance();
}

async function openDevice(id: string, lang: 'en' | 'pl' = 'en') {
  window.location.hash = `#/devices/${id}`;
  render(<App lang={lang} />);
  await advance();
  await advance();
  await advance();
}

function card(): HTMLElement {
  return document.querySelector('[data-card="ev"]') as HTMLElement;
}

const row = (name: string) => card().querySelector(`[data-row="${name}"]`) as HTMLElement;

describe('EV card: what the module reports', () => {
  it('shows the reason in words, the car, the next trip and what the module asks for', async () => {
    await openPlan();

    expect(card()).not.toBeNull();
    expect(within(card()).getByRole('heading', { name: 'Car charging (EV module)' })).not.toBeNull();
    expect(card().querySelector('[data-ev="reason"]')!.textContent).toContain('The next trip sets the goal');
    expect(row('ev-soc').textContent).toBe('40 %');
    expect(row('ev-plugged').textContent).toContain('plugged in');
    expect(row('ev-trip').textContent).toContain('Kurs Warszawa: 42 km');
    expect(row('ev-mode').textContent).toBe('Automatic');
    expect(row('ev-consumption').textContent).toContain('0.133 kWh/km');
    expect(row('ev-last-full').textContent).not.toBe('');
    const goal = card().querySelector('[data-ev-goal="trip"]') as HTMLElement;
    expect(goal.textContent).toContain('Trip: charge to 71 %');
    expect(goal.textContent).toContain('hard deadline');
    expect(goal.textContent).toContain('spread over the window');
    expect(goal.textContent).toContain('6.16 kWh');
  });

  it('says that with planner.dispatch off the module is only a preview (the note, not a guess)', async () => {
    await openPlan();

    expect(card().querySelector('[data-ev-note="ev_without_planner_dispatch"]')!.textContent).toContain('preview only');
  });

  it('puts the entity ids and zone ids in monospace', async () => {
    await openPlan();

    expect(row('ev-device').querySelector('code.mono')!.textContent).toBe('ev_charger');
    expect(card().querySelector('[data-ev-goal="sources"] .mono')!.textContent).toBe('offpeak');
    expect(row('ev-plugged').querySelector('.mono')!.textContent).toBe('Charging');
  });

  it('has no card without an ev: section, and none when the add-on does not answer', async () => {
    evAnswer = changed(ev, (p) => Object.assign(p, { enabled: false, config: null, parameters: null, addOnZone: null, inputs: null, trips: null, assessment: null, manual: null }));
    await openPlan();
    expect(card()).toBeNull();
    cleanup();

    evStatus = 503;
    await openPlan();
    expect(card()).toBeNull();
    expect(document.querySelector('[data-card="plan-head"]')).not.toBeNull();
  });

  it('says what a failed computation means: no goals, the car follows the v2 rules', async () => {
    evAnswer = changed(ev, (p) =>
      Object.assign(p.assessment, { failed: true, reason: null, notes: [], computedAt: null, validUntil: null, expired: true, mode: null, socPercent: null, kwhPerKm: null, nextTrip: null, lastFullAt: null, goals: [] }),
    );
    await openPlan();

    expect(within(card()).getByRole('alert').textContent).toContain('v2 rules');
    expect(card().querySelector('[data-ev="reason"]')).toBeNull();
    expect(card().querySelector('[data-ev="goals"]')).toBeNull();
  });

  it('says when the last answer is out of date', async () => {
    evAnswer = changed(ev, (p) => (p.assessment.expired = true));
    await openPlan();

    expect(card().querySelector('[data-ev="expired"]')!.textContent).toContain('out of date');
  });

  it('says why there is no state of charge instead of showing a number', async () => {
    evAnswer = changed(ev, (p) => Object.assign(p.assessment, { socPercent: null, socGap: 'drift_exceeded', reason: 'ev_soc_stale', goals: [] }));
    await openPlan();

    expect(row('ev-soc').textContent).toContain('no estimate');
    expect(row('ev-soc').textContent).toContain('drift limit');
    expect(card().querySelector('[data-ev="reason"]')!.textContent).toContain('stale');
    expect(card().querySelector('[data-ev="goals-none"]')).not.toBeNull();
  });

  it('lists the ignored calendar events and warns that one may hide the next trip', async () => {
    evAnswer = changed(ev, (p) => {
      p.trips.nextTrip = null;
      p.assessment.nextTrip = null;
      p.trips.unparsed = [
        { title: 'Dentysta', start: '2026-10-26T18:00:00+00:00', reason: 'no_km', km: null },
        { title: 'Urlop', start: '2026-10-28T00:00:00+00:00', reason: 'all_day', km: null },
      ];
      p.trips.warnings = [{ kind: 'next_trip_may_be_hidden', title: 'Dentysta', start: '2026-10-26T18:00:00+00:00' }];
    });
    await openPlan();

    expect(row('ev-trip').textContent).toBe('no trip in the calendar');
    expect(card().querySelector('[data-ev-warning="next_trip_may_be_hidden"]')!.textContent).toContain('a trip behind it is hidden');
    const items = [...card().querySelectorAll('[data-ev-unparsed]')].map((li) => li.textContent);
    expect(items).toHaveLength(2);
    expect(items[0]).toContain('Dentysta');
    expect(items[0]).toContain('no “km:”');
    expect(items[1]).toContain('all-day');
  });

  it('opens the inputs by itself when one is not fresh, and keeps them closed when all are', async () => {
    await openPlan();
    expect((card().querySelector('[data-ev="inputs"]') as HTMLDetailsElement).open).toBe(false);
    cleanup();

    evAnswer = changed(ev, (p) => Object.assign(p.inputs.calendar, { state: 'unavailable', detail: 'state_unavailable' }));
    await openPlan();

    const inputs = card().querySelector('[data-ev="inputs"]') as HTMLDetailsElement;
    expect(inputs.open).toBe(true);
    expect(inputs.querySelector('[data-ev-input="calendar"]')!.textContent).toContain('unavailable');
    expect(inputs.querySelector('[data-ev-input="soc"]')!.textContent).toContain('fresh');
  });

  it('names an input the configuration does not map, and says the range is the electric one', async () => {
    evAnswer = changed(ev, (p) => {
      p.inputs.range = { configured: false, state: 'unmapped', detail: 'none', readAt: null, ageS: null, maxAgeS: null };
      p.inputs.rangeKm = null;
    });
    await openPlan();

    expect(card().querySelector('[data-ev-input="range"]')!.textContent).toContain('not mapped');
  });

  it('shows the configuration issues in words, falling back to the add-on’s message for a code it has no sentence for', async () => {
    evAnswer = changed(ev, (p) => {
      p.issues = [
        { code: 'ev_calendar_zone_mismatch', path: 'ev.calendar', line: null, column: null, message: 'zone' },
        { code: 'a_code_from_the_future', path: 'ev.x', line: null, column: null, message: 'The add-on’s own message.' },
      ];
    });
    await openPlan();

    const items = [...card().querySelectorAll('[data-ev="issues"] li')].map((li) => li.textContent);
    expect(items[0]).toContain('would start at the wrong hour');
    expect(items[1]).toContain('The add-on’s own message.');
  });

  it('is in Polish with the same facts', async () => {
    await openPlan('pl');

    expect(within(card()).getByRole('heading', { name: 'Ładowanie auta (moduł EV)' })).not.toBeNull();
    expect(card().querySelector('[data-ev="reason"]')!.textContent).toContain('Cel wyznacza najbliższy kurs');
    expect(row('ev-mode').textContent).toBe('Automatyczny');
    expect((card().querySelector('[data-ev-goal="trip"]') as HTMLElement).textContent).toContain('Kurs: naładuj do 71 %');
  });

  it('makes no write while the person does nothing', async () => {
    await openPlan();
    await advance(60_000);

    expect(writes).toEqual([]);
  });
});

describe('EV card: the manual control (S6)', () => {
  it('is absent while the add-on has no manual control (an older add-on writes no `manual` key)', async () => {
    evAnswer = changed(ev, (p) => delete p.manual);
    await openPlan();

    expect(card().querySelector('[data-ev="controls"]')).toBeNull();
  });

  it('offers the module’s own modes and never “off”, which is the device’s override', async () => {
    evAnswer = changed(ev, (p) => (p.manual = MANUAL_IDLE));
    await openPlan();

    const select = card().querySelector('[data-form="ev-mode"] select') as HTMLSelectElement;
    expect([...select.options].map((o) => o.value)).toEqual(['auto', 'pv_only', 'full_charge']);
    expect(select.value).toBe('auto');
    expect(card().querySelector('[data-form="ev-mode"]')!.textContent).toContain('the module never sets it');
  });

  it('posts the chosen mode and reads the answer again', async () => {
    evAnswer = changed(ev, (p) => (p.manual = MANUAL_IDLE));
    await openPlan();
    const before = fetchMock.mock.calls.filter(([i]) => String(i) === 'api/ev').length;

    await act(async () => {
      fireEvent.change(card().querySelector('[data-form="ev-mode"] select')!, { target: { value: 'full_charge' } });
    });
    await advance();

    expect(writes).toEqual([{ url: 'api/ev/mode', method: 'POST', body: { mode: 'full_charge' } }]);
    expect(fetchMock.mock.calls.filter(([i]) => String(i) === 'api/ev').length).toBe(before + 1);
    expect(card().querySelector('[data-ev="message"]')!.textContent).toBe('Saved.');
  });

  it('words a refusal of the mode and goes back to what the add-on holds', async () => {
    evAnswer = changed(ev, (p) => (p.manual = { ...MANUAL_IDLE, mode: 'pv_only' }));
    writeStatus = 404;
    await openPlan();

    await act(async () => {
      fireEvent.change(card().querySelector('[data-form="ev-mode"] select')!, { target: { value: 'full_charge' } });
    });
    await advance();

    expect(card().querySelector('[data-ev="message"]')!.textContent).toBe('The configuration has no EV module.');
    expect((card().querySelector('[data-form="ev-mode"] select') as HTMLSelectElement).value).toBe('pv_only');
  });

  it('keeps “charge now” closed at planner.dispatch off and says what to use instead', async () => {
    evAnswer = changed(ev, (p) => (p.manual = MANUAL_IDLE));
    await openPlan();

    const form = card().querySelector('[data-form="ev-charge-now"]') as HTMLElement;
    expect(form.querySelector('[data-ev="charge-needs-full"]')!.textContent).toContain('planner.dispatch: full (now: off)');
    expect(form.querySelector('[data-ev="charge-needs-full"]')!.textContent).toContain('“force on” override');
    expect((within(form).getByRole('button', { name: 'Charge now' }) as HTMLButtonElement).disabled).toBe(true);
    // Why: a field that can be typed in but never sent is a lie; both inputs are closed with the button, and open at full.
    expect([...form.querySelectorAll('input')].map((i) => i.disabled)).toEqual([true, true]);
  });

  it('opens the target and the duration at planner.dispatch full', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();

    const form = card().querySelector('[data-form="ev-charge-now"]') as HTMLElement;
    expect([...form.querySelectorAll('input')].map((i) => i.disabled)).toEqual([false, false]);
  });

  it('takes a target below 1 %, like the add-on (above 0 and at most 100)', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();
    const form = card().querySelector('[data-form="ev-charge-now"]') as HTMLElement;

    fireEvent.input(form.querySelector('input')!, { target: { value: '0.5' } });
    await act(async () => {
      fireEvent.submit(form);
    });
    await advance();

    expect(writes).toEqual([{ url: 'api/ev/charge-now', method: 'POST', body: { targetPct: 0.5 } }]);
  });

  it('says so when there is no planner: section at all', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = null;
    });
    await openPlan();

    expect(card().querySelector('[data-ev="charge-needs-full"]')!.textContent).toContain('no planner: section');
  });

  it('starts a charge at planner.dispatch full with the typed target and duration (hours sent as minutes)', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();
    const form = card().querySelector('[data-form="ev-charge-now"]') as HTMLElement;
    expect(form.querySelector('[data-ev="charge-needs-full"]')).toBeNull();
    // The placeholders are the add-on's own defaults (control.ev.charge_now): 90 % for 12 hours.
    const [target, hours] = [...form.querySelectorAll('input')] as HTMLInputElement[];
    expect(target!.placeholder).toBe('90');
    expect(hours!.placeholder).toBe('12');

    fireEvent.input(target!, { target: { value: '80' } });
    fireEvent.input(hours!, { target: { value: '1,5' } });
    await act(async () => {
      fireEvent.submit(form);
    });
    await advance();

    expect(writes).toEqual([{ url: 'api/ev/charge-now', method: 'POST', body: { targetPct: 80, durationMinutes: 90 } }]);
    expect(card().querySelector('[data-ev="message"]')!.textContent).toBe('Started.');
  });

  it('sends nothing but the empty request when the fields are left to the defaults', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();

    await act(async () => {
      fireEvent.submit(card().querySelector('[data-form="ev-charge-now"]')!);
    });
    await advance();

    expect(writes).toEqual([{ url: 'api/ev/charge-now', method: 'POST', body: {} }]);
  });

  it.each([
    ['0', 'Enter a target above 0 and at most 100 %.'],
    ['101', 'Enter a target above 0 and at most 100 %.'],
    ['abc', 'Enter a target above 0 and at most 100 %.'],
  ])('refuses the target %s before asking the add-on', async (typed, sentence) => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();
    const form = card().querySelector('[data-form="ev-charge-now"]') as HTMLElement;

    fireEvent.input(form.querySelector('input')!, { target: { value: typed } });
    await act(async () => {
      fireEvent.submit(form);
    });

    expect(card().querySelector('[data-ev="message"]')!.textContent).toBe(sentence);
    expect(writes).toEqual([]);
  });

  it('words the add-on’s 409 (charge now needs planner.dispatch full)', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    writeStatus = 409;
    await openPlan();

    await act(async () => {
      fireEvent.submit(card().querySelector('[data-form="ev-charge-now"]')!);
    });
    await advance();

    expect(card().querySelector('[data-ev="message"]')!.textContent).toContain('only at planner.dispatch: full');
  });

  it('shows a running charge and stops it with DELETE', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = { mode: 'auto', chargeNow: { active: true, targetPct: 90, until: '2026-10-27T05:00:00+00:00' } };
      p.config.plannerDispatch = 'full';
    });
    await openPlan();

    expect(card().querySelector('[data-ev="charge-active"]')!.textContent).toContain('Charging now to 90 %');
    await act(async () => {
      fireEvent.click(within(card()).getByRole('button', { name: 'Stop charging now' }));
    });
    await advance();

    expect(writes).toEqual([{ url: 'api/ev/charge-now', method: 'DELETE', body: undefined }]);
    expect(card().querySelector('[data-ev="message"]')!.textContent).toBe('Stopped.');
  });

  it('has no stop button when no charge is running', async () => {
    evAnswer = changed(ev, (p) => {
      p.manual = MANUAL_IDLE;
      p.config.plannerDispatch = 'full';
    });
    await openPlan();

    expect(within(card()).queryByRole('button', { name: 'Stop charging now' })).toBeNull();
    expect(card().querySelector('[data-ev="charge-inactive"]')).not.toBeNull();
  });
});

describe('the charger’s device card', () => {
  it('has a car charging row that says where the car stands, why, and links to the card of the plan', async () => {
    await openDevice('ev_charger');

    const field = screen.getByText('Car charging', { selector: 'dt' }).nextElementSibling as HTMLElement;
    expect(field.querySelector('[data-ev-device="summary"]')!.textContent).toBe('40 %; mode Automatic');
    expect(field.querySelector('[data-ev-device="reason"]')!.textContent).toContain('The next trip sets the goal');
    expect(field.querySelector('a')!.getAttribute('href')).toBe('#/plan');
  });

  it('has no such row on another device', async () => {
    await openDevice('heater_l1');

    expect(screen.queryByText('Car charging', { selector: 'dt' })).toBeNull();
  });

  it('has none without an ev: section', async () => {
    evAnswer = changed(ev, (p) => Object.assign(p, { enabled: false, config: null, parameters: null, addOnZone: null, inputs: null, trips: null, assessment: null, manual: null }));
    await openDevice('ev_charger');

    expect(screen.queryByText('Car charging', { selector: 'dt' })).toBeNull();
  });

  it('says so in Polish', async () => {
    await openDevice('ev_charger', 'pl');

    const field = screen.getByText('Ładowanie auta', { selector: 'dt' }).nextElementSibling as HTMLElement;
    expect(field.querySelector('[data-ev-device="summary"]')!.textContent).toBe('40 %; tryb Automatyczny');
  });
});

describe('the goal of the plan says why the module wants it', () => {
  it('shows the cause on a module goal in the day plan', async () => {
    const answer = changed(plan, (p) => {
      p.devices.find((d: any) => d.device === 'ev_charger').goal.cause = 'ev_trip_deadline';
      p.devices.find((d: any) => d.device === 'ev_charger').goals[0].cause = 'ev_trip_deadline';
    });
    fetchMock.mockImplementation((input) => {
      const url = String(input);
      return url === 'api/plan' ? respond(structuredClone(answer)) : respond({}, 404);
    });
    window.location.hash = '#/plan';
    render(<App lang="en" />);
    await advance();

    expect(document.querySelector('[data-goal="cause"]')!.getAttribute('data-cause')).toBe('ev_trip_deadline');
    expect(document.querySelector('[data-goal="cause"]')!.textContent).toBe(messages.en['plan.cause.ev_trip_deadline']);
  });

  it('shows none on a goal of another source', async () => {
    fetchMock.mockImplementation((input) => (String(input) === 'api/plan' ? respond(structuredClone(plan)) : respond({}, 404)));
    window.location.hash = '#/plan';
    render(<App lang="en" />);
    await advance();

    expect(document.querySelector('[data-goal="cause"]')).toBeNull();
  });
});
