import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/preact';
import { App } from './App';
import devices from './api/devices.sample.json';
import dispatch from './api/dispatch.sample.json';
import plan from './api/plan.sample.json';
import power from './api/power.sample.json';
import { advance, respond } from './screens.test-support';

// UI-12 and PLAN-19: the goal row of the device card. It shows what the plan reports for the device (goal, progress, deadline, status, the latest
// grid start, urgency, shortfall, the next planned runs), says "no goal" for an opportunist and "not available" when api/plan does not answer, and
// offers "want now" and its cancel. The only write is POST and DELETE api/goals: the add-on's own state, never an entity. A refusal is worded,
// and nothing is assumed to have worked until the add-on answers.
let fetchMock: Mock<typeof fetch>;
let posted: unknown[];
let deleted: string[];
let planAnswer: unknown;
let goalAnswer: () => Promise<Response>;

const stored = { device: 'ev_charger', kind: 'energy', target: 8, deadline: '2026-10-27T05:30:00Z', hard: true, since: '2026-10-26T17:02:00Z', deliveredKwh: 0, created: true };

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  vi.setSystemTime(new Date('2026-10-26T17:02:00Z'));
  posted = [];
  deleted = [];
  planAnswer = plan;
  goalAnswer = () => respond(stored);
  fetchMock = vi.fn<typeof fetch>();
  fetchMock.mockImplementation((input, init) => {
    const url = String(input).split('?')[0]!;
    if (url === 'api/goals' && init?.method === 'POST') {
      posted.push(JSON.parse(String(init.body)));
      return goalAnswer();
    }
    if (url.startsWith('api/goals/') && init?.method === 'DELETE') {
      deleted.push(url.slice('api/goals/'.length));
      return goalAnswer();
    }
    const samples: Record<string, unknown> = { 'api/devices': devices, 'api/dispatch': dispatch, 'api/power': power, 'api/plan': planAnswer };
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

async function open(id: string, lang: 'en' | 'pl' = 'en') {
  window.location.hash = `#/devices/${id}`;
  render(<App lang={lang} />);
  await advance();
  await advance();
  await advance();
}

function goalField(): HTMLElement {
  const section = screen.getByRole('heading', { name: /Energy value and goal|Wartość energii i cel/ }).closest('section') as HTMLElement;
  const value = within(section).getByText(/^(Goal|Cel)$/, { selector: 'dt' }).nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error('No goal field.');
  return value;
}

const planCalls = () => fetchMock.mock.calls.filter(([input]) => String(input) === 'api/plan').length;

/** A plan where heater_l2 has a goal of its own source, for the cancel button. */
const withManualHeater = () =>
  changed(plan, (p) => {
    const goal = { ...structuredClone(p.devices[0].goal), source: 'manual', hard: false };
    p.devices.push({ device: 'heater_l2', goal, goals: [goal], entries: [], shortfallKwh: 0, predictedCompletion: null });
  });

describe('device card: the goal row', () => {
  it('shows the goal the plan reports: what, when, how far, status, plan verdict, and where it came from', async () => {
    await open('ev_charger');
    const goal = goalField();

    expect(goal.querySelector('[data-goal="what"]')!.textContent).toMatch(/^reach 90 % state of charge by .*\(finish 30 min before\)$/);
    expect(goal.querySelector('[data-goal="pills"]')!.textContent).toBe('on trackhard deadlinefrom a mapped entity');
    expect(goal.querySelector('[data-goal="progress"]')!.textContent).toMatch(/^State of charge 40 %; 9\.90 kWh still to draw \(losses included\)$/);
    expect(goal.querySelector('[data-goal="plan"]')!.textContent).toMatch(/^9\.90 kWh planned · Latest start on grid power: /);
    expect(goal.querySelector('[data-goal="sources"]')!.textContent).toContain('sources: surplus and these tariff zones:');
    expect(goal.querySelector('[data-goal="sources"] .mono')!.textContent).toBe('offpeak');
    expect(goal.querySelector('[data-goal="urgent"]')).toBeNull();
  });

  it('lists the next planned runs, and what the precedence merge set aside', async () => {
    await open('ev_charger');
    const goal = goalField();

    expect(goal.querySelector('[data-goal-row="next"]')!.textContent).toMatch(/^Next planned: .*from PV surplus.*kWh, up to/);
    expect(goal.querySelector('[data-goal-row="superseded"]')!.textContent).toBe('Not applied: a goal from the file (90 %) for the same deadline is set aside by the goal above.');
  });

  it('says a preview while planner.dispatch is off: the goal changes the plan, not the device', async () => {
    await open('ev_charger');

    expect(goalField().querySelector('[data-goal-row="preview"]')!.textContent).toBe('planner.dispatch is off: a goal changes the plan shown here, not what the device does.');
  });

  it('says the same in Polish', async () => {
    await open('ev_charger', 'pl');
    const goal = goalField();

    expect(goal.querySelector('[data-goal="what"]')!.textContent).toContain('osiągnąć stan naładowania 90 %');
    expect(goal.querySelector('[data-goal="pills"]')!.textContent).toBe('zgodnie z planemtwardy terminz przypisanej encji');
    expect(goal.querySelector('[data-goal-row="preview"]')!.textContent).toContain('planner.dispatch jest wyłączone (off)');
    expect(within(goal).getByRole('form', { name: 'Chcę teraz' })).toBeTruthy();
  });

  it('shows an urgent goal with its shortfall and the latest grid start', async () => {
    planAnswer = changed(plan, (p) => {
      for (const goal of [p.devices[0].goal, p.devices[0].goals[0]]) Object.assign(goal, { status: 'at_risk', urgent: true, shortfallKwh: 2.5 });
    });
    await open('ev_charger');
    const goal = goalField();

    expect(goal.querySelector('[data-goal="pills"]')!.textContent).toContain('at risk');
    expect(goal.querySelector('[data-goal="urgent"]')!.textContent).toBe('Urgent: the deadline needs the grid now');
    expect(goal.querySelector('[data-goal="plan"]')!.textContent).toContain('Short by 2.50 kWh: the plan cannot place it');
  });

  it('says why a goal is inactive instead of showing a made-up progress', async () => {
    await open('heater_l1');
    const goal = goalField();

    expect(goal.querySelector('[data-goal-status="inactive"]')).toBeTruthy();
    expect(goal.querySelector('[data-goal="inactive"]')!.textContent).toBe('The want switch is off: no goal now.');
    expect(goal.querySelector('[data-goal="progress"]')).toBeNull();
    expect(goal.querySelector('[data-goal="what"]')).toBeNull();
  });

  it('says there is no goal for an opportunist, and offers want now with energy only', async () => {
    await open('heater_l3');
    const goal = goalField();

    expect(goal.querySelector('[data-goal-row="none"]')!.textContent).toMatch(/^No goal: this device is an opportunist/);
    const kind = within(goal).getByRole('combobox') as HTMLSelectElement;
    expect([...kind.options].map((o) => o.value)).toEqual(['energy']);
  });

  it('offers a state-of-charge goal only for a device whose goal reads a state of charge', async () => {
    await open('ev_charger');

    const kind = within(goalField()).getByRole('combobox') as HTMLSelectElement;
    expect([...kind.options].map((o) => o.value)).toEqual(['energy', 'soc']);
  });

  it('says the goal is not available while api/plan does not answer, and offers nothing to do', async () => {
    planAnswer = { not: 'a plan' };
    await open('ev_charger');

    expect(goalField().textContent).toBe('The goal is not available: the add-on does not answer.');
    expect(within(goalField()).queryByRole('form')).toBeNull();
  });

  it('says no planner is configured and offers no form: a goal there would feed nothing', async () => {
    planAnswer = changed(plan, (p) =>
      Object.assign(p, { enabled: false, dispatchMode: null, status: 'absent', id: null, computedAt: null, validUntil: null, horizonEnd: null, slotMinutes: null, configRevision: null, cost: null, lastSolve: null, slots: [], devices: p.devices.map((d: any) => ({ ...d, entries: [] })) }),
    );
    await open('ev_charger');
    const goal = goalField();

    expect(goal.querySelector('[data-goal-row="not-enabled"]')!.textContent).toBe('No planner is configured, so this goal feeds nothing.');
    expect(within(goal).queryByRole('form')).toBeNull();
    expect(goal.querySelector('[data-goal-row="preview"]')).toBeNull();
  });

  it('says the plan is not fresh and shows no next runs from it', async () => {
    planAnswer = changed(plan, (p) => (p.status = 'stale'));
    await open('ev_charger');
    const goal = goalField();

    expect(goal.querySelector('[data-goal-row="not-fresh"]')!.textContent).toBe("The plan is not fresh: only the goal model's own status is shown.");
    expect(goal.querySelector('[data-goal-row="next"]')).toBeNull();
  });
});

describe('device card: want now', () => {
  async function fill(target: string, deadline: string, hard = true) {
    const form = within(goalField()).getByRole('form');
    fireEvent.input(within(form).getByLabelText('Target'), { target: { value: target } });
    fireEvent.input(within(form).getByLabelText('Deadline'), { target: { value: deadline } });
    const check = within(form).getByRole('checkbox') as HTMLInputElement;
    if (check.checked !== hard) fireEvent.click(check);
    await act(async () => {
      fireEvent.submit(form);
      await vi.advanceTimersByTimeAsync(0);
    });
  }

  it('posts exactly the five keys, then reads the plan again at once and says the goal is set', async () => {
    await open('ev_charger');
    const before = planCalls();

    await fill('8', '2026-10-27T06:30');

    expect(posted).toEqual([{ device: 'ev_charger', kind: 'energy', target: 8, deadline: new Date(2026, 9, 27, 6, 30).toISOString(), hard: true }]);
    expect(goalField().querySelector('[role="status"]')!.textContent).toBe('Goal set. The plan is recomputed shortly.');
    expect(planCalls()).toBe(before + 1);
  });

  it('takes a decimal comma and a soft goal', async () => {
    await open('ev_charger');

    await fill('7,5', '2026-10-27T06:30', false);

    expect(posted).toEqual([expect.objectContaining({ target: 7.5, hard: false })]);
  });

  it('asks for a state of charge when the device reads one', async () => {
    await open('ev_charger');
    const form = within(goalField()).getByRole('form');
    fireEvent.change(within(form).getByRole('combobox'), { target: { value: 'soc' } });

    await fill('80', '2026-10-27T06:30');

    expect(posted).toEqual([expect.objectContaining({ kind: 'soc', target: 80 })]);
  });

  it.each([
    ['0', '2026-10-27T06:30', 'Enter a target above zero.'],
    ['abc', '2026-10-27T06:30', 'Enter a target above zero.'],
    ['8', '', 'Choose a deadline in the future.'],
    ['8', '2026-10-26T10:00', 'Choose a deadline in the future.'],
  ])('refuses target %s and deadline "%s" in the page, without calling the add-on', async (target, deadline, message) => {
    await open('ev_charger');

    await fill(target, deadline);

    expect(goalField().querySelector('[role="status"]')!.textContent).toBe(message);
    expect(posted).toEqual([]);
  });

  it.each([
    ['deadline_beyond_horizon', 'The deadline is beyond the planning horizon.'],
    ['soc_not_configured', "A state-of-charge goal needs this device's goal: in switchboard.yaml to read a state of charge; ask for energy instead."],
    ['unknown_device', 'This is not a declared device.'],
    ['no_configuration', 'There is no valid configuration; fix switchboard.yaml first.'],
  ])("words the add-on's refusal %s", async (code, text) => {
    goalAnswer = () => respond({ code, message: 'x' }, 422);
    await open('ev_charger');
    const before = planCalls();

    await fill('8', '2026-10-27T06:30');

    expect(goalField().querySelector('[role="status"]')!.textContent).toBe(text);
    // A refused goal changed nothing, so the plan is not read again for it.
    expect(planCalls()).toBe(before);
  });

  it('says it could not send the request when the add-on does not answer', async () => {
    goalAnswer = () => Promise.reject(new Error('offline'));
    await open('ev_charger');

    await fill('8', '2026-10-27T06:30');

    expect(goalField().querySelector('[role="status"]')!.textContent).toBe('The request could not be sent.');
  });

  it('says what it stores: the add-on own state, expiring at the deadline, no entity', async () => {
    await open('ev_charger');

    expect(goalField().textContent).toContain("Kept in the add-on's own state and dropped at its deadline. It never writes to any entity.");
  });

  it('offers to cancel only a manual goal, and sends a DELETE for the device', async () => {
    planAnswer = withManualHeater();
    await open('heater_l2');
    const goal = goalField();
    const cancel = within(goal).getByRole('button', { name: 'Cancel the manual goal' });

    await act(async () => {
      fireEvent.click(cancel);
      await vi.advanceTimersByTimeAsync(0);
    });

    expect(deleted).toEqual(['heater_l2']);
    expect(goalField().querySelector('[role="status"]')!.textContent).toBe('Manual goal cancelled.');
  });

  it('has no cancel for a goal that is not manual', async () => {
    await open('ev_charger');

    expect(within(goalField()).queryByRole('button', { name: 'Cancel the manual goal' })).toBeNull();
  });

  it('reads only the add-on api until the person acts, and never writes', async () => {
    await open('ev_charger');

    expect(fetchMock.mock.calls.every(([, init]) => init?.method === undefined || init.method === 'GET')).toBe(true);
    expect([...new Set(fetchMock.mock.calls.map(([input]) => String(input)))].sort()).toEqual(['api/devices', 'api/dispatch', 'api/economics', 'api/mode', 'api/plan', 'api/power']);
  });
});
