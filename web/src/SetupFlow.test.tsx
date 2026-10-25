import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import type { ComponentType } from 'preact';
import { useState } from 'preact/hooks';
import { cleanup, fireEvent, render, screen } from '@testing-library/preact';
import modeSample from './api/mode.sample.json';
import savedSample from './api/setup-saved.sample.json';
import { messages } from './i18n';
import { advance, respond } from './screens.test-support';
import { SetupConnection } from './SetupConnection';
import { cleanDraft, posted, samples, serve, type Payload } from './SetupLoads.test-support';
import { SetupLoads } from './SetupLoads';
import { SetupMeter } from './SetupMeter';
import { SetupMode } from './SetupMode';
import { SetupSources } from './SetupSources';
import { fieldId, type StepName } from './setupDraft';
import { useSetup, type StepProps } from './useSetup';

// The stage's done-when criterion at the SPA seam: a clean install (no file) configured through the
// five real step components and one shared draft, ending in exactly the save body the add-on maps
// onto switchboard.yaml. A step writing a draft path or shape the add-on would refuse or map
// elsewhere breaks this test; the add-on's own side is SetupEndpointTests.Clean_install_is_saved_through_the_api.

const STEPS: Record<StepName, ComponentType<StepProps>> = {
  meter: SetupMeter,
  connection: SetupConnection,
  sources: SetupSources,
  loads: SetupLoads,
  mode: SetupMode,
};

let fetchMock: Mock<typeof fetch>;

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  window.sessionStorage.clear();
  window.localStorage.clear();
  fetchMock = vi.fn<typeof fetch>();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

/** The wizard as the shell runs it: one useSetup, the step a goTo chose. */
function Wizard({ onStep }: { onStep: (step: StepName) => void }) {
  const setup = useSetup();
  const [step, setStep] = useState<StepName>('meter');
  const Step = STEPS[step];
  onStep(step);
  return setup.load.kind === 'ready' ? (
    <Step
      setup={setup}
      t={messages.en}
      lang="en"
      goTo={(next) => {
        setStep(next);
      }}
    />
  ) : null;
}

/** The step-test fetch double, plus what a save and the mode choice answer. */
function serveCleanInstall() {
  serve(fetchMock, { draft: cleanDraft() });
  const base = fetchMock.getMockImplementation()!;
  fetchMock.mockImplementation((input, init) => {
    const path = String(input).split('?')[0];
    if (path === 'api/setup/save' && init?.method === 'POST') return respond(structuredClone(savedSample));
    if (path === 'api/mode') return respond(structuredClone(modeSample));
    return base(input, init);
  });
}

const byField = <T extends HTMLElement = HTMLInputElement>(field: string) => {
  const element = document.getElementById(fieldId(field));
  if (element === null) throw new Error(`No field ${field} on the page.`);
  return element as T;
};

const choose = (field: string, value: string) => fireEvent.change(byField<HTMLSelectElement>(field), { target: { value } });
const type = (field: string, value: string) => fireEvent.input(byField(field), { target: { value } });

async function settle(times = 4) {
  for (let i = 0; i < times; i++) await advance();
}

describe('clean install end to end', () => {
  it('configures a clean install through all five steps and saves exactly what was entered', async () => {
    serveCleanInstall();
    let step: StepName = 'meter';
    render(<Wizard onStep={(s) => (step = s)} />);
    await settle();

    // Step 1: layout, breaker, the three phase currents and powers with their max ages, signs.
    fireEvent.click(byField('connection.topology'));
    type('connection.breakerRatingA', '25');
    for (const [phase, letter] of [['L1', 'a'], ['L2', 'b'], ['L3', 'c']] as const) {
      choose(`meter.phases.${phase}.current.entity`, `sensor.solaredge_m1_ac_current_${letter}`);
      choose(`meter.phases.${phase}.power.entity`, `sensor.solaredge_m1_ac_power_${letter}`);
      await settle(1);
      type(`meter.phases.${phase}.current.maxAge`, '90s');
      type(`meter.phases.${phase}.power.maxAge`, '90s');
    }
    choose('meter.currentSign', 'unsigned');
    choose('meter.currentDirection', 'phase_power');
    choose('meter.powerSign', 'positive_export');
    fireEvent.click(screen.getByRole('button', { name: 'Check the measurement and continue' }));
    await settle();
    expect(step).toBe('connection');

    // Step 2: characteristic (never prefilled), voltage (prefilled), a reserve per phase.
    expect(byField('connection.voltageV').value).toBe('230');
    choose('connection.characteristic', 'B');
    for (const phase of ['L1', 'L2', 'L3']) type(`connection.reserveW.${phase}`, '2500');
    fireEvent.click(screen.getByRole('button', { name: 'Check and continue' }));
    await settle();
    expect(step).toBe('sources');

    // Step 3: no PV here.
    fireEvent.click(screen.getByRole('button', { name: 'Check the sources and continue' }));
    await settle();
    expect(step).toBe('loads');

    // Step 4: one water-heater element from its template card; the user declares the actuator.
    fireEvent.click(screen.getByRole('button', { name: /^Water-heater element/ }));
    await settle(1);
    choose('devices[0].actuator', 'switch.grzalka_2');
    choose('devices[0].phase', 'L1');
    type('devices[0].ratedCurrentA', '7');
    type('devices[0].ratedPowerW', '1700');
    type('devices[0].valuePerKwh', '0,35');
    choose('devices[0].stateReports', 'on_change');
    type('devices[0].name', 'Grzałka CWU');
    fireEvent.click(screen.getByRole('button', { name: 'Check the loads and continue' }));
    await settle();
    expect(step).toBe('mode');

    // Step 5: the statement must be acknowledged; shadow is the only mode offered.
    const finish = screen.getByRole('button', { name: 'Finish setup' }) as HTMLButtonElement;
    expect(finish.disabled).toBe(true);
    fireEvent.click(screen.getByRole('checkbox', { name: /I have read this/ }));
    fireEvent.click(finish);
    await settle(6);

    const [body] = posted(fetchMock, 'api/setup/save');
    expect(body).toBeDefined();
    const draft = (body as { draft: Payload; baseRevision: unknown }).draft as Record<string, Payload>;
    expect((body as { baseRevision: unknown }).baseRevision).toBeNull();
    expect(draft.connection).toEqual({ topology: 'three_phase', breakerRatingA: 25, characteristic: 'B', voltageV: 230, reserveW: { L1: 2500, L2: 2500, L3: 2500 } });
    expect(draft.meter).toMatchObject({ currentSign: 'unsigned', currentDirection: 'phase_power', powerSign: 'positive_export' });
    const phases = draft.meter!.phases as Record<string, Payload>;
    for (const [phase, letter] of [['L1', 'a'], ['L2', 'b'], ['L3', 'c']] as const) {
      expect(phases[phase]).toMatchObject({
        current: { entity: `sensor.solaredge_m1_ac_current_${letter}`, unit: 'A', maxAge: '90s' },
        power: { entity: `sensor.solaredge_m1_ac_power_${letter}`, unit: 'W', maxAge: '90s' },
      });
    }
    expect(draft.sources).toEqual({ inverter: null });
    const devices = draft.devices as unknown as Payload[];
    expect(devices).toHaveLength(1);
    expect(devices[0]).toMatchObject({
      id: 'water_heater_element',
      existing: false,
      template: 'water_heater_element',
      name: 'Grzałka CWU',
      type: 'onoff',
      actuator: 'switch.grzalka_2',
      phase: 'L1',
      ratedCurrentA: 7,
      ratedPowerW: 1700,
      valuePerKwh: 0.35,
      stateReports: 'on_change',
      draw: null,
    });
    expect(draft.acknowledgements).toEqual({ notProtective: true, commentsDropped: false, replaceUnreadable: false });
    // Only shadow is ever chosen; active is never sent.
    const modes = posted(fetchMock, 'api/mode');
    expect(modes.map((m) => m.uiChoice)).toEqual(['shadow']);
    // The saved draft is not kept for the tab any more.
    expect(window.sessionStorage.getItem('switchboard.setup.draft')).toBeNull();
    expect(samples.templatesSample.templates.map((t) => t.id)).toContain('water_heater_element');
  });
});
