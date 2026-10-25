import { useEffect, useRef, useState } from 'preact/hooks';
import { EntityPicker, matchesFilter, type PickerFilter } from './EntityPicker';
import { ReadoutLine } from './Readout';
import { SetupIssues } from './SetupIssues';
import {
  cancelSelfTest,
  DiscoveryUnavailable,
  fetchDiscovery,
  parseSelfTest,
  startSelfTest,
  type DiscoveredEntity,
  type Discovery,
  type Observation,
  type Readout,
  type SelfTest,
  type SelfTestTarget,
  type SetupDraft,
  type SetupIssue,
  type SignalDraft,
  type StepSummary,
} from './api/setup';
import { PHASES, type PhaseName } from './api/signals';
import { SETUP_REQUEST_TIMEOUT_MS, SETUP_SELF_TEST_REFRESH_INTERVAL_MS, SETUP_SELF_TEST_REQUEST_TIMEOUT_MS } from './config';
import { formatCountdown, formatInterval, formatNumber, formatPercent } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { fieldId, getIn, issuesOfStep } from './setupDraft';
import { PollStatus, StateBadge, StateLabel, Timestamp, type Tone } from './ui';
import type { PollView } from './usePoll';
import { useReadouts } from './useReadouts';
import type { SetupState, StepProps } from './useSetup';
import './setupMeter.css';

// Step 1 of screen 03 (docs/mockups "Licznik główny"): the one step that cannot be simplified. The
// layout and breaker rating come first (they decide which rows are required and what is plausible),
// then the explicit per-phase mapping with the live value, age and detected refresh beside every
// pick, the sign convention and direction, the optional powers, voltages and totals, the add-on's
// verdict (CONN-06), and the passive phase assignment test (CONN-05), which is never hidden.

type Quantity = 'current' | 'power' | 'voltage';

/**
 * What each meter row accepts (UI-03): a sensor of the quantity's device class, or one reporting a
 * unit the add-on's signal layer reads for it (SignalUnits.AllowedFor; a YAML Modbus sensor often
 * has a unit and no device class). The same units are the row's unit choices.
 */
const FILTERS: Record<Quantity, PickerFilter & { units: readonly string[] }> = {
  current: { domains: ['sensor'], deviceClasses: ['current'], units: ['A', 'mA'] },
  power: { domains: ['sensor'], deviceClasses: ['power'], units: ['W', 'kW'] },
  voltage: { domains: ['sensor'], deviceClasses: ['voltage'], units: ['V'] },
};

/** The layouts of switchboard.yaml's connection.topology, in the mockup's order. */
export const TOPOLOGIES = ['three_phase', 'three_phase_summed', 'single_phase'] as const;
const CURRENT_SIGNS = ['positive_import', 'positive_export', 'unsigned'] as const;
const POWER_SIGNS = ['positive_import', 'positive_export'] as const;
/** Home Assistant's entity ID shape (domain.object_id). */
const ENTITY_ID = /^[a-z0-9_]+\.[a-z0-9_]+$/;

/** The phases a layout measures (TopologyPhases.For): single-phase is L1 alone. */
export function topologyPhases(topology: string | null): readonly PhaseName[] {
  return topology === 'single_phase' ? ['L1'] : PHASES;
}

/**
 * The tokens of an entity ID that name a phase: sensor.solaredge_m1_ac_current_a, …_voltage_an,
 * shellypro3em_phase_a_current. A naming heuristic for a suggestion the user confirms, nothing more.
 */
const PHASE_TOKENS: Record<PhaseName, readonly string[]> = { L1: ['l1', 'a', 'an'], L2: ['l2', 'b', 'bn'], L3: ['l3', 'c', 'cn'] };

/** The one phase an entity ID names by a separate token; null when it names none, 'several' when more than one. */
export function phaseFromName(entityId: string): PhaseName | null | 'several' {
  const tokens = (entityId.split('.')[1] ?? '').toLowerCase().split('_');
  const named = PHASES.filter((phase) => PHASE_TOKENS[phase].some((token) => tokens.includes(token)));
  return named.length === 0 ? null : named.length === 1 ? named[0]! : 'several';
}

export interface Suggestion {
  path: string;
  quantity: Quantity;
  entity: DiscoveredEntity;
}

/**
 * The mapping a meter device's entity names suggest: per quantity, the single primary sensor naming
 * each phase, and the single one naming none as the total. Ambiguous names suggest nothing, derived
 * sensors are never suggested (07-PITFALLS), and nothing is written until the user applies it.
 */
export function suggestMapping(entities: readonly DiscoveredEntity[], phases: readonly PhaseName[]): Suggestion[] {
  const suggestions: Suggestion[] = [];
  for (const quantity of ['current', 'power', 'voltage'] as const) {
    const candidates = entities.filter((e) => e.derived === null && matchesFilter(e, FILTERS[quantity]));
    for (const phase of phases) {
      const hits = candidates.filter((e) => phaseFromName(e.entityId) === phase);
      if (hits.length === 1) suggestions.push({ path: `meter.phases.${phase}.${quantity}`, quantity, entity: hits[0]! });
    }
    if (quantity !== 'voltage') {
      const totals = candidates.filter((e) => phaseFromName(e.entityId) === null);
      if (totals.length === 1) suggestions.push({ path: `meter.total.${quantity}`, quantity, entity: totals[0]! });
    }
  }
  return suggestions;
}

function signalAt(draft: SetupDraft, path: string): SignalDraft | null {
  return (getIn(draft, path) ?? null) as SignalDraft | null;
}

function mapped(draft: SetupDraft, path: string): boolean {
  return typeof signalAt(draft, path)?.entity === 'string';
}

/** The unit Home Assistant reports for an entity, when it is one this quantity accepts. */
function unitOf(entity: DiscoveredEntity | undefined, quantity: Quantity, current: string | null): string | null {
  const units = FILTERS[quantity].units;
  if (entity?.unit !== null && entity?.unit !== undefined && units.includes(entity.unit)) return entity.unit;
  // A quantity with a single unit (V) has nothing to choose.
  return current ?? (units.length === 1 ? units[0]! : null);
}

/** A number typed into a field; empty or not a number clears it. */
export function parseNumber(text: string): number | null {
  const value = Number(text.replace(',', '.'));
  return text.trim() === '' || !Number.isFinite(value) ? null : value;
}

/** The label of a step-1 field, for the sentence that says what to fix (UI-05). */
export function fieldLabel(field: string, t: Messages): string {
  if (field === 'connection.topology') return t['setupMeter.topology.title'];
  if (field === 'connection.breakerRatingA') return t['setupMeter.breaker.label'];
  if (field === 'meter.currentSign') return t['setupMeter.sign.label'];
  if (field === 'meter.currentDirection') return t['setupMeter.direction.label'];
  if (field === 'meter.powerSign') return t['setupMeter.powerSign.label'];
  if (field === 'meter.total') return t['setupMeter.total.title'];
  const match = /^meter\.(?:phases\.(L[123])\.(current|power|voltage)|total\.(current|power))(?:\.(entity|unit|maxAge))?$/.exec(field);
  if (match === null) return field;
  const base =
    match[1] !== undefined
      ? fill(t[`setupMeter.label.${match[2] as Quantity}`], { phase: match[1] })
      : t[match[3] === 'current' ? 'setupMeter.label.totalCurrent' : 'setupMeter.label.totalPower'];
  if (match[4] === 'unit') return `${base} · ${t['setupMeter.label.unit']}`;
  if (match[4] === 'maxAge') return `${base} · ${t['setupMeter.label.maxAge']}`;
  return base;
}

/** The element of a field, or of the nearest enclosing field that has one (a row, a section). */
function elementOf(field: string): HTMLElement | null {
  let path = field;
  for (;;) {
    const element = document.getElementById(fieldId(path));
    if (element !== null) return element;
    const cut = path.lastIndexOf('.');
    if (cut < 0) return null;
    path = path.slice(0, cut);
  }
}

/** Focuses the input of a field, or the nearest enclosing field that has one. */
export function focusField(field: string): void {
  elementOf(field)?.focus();
}

/** Of the refused fields, the one the user meets first on the page (the add-on lists them in document-mapping order). */
export function firstOnPage(fields: readonly string[]): string | undefined {
  const placed = fields.map((field) => ({ field, element: elementOf(field) }));
  return placed.reduce<{ field: string; element: HTMLElement | null } | undefined>((first, next) => {
    if (first === undefined || first.element === null) return next.element !== null || first === undefined ? next : first;
    if (next.element === null) return first;
    return first.element.compareDocumentPosition(next.element) & Node.DOCUMENT_POSITION_PRECEDING ? next : first;
  }, undefined)?.field;
}

// ---------------------------------------------------------------------------------------------
// Discovery: what Home Assistant offers, read once per visit (the add-on caches it).

type DiscoveryView = { kind: 'loading' } | { kind: 'ready'; data: Discovery } | { kind: 'unavailable'; code: string | null };

function useDiscovery(): [DiscoveryView, () => void] {
  const [view, setView] = useState<DiscoveryView>({ kind: 'loading' });
  const [request, setRequest] = useState({ generation: 0, refresh: false });
  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), SETUP_REQUEST_TIMEOUT_MS);
    setView({ kind: 'loading' });
    fetchDiscovery(controller.signal, request.refresh)
      .then((data) => {
        if (!disposed) setView({ kind: 'ready', data });
      })
      .catch((error: unknown) => {
        if (!disposed) setView({ kind: 'unavailable', code: error instanceof DiscoveryUnavailable ? error.code : null });
      })
      .finally(() => clearTimeout(timeout));
    return () => {
      disposed = true;
      controller.abort();
    };
  }, [request]);
  return [view, () => setRequest((r) => ({ generation: r.generation + 1, refresh: true }))];
}

// ---------------------------------------------------------------------------------------------
// The passive phase self-test session (CONN-05). The add-on holds the watch for its whole timeout,
// whether or not this page polls; the session id is kept in the browser so a visit to Home
// Assistant to switch the device (which unloads this page) comes back to the same watch.

const SELF_TEST_STORAGE_KEY = 'switchboard.setup.selfTest';

function storedSession(): string | null {
  try {
    return window.localStorage.getItem(SELF_TEST_STORAGE_KEY);
  } catch {
    return null;
  }
}

function storeSession(id: string | null): void {
  try {
    if (id === null) window.localStorage.removeItem(SELF_TEST_STORAGE_KEY);
    else window.localStorage.setItem(SELF_TEST_STORAGE_KEY, id);
  } catch {
    // The watch still shows for this visit.
  }
}

/** One session, or null when the add-on no longer has it (404: restarted, or replaced). */
async function readSession(id: string, signal: AbortSignal): Promise<SelfTest | null> {
  const response = await fetch(`api/setup/self-test/${encodeURIComponent(id)}`, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`The self-test failed with HTTP ${response.status}.`);
  return parseSelfTest(await response.json());
}

type SessionView = { kind: 'none' } | { kind: 'lost' } | PollView<SelfTest>;

interface SelfTestSession {
  view: SessionView;
  follow: (id: string) => void;
  /** Reads the session again at once (after a cancel). */
  refresh: () => void;
}

function useSelfTestSession(): SelfTestSession {
  const [id, setId] = useState<string | null>(storedSession);
  const [generation, setGeneration] = useState(0);
  const [view, setView] = useState<SessionView>(id === null ? { kind: 'none' } : { kind: 'loading' });
  useEffect(() => {
    if (id === null) {
      setView({ kind: 'none' });
      return;
    }
    let disposed = false;
    let next: ReturnType<typeof setTimeout> | undefined;
    let inFlight: AbortController | undefined;
    let lastSuccessAt: Date | null = null;
    const poll = async () => {
      const controller = new AbortController();
      inFlight = controller;
      const timeout = setTimeout(() => controller.abort(), SETUP_SELF_TEST_REQUEST_TIMEOUT_MS);
      let again = true;
      try {
        const session = await readSession(id, controller.signal);
        if (disposed) return;
        if (session === null) {
          storeSession(null);
          setView({ kind: 'lost' });
          again = false;
        } else {
          lastSuccessAt = new Date();
          setView({ kind: 'live', data: session, updatedAt: lastSuccessAt });
          again = session.status === 'watching';
        }
      } catch {
        // A failed poll withdraws the session view (a frozen "watching" would read as a calm one); the next poll retries.
        if (disposed) return;
        setView({ kind: 'unavailable', lastSuccessAt });
      } finally {
        clearTimeout(timeout);
      }
      if (again) next = setTimeout(poll, SETUP_SELF_TEST_REFRESH_INTERVAL_MS);
    };
    void poll();
    return () => {
      disposed = true;
      clearTimeout(next);
      inFlight?.abort();
    };
  }, [id, generation]);
  return {
    view,
    follow: (next) => {
      storeSession(next);
      setView({ kind: 'loading' });
      setId(next);
      setGeneration((g) => g + 1);
    },
    refresh: () => setGeneration((g) => g + 1),
  };
}

/** The self-test target the panel has chosen: a declared device of the draft, or a test load. */
interface TestLoad {
  stateEntity: string | null;
  phase: string;
  ratedPowerW: number | null;
}

/** The declared devices the passive test can judge: on/off on one phase, with an entity reporting its state. */
function testableDevices(draft: SetupDraft): { index: number; name: string; phase: string; entity: string }[] {
  return draft.devices.flatMap((d, index) => {
    const entity = d.stateEntity ?? d.actuator;
    return d.type === 'onoff' && d.phase !== null && (PHASES as readonly string[]).includes(d.phase) && entity !== null
      ? [{ index, name: d.name ?? d.id ?? entity, phase: d.phase, entity }]
      : [];
  });
}

// ---------------------------------------------------------------------------------------------
// The step.

interface RowSpec {
  path: string;
  quantity: Quantity;
  label: string;
  tag: string;
  need: 'required' | 'optional' | 'outside' | null;
}

type CheckView = { kind: 'idle' } | { kind: 'refused'; field: string } | { kind: 'failed' };

/**
 * Step 1 (Meter): the layout and breaker rating, per-phase entity mapping with live readouts, sign
 * convention, direction, refresh, the add-on's checks, and the phase self-test, prominent (UI-03,
 * UI-04, CONN-01, CONN-05, CONN-06, SIG-07). Only the add-on judges: "Check the measurement and
 * continue" validates the draft and moves on only when no step-1 field is refused.
 */
export function SetupMeter({ setup, t, lang, goTo }: StepProps) {
  const draft = setup.draft;
  const topology = draft.connection.topology;
  const layout = topologyPhases(topology);
  const [discovery, rereadDiscovery] = useDiscovery();
  const [meterDevice, setMeterDevice] = useState('');
  const [check, setCheck] = useState<CheckView>({ kind: 'idle' });
  const [target, setTarget] = useState<string | null>(null);
  const [testLoad, setTestLoad] = useState<TestLoad>({ stateEntity: null, phase: '', ratedPowerW: null });
  const session = useSelfTestSession();
  const panel = useRef<HTMLHeadingElement>(null);

  const all = discovery.kind === 'ready' ? discovery.data.entities : discovery.kind === 'unavailable' ? null : [];
  const shown = all !== null && meterDevice !== '' ? all.filter((e) => e.deviceId === meterDevice) : all;

  const devices = testableDevices(draft);
  const chosen = target ?? (devices.length > 0 ? `device:${devices[0]!.index}` : 'testLoad');
  const chosenEntity = chosen === 'testLoad' ? testLoad.stateEntity : (devices.find((d) => `device:${d.index}` === chosen)?.entity ?? null);
  const watched = session.view.kind === 'live' ? session.view.data.expected.stateEntity : null;

  const rows = meterRows(draft, t);
  const meterEntities = [...rows.currents, ...rows.powers, ...rows.voltages, ...rows.totals]
    .map((r) => signalAt(draft, r.path)?.entity)
    .filter((e): e is string => typeof e === 'string');
  // Only well-formed entity IDs are asked for: one half-typed ID would make the add-on refuse the whole request.
  const readouts = useReadouts([...meterEntities, chosenEntity, watched].filter((e): e is string => e !== null && ENTITY_ID.test(e)));
  const readoutOf = (entity: string | null | undefined): Readout | undefined =>
    readouts.kind === 'live' && typeof entity === 'string' ? readouts.data.items.find((i) => i.entityId === entity) : undefined;
  const readoutsAt = readouts.kind === 'live' ? readouts.data.at : null;

  const run = async () => {
    setCheck({ kind: 'idle' });
    const result = await setup.validate();
    if (result === null) {
      setCheck({ kind: 'failed' });
      return;
    }
    const refused = firstOnPage(
      issuesOfStep(result.issues, 'meter')
        .filter((issue) => issue.severity === 'error' && issue.field !== null)
        .map((issue) => issue.field!),
    );
    if (refused !== undefined) {
      setCheck({ kind: 'refused', field: refused });
      focusField(refused);
      return;
    }
    goTo('connection');
  };

  const rowProps = { setup, all, entities: shown, readoutOf, t, lang };
  const currentSign = draft.meter.currentSign;
  const powerEntities = layout.map((p) => signalAt(draft, `meter.phases.${p}.power`)?.entity).filter((e): e is string => typeof e === 'string');

  return (
    <>
      <section class="card setup-step sm-scope" aria-labelledby="setup-meter-title" data-step="meter">
        <header class="cardhead">
          <h2 id="setup-meter-title">{t['setupMeter.title']}</h2>
          <p class="sub">{t['setupMeter.stepLine']}</p>
        </header>
        <p>{t['setupMeter.lead']}</p>

        <div class="sm-section" data-part="layout">
          <h3 id="setup-meter-topology">{t['setupMeter.topology.title']}</h3>
          <p class="sub">{t['setupMeter.topology.lead']}</p>
          <fieldset class="sm-choices" aria-labelledby="setup-meter-topology">
            {TOPOLOGIES.map((choice, i) => (
              <label class="sm-choice" key={choice}>
                <input
                  type="radio"
                  name="setup-topology"
                  value={choice}
                  id={i === 0 ? fieldId('connection.topology') : undefined}
                  checked={topology === choice}
                  onChange={() => setup.set('connection.topology', choice)}
                />
                <span>
                  <span class="sm-choice-t">{t[`setupMeter.topology.${choice}`]}</span>
                  <span class="sub">{t[`setupMeter.topology.${choice}.d`]}</span>
                </span>
              </label>
            ))}
          </fieldset>
          {topology !== null && !(TOPOLOGIES as readonly string[]).includes(topology) && (
            <p class="note">
              <StateLabel tone="warn" label={fill(t['setupMeter.topology.other'], { value: topology })} />
            </p>
          )}
          <div class="sm-fields">
            <div class="picker">
              <label for={fieldId('connection.breakerRatingA')}>{t['setupMeter.breaker.label']}</label>
              <input
                id={fieldId('connection.breakerRatingA')}
                type="number"
                inputMode="decimal"
                min="0"
                step="any"
                value={draft.connection.breakerRatingA ?? ''}
                onInput={(event) => setup.set('connection.breakerRatingA', parseNumber((event.currentTarget as HTMLInputElement).value))}
              />
              <span class="sub">{t['setupMeter.breaker.hint']}</span>
            </div>
          </div>
        </div>

        <div class="sm-section" data-part="device">
          <DiscoveryNotes discovery={discovery} reread={rereadDiscovery} t={t} />
          {discovery.kind === 'ready' && (
            <MeterDevice
              discovery={discovery.data}
              chosen={meterDevice}
              onChoose={setMeterDevice}
              layout={layout}
              setup={setup}
              t={t}
            />
          )}
        </div>

        <div class="sm-section" data-part="currents">
          <h3>{t['setupMeter.currents.title']}</h3>
          <p class="sub">{t['setupMeter.currents.hint']}</p>
          <div class="sm-rows">
            {rows.currents.map((spec) => (
              <SignalRow key={spec.path} spec={spec} {...rowProps} />
            ))}
          </div>
          <div class="sm-fields">
            <div class="picker">
              <label for={fieldId('meter.currentSign')}>{t['setupMeter.sign.label']}</label>
              <select
                id={fieldId('meter.currentSign')}
                value={currentSign ?? ''}
                onChange={(event) => {
                  const value = (event.currentTarget as HTMLSelectElement).value;
                  setup.set('meter.currentSign', value === '' ? null : value);
                  // A direction belongs to an unsigned current only.
                  if (value !== 'unsigned') setup.set('meter.currentDirection', null);
                }}
              >
                <option value="">{t['setupMeter.sign.choose']}</option>
                {CURRENT_SIGNS.map((sign) => (
                  <option key={sign} value={sign}>
                    {t[`setupMeter.sign.${sign}`]}
                  </option>
                ))}
                {currentSign !== null && !(CURRENT_SIGNS as readonly string[]).includes(currentSign) && <option value={currentSign}>{currentSign}</option>}
              </select>
            </div>
            <div class="picker">
              <label for={fieldId('meter.currentDirection')}>{t['setupMeter.direction.label']}</label>
              {currentSign === 'unsigned' ? (
                <>
                  <select
                    id={fieldId('meter.currentDirection')}
                    value={draft.meter.currentDirection ?? ''}
                    onChange={(event) => {
                      const value = (event.currentTarget as HTMLSelectElement).value;
                      setup.set('meter.currentDirection', value === '' ? null : value);
                    }}
                  >
                    <option value="">{t['setupMeter.sign.choose']}</option>
                    <option value="phase_power">
                      {powerEntities.length > 0
                        ? fill(t['setupMeter.direction.phase_power'], { entities: powerEntities.join(', ') })
                        : t['setupMeter.direction.phase_powerUnmapped']}
                    </option>
                    <option value="none">{t['setupMeter.direction.none']}</option>
                  </select>
                  <span class="sub">{t['setupMeter.direction.hint']}</span>
                </>
              ) : (
                <span class="sub" id={fieldId('meter.currentDirection')} tabIndex={-1}>
                  {t['setupMeter.direction.notNeeded']}
                </span>
              )}
            </div>
            <RefreshSummary rows={topology === 'three_phase_summed' ? rows.totals.filter((r) => r.quantity === 'current') : rows.currents} draft={draft} readoutOf={readoutOf} t={t} lang={lang} />
          </div>
        </div>

        <div class="sm-section" data-part="powers">
          <h3>{t['setupMeter.powers.title']}</h3>
          <p class="sub">{t['setupMeter.powers.hint']}</p>
          <div class="sm-rows">
            {rows.powers.map((spec) => (
              <SignalRow key={spec.path} spec={spec} {...rowProps} />
            ))}
          </div>
          <div class="sm-fields">
            <div class="picker">
              <label for={fieldId('meter.powerSign')}>{t['setupMeter.powerSign.label']}</label>
              <select
                id={fieldId('meter.powerSign')}
                value={draft.meter.powerSign ?? ''}
                onChange={(event) => {
                  const value = (event.currentTarget as HTMLSelectElement).value;
                  setup.set('meter.powerSign', value === '' ? null : value);
                }}
              >
                <option value="">{t['setupMeter.sign.choose']}</option>
                {POWER_SIGNS.map((sign) => (
                  <option key={sign} value={sign}>
                    {t[`setupMeter.sign.${sign}`]}
                  </option>
                ))}
                {draft.meter.powerSign !== null && !(POWER_SIGNS as readonly string[]).includes(draft.meter.powerSign) && (
                  <option value={draft.meter.powerSign}>{draft.meter.powerSign}</option>
                )}
              </select>
            </div>
          </div>
        </div>

        <div class="sm-section" data-part="voltages">
          <h3>{t['setupMeter.voltages.title']}</h3>
          <p class="sub">{t['setupMeter.voltages.hint']}</p>
          <div class="sm-rows">
            {rows.voltages.map((spec) => (
              <SignalRow key={spec.path} spec={spec} {...rowProps} />
            ))}
          </div>
        </div>

        <div class="sm-section" data-part="total" id={fieldId('meter.total')} tabIndex={-1}>
          <h3>{t['setupMeter.total.title']}</h3>
          <p class="sub">{t['setupMeter.total.hint']}</p>
          <div class="sm-rows">
            {rows.totals.map((spec) => (
              <SignalRow key={spec.path} spec={spec} {...rowProps} />
            ))}
          </div>
        </div>

        <p class="note">
          <strong>{t['setupMeter.checks.title']}</strong> {t['setupMeter.checks.body']}
        </p>
        <p class="note">
          <strong>{t['setupMeter.net.title']}</strong> {t['setupMeter.net.body']}
        </p>

        <CheckResult setup={setup} t={t} lang={lang} />
        <SetupIssues issues={setup.issuesOf('meter')} t={t} />
        {check.kind === 'refused' && (
          <p class="note" role="alert" data-refused={check.field}>
            <StateLabel tone="crit" label={fill(t['setupMeter.check.refused'], { field: fieldLabel(check.field, t) })} />
          </p>
        )}
        {check.kind === 'failed' && (
          <p class="note" role="alert">
            <StateLabel tone="crit" label={t['setupMeter.check.failed']} />
          </p>
        )}
        <div class="sm-actions">
          <button type="button" class="log-button" disabled={setup.busy !== 'idle'} onClick={() => void run()}>
            {setup.busy === 'validating' ? t['setupMeter.check.running'] : t['setupMeter.check.run']}
          </button>
          <button
            type="button"
            class="log-button"
            onClick={() => {
              panel.current?.scrollIntoView?.({ block: 'start' });
              panel.current?.focus();
            }}
          >
            {t['setupMeter.check.selfTest']}
          </button>
        </div>
      </section>

      <SelfTestPanel
        setup={setup}
        t={t}
        lang={lang}
        entities={all}
        devices={devices}
        chosen={chosen}
        onChoose={setTarget}
        testLoad={testLoad}
        onTestLoad={(change) => setTestLoad((load) => ({ ...load, ...change }))}
        session={session}
        readoutOf={readoutOf}
        readoutsAt={readoutsAt}
        headingRef={panel}
      />
    </>
  );
}

/** The rows the layout needs, plus any row the draft maps outside it (kept until removed). */
function meterRows(draft: SetupDraft, t: Messages): { currents: RowSpec[]; powers: RowSpec[]; voltages: RowSpec[]; totals: RowSpec[] } {
  const topology = draft.connection.topology;
  const known = (TOPOLOGIES as readonly string[]).includes(topology ?? '');
  const layout = topologyPhases(topology);
  const phaseRows = (quantity: Quantity, inLayout: 'required' | 'optional') =>
    PHASES.filter((p) => layout.includes(p) || mapped(draft, `meter.phases.${p}.${quantity}`)).map(
      (p): RowSpec => ({
        path: `meter.phases.${p}.${quantity}`,
        quantity,
        label: fill(t[`setupMeter.label.${quantity}`], { phase: p }),
        tag: p,
        need: !layout.includes(p) ? 'outside' : known ? inLayout : null,
      }),
    );
  const summed = topology === 'three_phase_summed';
  return {
    currents: phaseRows('current', summed ? 'optional' : 'required'),
    powers: phaseRows('power', 'optional'),
    voltages: phaseRows('voltage', 'optional'),
    totals: [
      { path: 'meter.total.current', quantity: 'current', label: t['setupMeter.label.totalCurrent'], tag: 'Σ A', need: summed ? 'required' : known ? 'optional' : null },
      { path: 'meter.total.power', quantity: 'power', label: t['setupMeter.label.totalPower'], tag: 'Σ W', need: known ? 'optional' : null },
    ],
  };
}

function DiscoveryNotes({ discovery, reread, t }: { discovery: DiscoveryView; reread: () => void; t: Messages }) {
  if (discovery.kind === 'loading') return <p class="updated">{t['setupMeter.discovery.loading']}</p>;
  if (discovery.kind === 'unavailable') {
    return (
      <p class="note" role="status">
        <StateLabel tone="warn" label={discovery.code === 'discovery_too_large' ? t['setupMeter.discovery.tooLarge'] : t['setupMeter.discovery.unavailable']} />{' '}
        <button type="button" class="linkbutton" onClick={reread}>
          {t['setupMeter.discovery.refresh']}
        </button>
      </p>
    );
  }
  const data = discovery.data;
  return (
    <>
      {!data.registryAvailable && (
        <p class="note">
          <StateLabel tone="warn" label={t['setupMeter.discovery.registry']} />
        </p>
      )}
      {data.truncated && (
        <p class="note">
          <StateLabel tone="warn" label={t['setupMeter.discovery.truncated']} />
        </p>
      )}
    </>
  );
}

/** The meter device picker (mockup "Urządzenie pomiarowe na przyłączu") and the mapping its entity names suggest. */
function MeterDevice({
  discovery,
  chosen,
  onChoose,
  layout,
  setup,
  t,
}: {
  discovery: Discovery;
  chosen: string;
  onChoose: (id: string) => void;
  layout: readonly PhaseName[];
  setup: SetupState;
  t: Messages;
}) {
  const candidates = discovery.devices
    .map((device) => ({
      device,
      count: discovery.entities.filter((e) => e.deviceId === device.id && e.derived === null && (matchesFilter(e, FILTERS.current) || matchesFilter(e, FILTERS.power))).length,
    }))
    .filter((c) => c.count > 0);
  const suggestions = chosen === '' ? [] : suggestMapping(discovery.entities.filter((e) => e.deviceId === chosen), layout);
  const apply = () => {
    for (const s of suggestions) {
      const current = signalAt(setup.draft, s.path);
      if (typeof current?.entity === 'string') continue;
      setup.set(s.path, { entity: s.entity.entityId, unit: unitOf(s.entity, s.quantity, current?.unit ?? null), maxAge: current?.maxAge ?? null });
    }
  };
  return (
    <>
      <div class="picker">
        <label for="setup-meter-device">{t['setupMeter.device.label']}</label>
        <select id="setup-meter-device" value={chosen} onChange={(event) => onChoose((event.currentTarget as HTMLSelectElement).value)}>
          <option value="">{t['setupMeter.device.all']}</option>
          {candidates.map(({ device, count }) => (
            <option key={device.id} value={device.id}>
              {fill(t['setupMeter.device.option'], { name: device.name ?? device.id, count: String(count) })}
            </option>
          ))}
        </select>
        <span class="sub">{fill(t['setupMeter.device.hint'], { count: String(candidates.length) })}</span>
      </div>
      {chosen !== '' && (
        <div class="note" data-part="suggestion">
          {suggestions.length === 0 ? (
            <span>{t['setupMeter.suggest.none']}</span>
          ) : (
            <>
              <strong>{t['setupMeter.suggest.title']}</strong>
              <span class="sub">{t['setupMeter.suggest.lead']}</span>
              <ul class="plainlist">
                {suggestions.map((s) => (
                  <li key={s.path}>
                    {fieldLabel(s.path, t)}: <span class="mono">{s.entity.entityId}</span>
                  </li>
                ))}
              </ul>
              <button type="button" class="log-button" onClick={apply}>
                {t['setupMeter.suggest.apply']}
              </button>
            </>
          )}
        </div>
      )}
    </>
  );
}

interface RowProps {
  spec: RowSpec;
  setup: SetupState;
  /** Every discovered entity (units are looked up here); null when Home Assistant cannot be reached. */
  all: readonly DiscoveredEntity[] | null;
  /** What the picker offers: every entity, or the chosen meter device's. */
  entities: readonly DiscoveredEntity[] | null;
  readoutOf: (entity: string | null | undefined) => Readout | undefined;
  t: Messages;
  lang: Lang;
}

/**
 * One mapped signal (mockup columns Faza / Encja / Jednostka / Odczyt / Wiek, plus max_age): the
 * picker, its unit, the live readout with age and refresh (UI-04, SIG-07), and the declared max age
 * with the add-on's verdict on it against the detected refresh (CONN-06).
 */
function SignalRow({ spec, setup, all, entities, readoutOf, t, lang }: RowProps) {
  const signal = signalAt(setup.draft, spec.path);
  const entity = signal?.entity ?? null;
  const units = FILTERS[spec.quantity].units;
  const unit = signal?.unit ?? null;
  const invalid = setup.issuesAt(spec.path).some((i) => i.severity === 'error');
  const refresh = setup.validation?.checks.refresh.find((r) => r.field === spec.path);
  // One key per edit: the reducer applies it to the latest draft, so quick edits never undo each other.
  const write = (next: Partial<SignalDraft>) => {
    if (signal === null) setup.set(spec.path, { entity: null, unit: null, maxAge: null, ...next });
    else for (const [key, value] of Object.entries(next)) setup.set(`${spec.path}.${key}`, value);
  };
  const suggested = refresh?.suggestedMaxAgeS == null ? null : `${Math.ceil(refresh.suggestedMaxAgeS)}s`;
  const refreshTone: Record<string, Tone> = { ok: 'ok', below_refresh: 'crit', above_refresh: 'warn', not_detected: 'warn' };
  return (
    <div class="sm-row" id={fieldId(spec.path)} tabIndex={-1} data-invalid={invalid ? 'true' : undefined} data-path={spec.path}>
      <div class="sm-tag">
        <span class="mono">{spec.tag}</span>
        {spec.need === 'required' && <span class="sub">{t['setupMeter.row.required']}</span>}
        {spec.need === 'optional' && <span class="sub">{t['setupMeter.row.optional']}</span>}
      </div>
      <div class="sm-entity">
        <EntityPicker
          id={fieldId(`${spec.path}.entity`)}
          label={spec.label}
          value={entity}
          onChange={(value) => {
            const found = value === null ? undefined : all?.find((e) => e.entityId === value);
            write({ entity: value, unit: unitOf(found, spec.quantity, unit) });
          }}
          entities={entities}
          filter={FILTERS[spec.quantity]}
          t={t}
        />
      </div>
      <div class="picker">
        <label for={fieldId(`${spec.path}.unit`)}>{t['setupMeter.label.unit']}</label>
        <select
          id={fieldId(`${spec.path}.unit`)}
          aria-label={`${spec.label} · ${t['setupMeter.label.unit']}`}
          value={unit ?? ''}
          onChange={(event) => {
            const value = (event.currentTarget as HTMLSelectElement).value;
            write({ unit: value === '' ? null : value });
          }}
        >
          <option value="">{t['setupMeter.unit.choose']}</option>
          {units.map((u) => (
            <option key={u} value={u}>
              {u}
            </option>
          ))}
          {unit !== null && !units.includes(unit) && <option value={unit}>{unit}</option>}
        </select>
      </div>
      <div class="sm-readout">
        <span class="sub">{t['setupMeter.label.readout']}</span>
        {entity !== null ? <ReadoutLine readout={readoutOf(entity)} t={t} lang={lang} /> : <span class="sub-inline">{t['setup.readout.none']}</span>}
      </div>
      <div class="picker">
        <label for={fieldId(`${spec.path}.maxAge`)}>{t['setupMeter.label.maxAge']}</label>
        <input
          id={fieldId(`${spec.path}.maxAge`)}
          aria-label={`${spec.label} · ${t['setupMeter.label.maxAge']}`}
          class="mono"
          type="text"
          spellcheck={false}
          autocomplete="off"
          placeholder={t['setupMeter.maxAge.hint']}
          value={signal?.maxAge ?? ''}
          onInput={(event) => {
            const text = (event.currentTarget as HTMLInputElement).value.trim();
            write({ maxAge: text === '' ? null : text });
          }}
        />
        {refresh !== undefined && (
          <span class="sub" data-refresh={refresh.state}>
            <StateLabel tone={refreshTone[refresh.state] ?? 'muted'} label={t[`setupMeter.refreshCheck.${refresh.state}`]} />
          </span>
        )}
        {suggested !== null && signal?.maxAge !== suggested && (
          <button type="button" class="linkbutton" onClick={() => write({ maxAge: suggested })}>
            {fill(t['setupMeter.maxAge.suggest'], { value: suggested })}
          </button>
        )}
      </div>
      {spec.need === 'outside' && (
        <p class="sub sm-outside">
          {t['setupMeter.row.outside']}{' '}
          <button type="button" class="linkbutton" onClick={() => setup.set(spec.path, null)}>
            {t['setupMeter.row.clear']}
          </button>
        </p>
      )}
    </div>
  );
}

/**
 * The detected refresh of the protection inputs (mockup "Odświeżanie", SIG-07): the slowest phase
 * counts, since the minimum safe reserve in step 2 is derived from it.
 */
function RefreshSummary({ rows, draft, readoutOf, t, lang }: { rows: RowSpec[]; draft: SetupDraft; readoutOf: RowProps['readoutOf']; t: Messages; lang: Lang }) {
  const entities = rows.filter((r) => r.need !== 'outside').map((r) => signalAt(draft, r.path)?.entity ?? null);
  const readouts = entities.map((e) => readoutOf(e));
  let text: string;
  if (entities.length === 0 || entities.some((e) => e === null)) text = t['setupMeter.refresh.none'];
  else if (readouts.every((r) => r?.refresh.intervalS != null)) {
    text = fill(t['setupMeter.refresh.detected'], { interval: formatInterval(Math.max(...readouts.map((r) => r!.refresh.intervalS!)), lang) });
  } else {
    const slowest = readouts.reduce<Readout | undefined>((low, r) => (r === undefined || (low !== undefined && low.refresh.samples <= r.refresh.samples) ? low : r), undefined);
    text =
      slowest === undefined
        ? t['setup.readout.pending']
        : fill(t['setupMeter.refresh.detecting'], { samples: String(slowest.refresh.samples), needed: String(slowest.refresh.needed) });
  }
  return (
    <div class="picker" data-part="refresh">
      <span>{t['setupMeter.refresh.label']}</span>
      <span class="mono">{text}</span>
      <span class="sub">{t['setupMeter.refresh.hint']}</span>
    </div>
  );
}

/** The add-on's last verdict on the measurement: the phase sum (skipped is never passed). */
function CheckResult({ setup, t, lang }: { setup: SetupState; t: Messages; lang: Lang }) {
  const validation = setup.validation;
  if (validation === null) return null;
  const sum = validation.checks.phaseSum;
  const number = (value: number | null) => (value === null ? t['common.none'] : formatNumber(value, lang, 1));
  let line: { tone: Tone; text: string };
  if (sum.state === 'passed') {
    line = { tone: 'ok', text: fill(t['setupMeter.phaseSum.passed'], { residual: number(sum.residual), tolerance: number(sum.tolerance), unit: sum.unit ?? '', samples: String(sum.samples) }) };
  } else if (sum.state === 'failed') {
    line = { tone: 'crit', text: fill(t['setupMeter.phaseSum.failed'], { residual: number(sum.residual), tolerance: number(sum.tolerance), unit: sum.unit ?? '' }) };
  } else {
    line = { tone: 'warn', text: t['setupMeter.phaseSum.skipped'] };
  }
  const reasonKey = `setupMeter.phaseSum.reason.${sum.reason ?? ''}`;
  return (
    <div class="sm-section" data-part="result">
      <h3>{t['setupMeter.result.title']}</h3>
      <p data-phase-sum={sum.state}>
        <StateLabel tone={line.tone} label={line.text} />
        {sum.state === 'skipped' && reasonKey in t && <span class="sub">{t[reasonKey as keyof Messages]}</span>}
      </p>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// The phase assignment test panel.

const VERDICT_TONE: Record<StepSummary['verdict'], Tone> = { confirmed: 'ok', mismatch: 'crit', inconclusive: 'warn' };

function VerdictText({ verdict, observed, expected, reason, t }: { verdict: StepSummary['verdict']; observed: PhaseName | null; expected: string; reason: StepSummary['reason']; t: Messages }) {
  const text =
    verdict === 'confirmed'
      ? fill(t['setupMeter.outcome.confirmed'], { observed: observed ?? expected })
      : verdict === 'mismatch'
        ? fill(t['setupMeter.outcome.mismatch'], { observed: observed ?? '—', expected })
        : fill(t['setupMeter.outcome.inconclusive'], { reason: t[`setupMeter.reason.${reason ?? 'timed_out'}`] });
  return (
    <span class="sm-verdict" data-verdict={verdict}>
      <StateBadge tone={VERDICT_TONE[verdict]} label={t[`setupMeter.verdict.${verdict}`]} /> {text}
    </span>
  );
}

function ObservationItem({ observation, expected, t, lang }: { observation: Observation; expected: string; t: Messages; lang: Lang }) {
  const digits = observation.unit === 'W' ? 0 : 2;
  const deltas = PHASES.filter((p) => observation.deltas[p] !== undefined)
    .map((p) => {
      const value = observation.deltas[p]!;
      return `${p} ${value > 0 ? '+' : ''}${formatNumber(value, lang, digits)} ${observation.unit}`;
    })
    .join(' · ');
  return (
    <li data-source={observation.source}>
      <Timestamp at={observation.at} lang={lang} />
      <span>{t[`setupMeter.obs.${observation.transition}`]}</span>
      <span class="mono sm-deltas">{deltas}</span>
      <VerdictText verdict={observation.verdict} observed={observation.observedPhase} expected={expected} reason={observation.reason} t={t} />
      {observation.ratingRatio !== null && <span class="sub-inline">{fill(t['setupMeter.obs.ratio'], { ratio: formatPercent(observation.ratingRatio, lang) })}</span>}
      <span class="sub-inline">{t[`setupMeter.obs.${observation.source}`]}</span>
      {observation.houseLoad && <span class="sub-inline">{t['setupMeter.obs.houseLoad']}</span>}
      {observation.note !== null && <span class="sub-inline">{observation.note}</span>}
    </li>
  );
}

interface PanelProps {
  setup: SetupState;
  t: Messages;
  lang: Lang;
  entities: readonly DiscoveredEntity[] | null;
  devices: ReturnType<typeof testableDevices>;
  chosen: string;
  onChoose: (target: string) => void;
  testLoad: TestLoad;
  /** Changes part of the test load; applied to the latest one, so quick edits never undo each other. */
  onTestLoad: (change: Partial<TestLoad>) => void;
  session: SelfTestSession;
  readoutOf: RowProps['readoutOf'];
  readoutsAt: Date | null;
  headingRef: { current: HTMLHeadingElement | null };
}

/**
 * The passive phase assignment test (CONN-05, binding orchestrator decision): Switchboard switches
 * nothing. The user switches a declared device (or another known on/off load) in Home Assistant or
 * by hand while the add-on watches which meter phase rises, and the verdict names the phase that
 * rose against the declared one. Inconclusive is shown as inconclusive, never as a pass.
 */
function SelfTestPanel({ setup, t, lang, entities, devices, chosen, onChoose, testLoad, onTestLoad, session, readoutOf, readoutsAt, headingRef }: PanelProps) {
  const [starting, setStarting] = useState(false);
  const [refusal, setRefusal] = useState<SetupIssue[]>([]);
  const [notice, setNotice] = useState<'busy' | 'failed' | null>(null);
  const phases = topologyPhases(setup.draft.connection.topology);
  const stateDomains = [...new Set(setup.templates.filter((tpl) => tpl.type === 'onoff').flatMap((tpl) => tpl.stateDomains))];
  const live = session.view.kind === 'live' ? session.view.data : null;
  const watching = live?.status === 'watching';

  const begin = async () => {
    setStarting(true);
    setRefusal([]);
    setNotice(null);
    const target: SelfTestTarget = chosen.startsWith('device:')
      ? { device: { index: Number(chosen.slice('device:'.length)) } }
      : // A missing part goes as null: the add-on judges the request and names the field to fill.
        ({ testLoad: { stateEntity: testLoad.stateEntity, phase: testLoad.phase === '' ? null : testLoad.phase, ratedPowerW: testLoad.ratedPowerW } } as unknown as SelfTestTarget);
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), SETUP_REQUEST_TIMEOUT_MS);
    try {
      const result = await startSelfTest(setup.draft, target, controller.signal);
      if (result.kind === 'refused') setRefusal(result.issues);
      else {
        if (result.kind === 'busy') setNotice('busy');
        session.follow(result.id);
      }
    } catch {
      setNotice('failed');
    } finally {
      clearTimeout(timeout);
      setStarting(false);
    }
  };

  const stop = async () => {
    if (live === null) return;
    try {
      await cancelSelfTest(live.id);
    } catch {
      setNotice('failed');
    }
    session.refresh();
  };

  return (
    <section class="card setup-step sm-scope sm-selftest" aria-labelledby="setup-meter-selftest" data-part="self-test">
      <header class="cardhead">
        <h2 id="setup-meter-selftest" ref={headingRef} tabIndex={-1}>
          {t['setupMeter.selfTest.title']}
        </h2>
      </header>
      <p>{t['setupMeter.selfTest.lead']}</p>
      <p class="note" data-part="passive">
        <strong>{t['setupMeter.selfTest.passive']}</strong>
      </p>

      <div class="sm-fields">
        <div class="picker">
          <label for="setup-selftest-target">{t['setupMeter.selfTest.target']}</label>
          <select id="setup-selftest-target" value={chosen} disabled={watching} onChange={(event) => onChoose((event.currentTarget as HTMLSelectElement).value)}>
            {devices.map((d) => (
              <option key={d.index} value={`device:${d.index}`}>
                {fill(t['setupMeter.selfTest.device'], { name: d.name, phase: d.phase })}
              </option>
            ))}
            <option value="testLoad">{t['setupMeter.selfTest.testLoad']}</option>
          </select>
        </div>
      </div>
      {chosen === 'testLoad' && (
        <div class="sm-fields" data-part="test-load">
          <EntityPicker
            id={fieldId('testLoad.stateEntity')}
            label={t['setupMeter.selfTest.loadEntity']}
            value={testLoad.stateEntity}
            onChange={(value) => onTestLoad({ stateEntity: value })}
            entities={entities}
            filter={{ domains: stateDomains }}
            t={t}
          />
          <div class="picker">
            <label for={fieldId('testLoad.phase')}>{t['setupMeter.selfTest.loadPhase']}</label>
            <select id={fieldId('testLoad.phase')} value={testLoad.phase} onChange={(event) => onTestLoad({ phase: (event.currentTarget as HTMLSelectElement).value })}>
              <option value="">{t['setupMeter.selfTest.choosePhase']}</option>
              {phases.map((p) => (
                <option key={p} value={p}>
                  {p}
                </option>
              ))}
            </select>
          </div>
          <div class="picker">
            <label for={fieldId('testLoad.ratedPowerW')}>{t['setupMeter.selfTest.loadPower']}</label>
            <input
              id={fieldId('testLoad.ratedPowerW')}
              type="number"
              inputMode="decimal"
              min="0"
              step="any"
              value={testLoad.ratedPowerW ?? ''}
              onInput={(event) => onTestLoad({ ratedPowerW: parseNumber((event.currentTarget as HTMLInputElement).value) })}
            />
            <span class="sub">{t['setupMeter.selfTest.loadHint']}</span>
          </div>
        </div>
      )}

      {refusal.length > 0 && (
        <div data-part="refusal">
          <p class="sub">{t['setupMeter.selfTest.refused']}</p>
          <SetupIssues issues={refusal} t={t} />
        </div>
      )}
      {notice === 'busy' && <p class="note">{t['setupMeter.selfTest.busy']}</p>}
      {notice === 'failed' && (
        <p class="note" role="alert">
          <StateLabel tone="crit" label={t['setupMeter.selfTest.failed']} />
        </p>
      )}

      <div class="sm-actions">
        {watching ? (
          <button type="button" class="log-button" onClick={() => void stop()}>
            {t['setupMeter.selfTest.cancel']}
          </button>
        ) : (
          <button type="button" class="log-button" disabled={starting} onClick={() => void begin()}>
            {starting ? t['setupMeter.selfTest.starting'] : t['setupMeter.selfTest.start']}
          </button>
        )}
      </div>

      <SessionDetails view={session.view} readoutOf={readoutOf} readoutsAt={readoutsAt} t={t} lang={lang} />
      <Evidence setup={setup} t={t} />
    </section>
  );
}

function SessionDetails({ view, readoutOf, readoutsAt, t, lang }: { view: SessionView; readoutOf: RowProps['readoutOf']; readoutsAt: Date | null; t: Messages; lang: Lang }) {
  if (view.kind === 'none') return null;
  if (view.kind === 'lost') {
    return (
      <p class="note" role="status">
        <StateLabel tone="warn" label={t['setupMeter.selfTest.lost']} />
      </p>
    );
  }
  if (view.kind !== 'live') return <PollStatus view={view} lang={lang} t={t} />;
  const session = view.data;
  const expected = session.expected;
  const rating = `${formatNumber(expected.rating, lang, expected.unit === 'W' ? 0 : 2)} ${expected.unit}`;
  const statusTone: Record<SelfTest['status'], Tone> = { watching: 'warn', finished: 'ok', expired: 'warn', cancelled: 'muted' };
  const remainingS = readoutsAt === null ? null : (session.expiresAt.getTime() - readoutsAt.getTime()) / 1000;
  return (
    <div class="sm-section" data-part="session" data-status={session.status}>
      <p>
        <StateBadge tone={statusTone[session.status]} label={t[`setupMeter.selfTest.status.${session.status}`]} />{' '}
        <span>{fill(t['setupMeter.selfTest.expected'], { entity: expected.stateEntity, phase: expected.phase, rating })}</span>
      </p>
      {session.status === 'watching' && (
        <p role="status">
          <strong>{fill(t['setupMeter.selfTest.watching'], { entity: expected.stateEntity, phase: expected.phase })}</strong>{' '}
          {remainingS !== null && <span class="sub-inline">{fill(t['setupMeter.selfTest.remaining'], { time: formatCountdown(remainingS) })}</span>}
          <span class="sub">
            {t['setupMeter.selfTest.live']} <span class="mono">{expected.stateEntity}</span> <ReadoutLine readout={readoutOf(expected.stateEntity)} t={t} lang={lang} />
          </span>
        </p>
      )}
      {session.observations.length === 0 ? (
        <p class="sub">{t['setupMeter.obs.none']}</p>
      ) : (
        <ul class="sm-observations">
          {session.observations.map((o, i) => (
            <ObservationItem key={i} observation={o} expected={expected.phase} t={t} lang={lang} />
          ))}
        </ul>
      )}
      {session.status !== 'watching' && (session.observations.length > 0 || session.status === 'expired') && (
        <p data-part="verdict">
          <VerdictText
            verdict={session.verdict.verdict}
            observed={session.verdict.observedPhase}
            expected={expected.phase}
            reason={session.verdict.reason ?? (session.status === 'expired' ? 'timed_out' : null)}
            t={t}
          />
        </p>
      )}
    </div>
  );
}

/** The add-on's recorded and watched switchings of the declared devices, judged against this draft's meter mapping and phases. */
function Evidence({ setup, t }: { setup: SetupState; t: Messages }) {
  const devices = setup.draft.devices;
  if (devices.length === 0) return null;
  const results = setup.validation?.selfTest ?? null;
  return (
    <div class="sm-section" data-part="evidence">
      <h3>{t['setupMeter.evidence.title']}</h3>
      {results === null ? (
        <p class="sub">{t['setupMeter.evidence.pending']}</p>
      ) : (
        <ul class="plainlist">
          {results.map((r) => {
            const index = Number(/^devices\[(\d+)\]/.exec(r.field)?.[1] ?? -1);
            const device = devices[index];
            const declared = device?.phase ?? '—';
            const none = r.confirmed + r.mismatched + r.inconclusive === 0;
            return (
              <li key={r.field} data-field={r.field}>
                {none ? (
                  // Nothing recorded is not a verdict: no badge, and never a pass.
                  <span class="sub-inline">{t['setupMeter.evidence.none']}</span>
                ) : (
                  <VerdictText verdict={r.verdict} observed={r.observedPhase} expected={declared} reason={r.reason} t={t} />
                )}{' '}
                <span class="sub">
                  {fill(t['setupMeter.evidence.row'], {
                    device: device?.name ?? r.device ?? r.stateEntity,
                    phase: declared,
                    confirmed: String(r.confirmed),
                    mismatched: String(r.mismatched),
                    inconclusive: String(r.inconclusive),
                  })}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
