import type { ComponentChildren } from 'preact';
import { useEffect, useRef, useState } from 'preact/hooks';
import {
  cancelActiveTest,
  cancelSelfTest,
  fetchActiveTest,
  fetchActiveTestPreconditions,
  fetchDiscovery,
  fetchEvidence,
  fetchSelfTest,
  startActiveTest,
  startSelfTest,
  type ActiveTest,
  type ActiveTestPreconditions,
  type ActiveTestRefusal,
  type DeviceDraft,
  type DeviceEvidence,
  type DiscoveredEntity,
  type Discovery,
  type Observation,
  type Readout,
  type SelfTest,
  type SetupIssue,
  type StepSummary,
  type Template,
} from './api/setup';
import { PHASES } from './api/signals';
import { SETUP_REQUEST_TIMEOUT_MS, SETUP_SELF_TEST_REFRESH_INTERVAL_MS, SETUP_SELF_TEST_REQUEST_TIMEOUT_MS } from './config';
import { EntityPicker, matchesFilter, type PickerFilter } from './EntityPicker';
import { formatAge, formatInterval, formatNumber, formatQuantity } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { localClock } from './localTime';
import { ReadoutLine } from './Readout';
import { SetupIssues, issueText } from './SetupIssues';
import { fieldId, newDevice, stepOfField, type StepName } from './setupDraft';
import { StateBadge, Timestamp, type Tone } from './ui';
import { usePoll, type PollView } from './usePoll';
import { useReadouts } from './useReadouts';
import type { SetupState, StepProps } from './useSetup';
import './setupLoads.css';

// Step 4 (Loads, UI-02, CONN-02, CONN-05) and the parts step 3 shares with it: the device form,
// discovery for the pickers, the live readouts and the "check and continue" action. Only the user
// declares an actuator or a stop actuator (CLAUDE.md): template cards prefill type, limits and
// lockouts from the add-on's templates, pickers offer what fits the field and never preselect. The
// phase self-test has two forms: passive (the user switches the device while the wizard watches)
// and automatic (on demand, Switchboard switches an idle declared relay on and off through the
// effector gate, only in effective active mode and below protection). What the running add-on
// recorded, from either, is judged again against this draft by the add-on itself.

// ---------------------------------------------------------------------------------------------
// Discovery (UI-03): one read per step visit; the add-on caches it and refreshes on request.

export type DiscoveryState = { kind: 'loading' } | { kind: 'ready'; discovery: Discovery } | { kind: 'unavailable' };

export function useDiscovery(): { state: DiscoveryState; refresh: () => void } {
  const [state, setState] = useState<DiscoveryState>({ kind: 'loading' });
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), SETUP_REQUEST_TIMEOUT_MS);
    setState({ kind: 'loading' });
    fetchDiscovery(controller.signal, generation > 0)
      .then((discovery) => {
        if (!disposed) setState({ kind: 'ready', discovery });
      })
      .catch(() => {
        if (!disposed) setState({ kind: 'unavailable' });
      })
      .finally(() => clearTimeout(timeout));
    return () => {
      disposed = true;
      clearTimeout(timeout);
      controller.abort();
    };
  }, [generation]);
  return { state, refresh: () => setGeneration((g) => g + 1) };
}

/** The pickers' entities: null while Home Assistant cannot be reached (manual entry only). */
export function discoveredEntities(state: DiscoveryState): DiscoveredEntity[] | null {
  return state.kind === 'ready' ? state.discovery.entities : null;
}

/** Whether discovery is still loading, with what the user should know about its result. */
export function DiscoveryNotice({ state, refresh, t }: { state: DiscoveryState; refresh: () => void; t: Messages }) {
  if (state.kind === 'loading') return <p class="updated">{t['setupLoads.discovery.loading']}</p>;
  const notes: string[] = [];
  if (state.kind === 'unavailable') notes.push(t['setupLoads.discovery.unavailable']);
  else {
    if (state.discovery.truncated) notes.push(t['setupLoads.discovery.truncated']);
    if (!state.discovery.registryAvailable) notes.push(t['setupLoads.discovery.registry']);
  }
  return (
    <div class="loads-discovery">
      {notes.map((note) => (
        <p key={note} class="note" role="status">
          {note}
        </p>
      ))}
      <button type="button" class="linkbutton" onClick={refresh}>
        {t['setupLoads.discovery.refresh']}
      </button>
    </div>
  );
}

/** Readouts by entity, for the entities a step shows. */
export function useReadoutMap(entities: readonly (string | null | undefined)[]): Map<string, Readout> {
  const view = useReadouts(entities.filter((e): e is string => typeof e === 'string' && e !== ''));
  return new Map(view.kind === 'live' ? view.data.items.map((r) => [r.entityId, r] as const) : []);
}

// Signal units the add-on converts (the signal layer: A/mA, W/kW); a sensor with a device class or
// one of these units fits the field.
export const CURRENT_UNITS = ['A', 'mA'] as const;
export const POWER_UNITS = ['W', 'kW'] as const;
export const CURRENT_SENSOR: PickerFilter = { domains: ['sensor'], deviceClasses: ['current'], units: CURRENT_UNITS };
export const POWER_SENSOR: PickerFilter = { domains: ['sensor'], deviceClasses: ['power'], units: POWER_UNITS };

// ---------------------------------------------------------------------------------------------
// Small form parts.

/** A number typed as text (a comma is a decimal separator too); empty clears the field. */
function parseNumber(text: string): number | null | undefined {
  const trimmed = text.trim().replace(',', '.');
  if (trimmed === '') return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : undefined;
}

/**
 * A numeric field that keeps what the user is typing ("0," on the way to "0,35") and commits the
 * parsed number to the draft; text that is not a number clears the value and says so.
 */
export function NumberInput({ id, value, onChange, invalid, t }: { id: string; value: number | null; onChange: (value: number | null) => void; invalid: boolean; t: Messages }) {
  const [text, setText] = useState(value === null ? '' : String(value));
  useEffect(() => {
    const parsed = parseNumber(text);
    if (parsed !== value && !(parsed === undefined && value === null)) setText(value === null ? '' : String(value));
    // Only an outside change of the value resyncs the text.
  }, [value]);
  const notANumber = parseNumber(text) === undefined;
  return (
    <>
      <input
        id={id}
        type="text"
        inputMode="decimal"
        class="loads-num"
        autocomplete="off"
        value={text}
        aria-invalid={invalid || notANumber ? true : undefined}
        onInput={(event) => {
          const raw = (event.currentTarget as HTMLInputElement).value;
          setText(raw);
          const parsed = parseNumber(raw);
          onChange(parsed === undefined ? null : parsed);
        }}
      />
      {notANumber && <span class="loads-field-issue">{t['setupLoads.number.invalid']}</span>}
    </>
  );
}

/** Free text (a name, a duration, a group id); empty clears the field so the default applies. */
export function TextInput({
  id,
  value,
  onChange,
  invalid,
  mono = false,
  placeholder,
  list,
  trim = false,
}: {
  id: string;
  value: string | null;
  onChange: (value: string | null) => void;
  invalid: boolean;
  mono?: boolean;
  placeholder?: string;
  list?: string;
  trim?: boolean;
}) {
  return (
    <input
      id={id}
      type="text"
      class={mono ? 'mono' : undefined}
      autocomplete="off"
      spellcheck={false}
      value={value ?? ''}
      placeholder={placeholder}
      list={list}
      aria-invalid={invalid ? true : undefined}
      onInput={(event) => {
        const raw = (event.currentTarget as HTMLInputElement).value;
        const text = trim ? raw.trim() : raw;
        onChange(text === '' ? null : text);
      }}
    />
  );
}

/** The errors the add-on reported at exactly this field, under it. */
export function FieldErrors({ issues, field, t }: { issues: readonly SetupIssue[]; field: string; t: Messages }) {
  const own = issues.filter((i) => i.field === field && i.severity === 'error');
  if (own.length === 0) return null;
  return (
    <ul class="loads-field-issues">
      {own.map((issue, n) => (
        <li key={n} class="loads-field-issue">
          {issueText(issue, t)}
        </li>
      ))}
    </ul>
  );
}

/** One labelled field with its hint and the errors at it. */
export function Field({ label, htmlFor, hint, field, setup, t, children }: { label: string; htmlFor: string; hint?: string; field: string; setup: SetupState; t: Messages; children: ComponentChildren }) {
  return (
    <div class="loads-field">
      <label for={htmlFor}>{label}</label>
      {children}
      {hint !== undefined && <span class="sub">{hint}</span>}
      <FieldErrors issues={setup.issues} field={field} t={t} />
    </div>
  );
}

export const hasError = (setup: SetupState, field: string) => setup.issues.some((i) => i.field === field && i.severity === 'error');

/** The add-on's verdict on a signal's refresh against its max age (SIG-07), with its suggestion. */
export function RefreshCheck({ setup, field, onSuggest, t, lang }: { setup: SetupState; field: string; onSuggest: (maxAge: string) => void; t: Messages; lang: Lang }) {
  const check = setup.validation?.checks.refresh.find((c) => c.field === field);
  if (check === undefined) return null;
  const maxAge = check.maxAgeS === null ? '—' : formatInterval(check.maxAgeS, lang);
  const detected = check.detectedS === null ? '—' : formatInterval(check.detectedS, lang);
  const suggested = check.suggestedMaxAgeS;
  return (
    <span class="sub loads-refresh" data-state={check.state}>
      {check.state === 'not_detected' ? t['setupLoads.refresh.not_detected'] : fill(t[`setupLoads.refresh.${check.state}`], { detected, maxAge })}
      {suggested !== null && suggested !== check.maxAgeS && (
        <>
          {' '}
          <button type="button" class="linkbutton" onClick={() => onSuggest(`${suggested}s`)}>
            {fill(t['setupLoads.refresh.suggest'], { suggested: `${suggested}s` })}
          </button>
        </>
      )}
    </span>
  );
}

// ---------------------------------------------------------------------------------------------
// Templates and device specifics.

const TEMPLATE_IDS = ['ev_charger', 'water_heater_element', 'heat_pump', 'generic_onoff'] as const;
type KnownTemplate = (typeof TEMPLATE_IDS)[number];
const isKnownTemplate = (id: string): id is KnownTemplate => (TEMPLATE_IDS as readonly string[]).includes(id);

const DEVICE_TYPES = ['onoff', 'modulating', 'storage', 'stepped'] as const;
type KnownType = (typeof DEVICE_TYPES)[number];
const isKnownType = (type: string): type is KnownType => (DEVICE_TYPES as readonly string[]).includes(type);

export function typeLabel(type: string | null, t: Messages): string {
  if (type === null) return '—';
  return isKnownType(type) ? t[`setupLoads.type.${type}`] : type;
}

/** What a device's pickers accept and what it inherits, from the add-on's templates. */
export interface DeviceSpec {
  actuatorDomains: readonly string[];
  stateDomains: readonly string[];
  /** Null when no template describes the type (storage): the shipped default applies, unnamed. */
  inherited: Template['inherited'] | null;
  drawRecommended: boolean;
}

/**
 * The template a device was made from, else the first one of its type (domains and inherited
 * values depend on the type only). Storage has no template; the device parser gives it the
 * setpoint domains of a modulating device, so those are offered, with nothing inherited named.
 */
export function deviceSpec(device: DeviceDraft, templates: readonly Template[]): DeviceSpec {
  const own = templates.find((tpl) => tpl.id === device.template) ?? templates.find((tpl) => tpl.type === device.type);
  if (own !== undefined) return { actuatorDomains: own.actuatorDomains, stateDomains: own.stateDomains, inherited: own.inherited, drawRecommended: own.drawRecommended };
  const setpoint = device.type === 'storage' ? templates.find((tpl) => tpl.type === 'modulating') : undefined;
  return { actuatorDomains: setpoint?.actuatorDomains ?? [], stateDomains: setpoint?.stateDomains ?? [], inherited: null, drawRecommended: false };
}

/**
 * A device id not used yet: the template's id, then _2, _3 … Ids of the loaded file count as taken
 * even when the device was removed in this edit: the add-on refuses a new device that reuses one,
 * because it would inherit the removed entry's keys.
 */
export function uniqueDeviceId(base: string, devices: readonly DeviceDraft[], fileDevices: readonly DeviceDraft[] = []): string {
  const taken = new Set([...devices, ...fileDevices].map((d) => d.id));
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base}_${n}`)) n += 1;
  return `${base}_${n}`;
}

const PREFILL_FIELDS: Record<string, { label: keyof Messages; unit: string }> = {
  min: { label: 'setupLoads.field.min', unit: ' A' },
  max: { label: 'setupLoads.field.max', unit: ' A' },
  step: { label: 'setupLoads.field.step', unit: ' A' },
  ratedCurrentA: { label: 'setupLoads.field.ratedCurrentA', unit: ' A' },
  ratedPowerW: { label: 'setupLoads.field.ratedPowerW', unit: ' W' },
  reactionTime: { label: 'setupLoads.field.reactionTime', unit: '' },
  minOn: { label: 'setupLoads.field.minOn', unit: '' },
  minOff: { label: 'setupLoads.field.minOff', unit: '' },
};

function prefillSummary(template: Template, t: Messages, lang: Lang): string {
  const parts = [typeLabel(template.type, t)];
  for (const [key, value] of Object.entries(template.prefill)) {
    if (key === 'type') continue;
    const known = PREFILL_FIELDS[key];
    const shown = typeof value === 'number' ? formatNumber(value, lang, 3) : value;
    parts.push(known === undefined ? `${key} ${shown}` : `${t[known.label].toLowerCase()} ${shown}${known.unit}`);
  }
  return parts.join(', ');
}

function TemplateCards({ setup, t, lang, onAdded }: { setup: SetupState; t: Messages; lang: Lang; onAdded: (index: number) => void }) {
  if (setup.templates.length === 0) return null;
  return (
    <div class="loads-block">
      <h3 id="setup-templates-title">{t['setupLoads.templates.title']}</h3>
      <div class="loads-templates" role="group" aria-labelledby="setup-templates-title">
        {setup.templates.map((template) => (
          <button
            key={template.id}
            type="button"
            class="loads-template"
            data-template={template.id}
            onClick={() => {
              const id = uniqueDeviceId(template.id, setup.draft.devices, setup.view?.draft.devices ?? []);
              setup.addDevice(newDevice({ ...template.prefill, id }, template.id));
              onAdded(setup.draft.devices.length);
            }}
          >
            <span class="t">{isKnownTemplate(template.id) ? t[`setupLoads.template.${template.id}.title`] : <span class="mono">{template.id}</span>}</span>
            {isKnownTemplate(template.id) && <span class="d">{t[`setupLoads.template.${template.id}.desc`]}</span>}
            <span class="sub">{fill(t['setupLoads.template.prefills'], { values: prefillSummary(template, t, lang) })}</span>
            <span class="sub">
              {fill(t['setupLoads.template.inherits'], { reaction: template.inherited.reactionTime, minOn: template.inherited.minOn, minOff: template.inherited.minOff })}
            </span>
          </button>
        ))}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// Recorded evidence (CONN-05, judged by the running add-on; the draft's verdict comes from validation).

export type EvidenceState = { kind: 'loading' } | { kind: 'ready'; devices: DeviceEvidence[] } | { kind: 'unavailable' };

function useEvidence(): { state: EvidenceState; refresh: () => void } {
  const [state, setState] = useState<EvidenceState>({ kind: 'loading' });
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    let disposed = false;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), SETUP_REQUEST_TIMEOUT_MS);
    fetchEvidence(controller.signal)
      .then((devices) => {
        if (!disposed) setState({ kind: 'ready', devices });
      })
      .catch(() => {
        if (!disposed) setState({ kind: 'unavailable' });
      })
      .finally(() => clearTimeout(timeout));
    return () => {
      disposed = true;
      clearTimeout(timeout);
      controller.abort();
    };
  }, [generation]);
  return { state, refresh: () => setGeneration((g) => g + 1) };
}

// ---------------------------------------------------------------------------------------------
// The passive phase self-test of one device.

/** A sentence whose {entity} is an entity ID, rendered in monospace like every entity ID. */
function withEntity(template: string, entity: string, values: Record<string, string> = {}) {
  const [before = '', after = ''] = template.split('{entity}');
  return (
    <>
      {fill(before, values)}
      <span class="mono">{entity}</span>
      {fill(after, values)}
    </>
  );
}

const reasonText = (t: Messages, reason: StepSummary['reason']) => t[`setupLoads.reason.${reason ?? 'timed_out'}`];

/**
 * A verdict in words. An inconclusive result is "not confirmed" with its reason, never a pass; a
 * mismatch names the phase that rose against the declared one.
 */
export function VerdictBadge({ summary, declared, t }: { summary: Pick<StepSummary, 'verdict' | 'observedPhase' | 'reason'>; declared: string | null; t: Messages }) {
  const tone: Tone = summary.verdict === 'confirmed' ? 'ok' : summary.verdict === 'mismatch' ? 'crit' : 'warn';
  const label =
    summary.verdict === 'confirmed'
      ? fill(t['setupLoads.verdict.confirmed'], { phase: summary.observedPhase ?? declared ?? '—' })
      : summary.verdict === 'mismatch'
        ? fill(t['setupLoads.verdict.mismatch'], { observed: summary.observedPhase ?? '—', declared: declared ?? '—' })
        : fill(t['setupLoads.verdict.inconclusive'], { reason: reasonText(t, summary.reason) });
  return (
    <span data-verdict={summary.verdict}>
      <StateBadge tone={tone} label={label} />
    </span>
  );
}

function Counts({ summary, t }: { summary: StepSummary; t: Messages }) {
  return (
    <span class="sub-inline">
      {fill(t['setupLoads.verdict.counts'], { confirmed: String(summary.confirmed), mismatched: String(summary.mismatched), inconclusive: String(summary.inconclusive) })}
    </span>
  );
}

function deltaText(observation: Observation, lang: Lang): string {
  return PHASES.filter((p) => observation.deltas[p] !== undefined)
    .map((p) => {
      const value = observation.deltas[p]!;
      return `${p} ${value > 0 ? '+' : ''}${formatQuantity(value, observation.unit, lang)}`;
    })
    .join(' · ');
}

/** Switchings with the step of every phase, the phase that rose and the verdict (a table that scrolls at 400 px). */
function Observations({ observations, declared, judged, t, lang }: { observations: readonly Observation[]; declared: string | null; judged: boolean; t: Messages; lang: Lang }) {
  if (observations.length === 0) return null;
  return (
    <div class="tablewrap">
      <table class="loads-observations">
        <thead>
          <tr>
            <th scope="col">{t['setupLoads.obs.time']}</th>
            <th scope="col">{t['setupLoads.obs.transition']}</th>
            <th scope="col">{t['setupLoads.obs.delta']}</th>
            <th scope="col">{t['setupLoads.obs.verdict']}</th>
          </tr>
        </thead>
        <tbody>
          {observations.map((o, i) => (
            <tr key={i} data-verdict={o.verdict}>
              <td>
                <Timestamp at={o.at} lang={lang} />
              </td>
              <td>
                {t[`setupLoads.obs.${o.transition}`]}
                <span class="sub">{t[`setupLoads.obs.source.${o.source}`]}</span>
              </td>
              <td class="mono">
                {deltaText(o, lang)}
                {o.houseLoad && <span class="sub">{t['setupLoads.obs.houseLoad']}</span>}
                {o.ratingRatio !== null && <span class="sub">{fill(t['setupLoads.obs.ratio'], { ratio: formatNumber(o.ratingRatio, lang, 2) })}</span>}
              </td>
              <td>{judged ? <VerdictBadge summary={o} declared={declared} t={t} /> : o.observedPhase === null ? '—' : fill(t['setupLoads.obs.stepOn'], { phase: o.observedPhase })}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

type Watch =
  | { kind: 'idle' }
  | { kind: 'starting' }
  | { kind: 'active'; id: string; test: SelfTest | null; lost: boolean }
  | { kind: 'refused'; issues: SetupIssue[] }
  | { kind: 'busy'; id: string }
  | { kind: 'failed' };

const WATCH_STORAGE_PREFIX = 'switchboard.setup.watch.';

/** The id of a watch of this entity started in this browser tab, if any (storage may be unavailable). */
function storedWatch(entity: string | null): string | null {
  try {
    return entity === null ? null : window.sessionStorage.getItem(WATCH_STORAGE_PREFIX + entity);
  } catch {
    return null;
  }
}

function storeWatch(entity: string | null, id: string | null): void {
  try {
    if (entity === null) return;
    if (id === null) window.sessionStorage.removeItem(WATCH_STORAGE_PREFIX + entity);
    else window.sessionStorage.setItem(WATCH_STORAGE_PREFIX + entity, id);
  } catch {
    // A convenience only: without storage the watch is not shown again after leaving the page.
  }
}

function withTimeout<T>(ms: number, run: (signal: AbortSignal) => Promise<T>): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), ms);
  return run(controller.signal).finally(() => clearTimeout(timeout));
}

// ---------------------------------------------------------------------------------------------
// The automatic (active) phase self-test of one relay device (CONN-05, maintainer survey
// 2026-10-25). The add-on decides everything: whether it may start (control active, headroom on
// every phase, lockouts, nothing else in flight), when to switch, and the verdict. The page asks,
// shows the reasons, and never retries a start on its own.

/** The relay stage the wizard tests: an editable on/off device has exactly one. */
const ACTIVE_TEST_STAGE = 1;

type ActiveRun =
  | { kind: 'idle' }
  | { kind: 'starting' }
  | { kind: 'refused'; refusals: ActiveTestRefusal[] }
  | { kind: 'failed' }
  | { kind: 'test'; id: string; test: ActiveTest | null; lost: boolean };

const ACTIVE_TEST_STORAGE_PREFIX = 'switchboard.setup.activeTest.';

/** The id of an automatic test of this device started in this browser tab, if any (storage may be unavailable). */
function storedActiveTest(device: string | null): string | null {
  try {
    return device === null ? null : window.sessionStorage.getItem(ACTIVE_TEST_STORAGE_PREFIX + device);
  } catch {
    return null;
  }
}

function storeActiveTest(device: string | null, id: string | null): void {
  try {
    if (device === null) return;
    if (id === null) window.sessionStorage.removeItem(ACTIVE_TEST_STORAGE_PREFIX + device);
    else window.sessionStorage.setItem(ACTIVE_TEST_STORAGE_PREFIX + device, id);
  } catch {
    // A convenience only: without storage a running test is not shown again after leaving the page.
  }
}

const isRunning = (run: ActiveRun) => run.kind === 'test' && (run.test === null || run.test.state === 'running');

/** One reason the add-on gives for not starting, in words (a lockout says how long, headroom names the phase). */
function refusalText(refusal: ActiveTestRefusal, t: Messages, lang: Lang): string {
  if (refusal.code === 'lockout_min_off') {
    return fill(t['setupLoads.active.refusal.lockout_min_off'], { remaining: refusal.remainingS === null ? '—' : formatAge(Math.ceil(refusal.remainingS), lang) });
  }
  if (refusal.code === 'headroom_insufficient') return fill(t['setupLoads.active.refusal.headroom_insufficient'], { phase: refusal.phase ?? '—' });
  return t[`setupLoads.active.refusal.${refusal.code}`];
}

function Refusals({ refusals, t, lang }: { refusals: readonly ActiveTestRefusal[]; t: Messages; lang: Lang }) {
  return (
    <ul class="loads-active-refusals">
      {refusals.map((refusal, n) => (
        <li key={n} data-refusal={refusal.code}>
          {refusalText(refusal, t, lang)}
        </li>
      ))}
    </ul>
  );
}

/** Whether the test may start now, with the add-on's reasons and the expected duration. */
function ActivePreconditions({ state, t, lang }: { state: PollView<ActiveTestPreconditions | null>; t: Messages; lang: Lang }) {
  if (state.kind === 'unavailable') return <p class="sub">{t['setupLoads.active.preconditionsUnavailable']}</p>;
  if (state.kind === 'loading' || state.data === null) return <p class="updated">{t['setupLoads.active.checking']}</p>;
  const { ok, refusals, expectedDurationS } = state.data;
  const duration = expectedDurationS === null ? null : formatAge(Math.ceil(expectedDurationS), lang);
  if (ok) return <p class="loads-active-ready">{fill(t['setupLoads.active.ready'], { duration: duration ?? '—' })}</p>;
  return (
    <div class="loads-active-blocked">
      <p>{t['setupLoads.active.blocked']}</p>
      <Refusals refusals={refusals} t={t} lang={lang} />
      {duration !== null && <p class="sub">{fill(t['setupLoads.active.duration'], { duration })}</p>}
    </div>
  );
}

const ACTIVE_STEPS = ['on', 'measureOn', 'off', 'judge'] as const;
type ActiveStep = (typeof ACTIVE_STEPS)[number];
type StepState = 'done' | 'current' | 'pending';

/** Where a test stands on each of its four steps, from the add-on's phase and the times it saw the device switch. */
function stepState(test: ActiveTest, step: ActiveStep): StepState {
  const running = test.state === 'running';
  const finished = test.state === 'finished';
  switch (step) {
    case 'on':
      return test.onAt !== null ? 'done' : running && test.phase === 'pending_on' ? 'current' : 'pending';
    case 'measureOn':
      return test.offAt !== null || finished || (running && (test.phase === 'pending_off' || test.phase === 'judging')) ? 'done' : running && test.phase === 'on' ? 'current' : 'pending';
    case 'off':
      return test.offAt !== null ? 'done' : running && test.phase === 'pending_off' ? 'current' : 'pending';
    case 'judge':
      return finished ? 'done' : running && test.phase === 'judging' ? 'current' : 'pending';
  }
}

/**
 * After the test, whether the device is back where it was (off: the test starts only from off, so
 * its last command is an off). A finished test saw the off itself. After an abort the off may wait
 * (min_on applies unless protection holds the device), so the add-on's own view comes first: while
 * it reports the device on, it is not back, whatever an older readout says; otherwise the live
 * state says whether the off has arrived.
 */
function ReturnToPrior({ test, readout, addOnSeesOn, t, lang }: { test: ActiveTest; readout: Readout | undefined; addOnSeesOn: boolean; t: Messages; lang: Lang }) {
  const live = readout?.status === 'found' ? readout.state : null;
  const [restored, text] =
    test.state === 'finished' && test.offAt !== null
      ? [true, fill(t['setupLoads.active.restored'], { time: localClock(test.offAt, lang) })]
      : addOnSeesOn || live === 'on'
        ? [false, t['setupLoads.active.stillOn']]
        : live === 'off'
          ? [true, t['setupLoads.active.restoredLive']]
          : [false, t['setupLoads.active.awaitingOff']];
  return (
    <p class={restored ? 'loads-active-return' : 'loads-active-return loads-field-issue'} data-restored={restored ? 'true' : 'false'}>
      {text}
    </p>
  );
}

/** One test as the add-on reports it: where it stands, the step per phase, the verdict, and the return to the prior state. */
function ActiveTestProgress({
  test,
  draftPhase,
  readout,
  addOnSeesOn,
  t,
  lang,
}: {
  test: ActiveTest;
  draftPhase: string | null;
  readout: Readout | undefined;
  addOnSeesOn: boolean;
  t: Messages;
  lang: Lang;
}) {
  const running = test.state === 'running';
  const headline =
    test.state === 'aborted'
      ? fill(t['setupLoads.active.state.aborted'], { reason: t[`setupLoads.active.abort.${test.abortReason ?? 'cancelled'}`] })
      : t[`setupLoads.active.state.${test.state}`];
  const phaseText =
    !running || test.phase === 'done'
      ? null
      : test.phase === 'on'
        ? fill(t['setupLoads.active.phase.on'], { time: localClock(test.onAt ?? test.startedAt, lang) })
        : test.phase === 'judging'
          ? fill(t['setupLoads.active.phase.judging'], { time: localClock(test.offAt ?? test.startedAt, lang) })
          : t[`setupLoads.active.phase.${test.phase}`];
  const times: Partial<Record<ActiveStep, Date | null>> = { on: test.onAt, off: test.offAt };
  return (
    <div class="loads-active-test" data-state={test.state}>
      <p>
        <strong>{headline}</strong>
        {phaseText !== null && <> {phaseText}</>}
      </p>
      {running && <p class="sub">{fill(t['setupLoads.active.until'], { time: localClock(new Date(test.startedAt.getTime() + test.expectedDurationS * 1000), lang) })}</p>}
      <p class="sub">
        <span class="mono">{test.stateEntity}</span> · {fill(t['setupLoads.active.declared'], { phase: test.expectedPhase })}
      </p>
      {draftPhase !== null && draftPhase !== test.expectedPhase && <p class="note">{fill(t['setupLoads.active.draftDiffers'], { draft: draftPhase, declared: test.expectedPhase })}</p>}
      <ol class="loads-active-steps">
        {ACTIVE_STEPS.map((step) => {
          const state = stepState(test, step);
          const at = times[step];
          return (
            <li key={step} data-step={step} data-state={state}>
              {t[`setupLoads.active.step.${step}`]}{' '}
              <span class="sub-inline">
                {t[`setupLoads.active.stepState.${state}`]}
                {state === 'done' && at !== undefined && at !== null && ` · ${localClock(at, lang)}`}
              </span>
            </li>
          );
        })}
      </ol>
      <Observations observations={test.observations} declared={test.expectedPhase} judged={test.verdict !== null} t={t} lang={lang} />
      {test.verdict !== null && (
        <p class="loads-active-verdict">
          <VerdictBadge summary={test.verdict} declared={test.expectedPhase} t={t} />
          {test.verdict.verdict === 'mismatch' && <span class="loads-field-issue">{fill(t['setupLoads.verdict.mismatchAdvice'], { observed: test.verdict.observedPhase ?? '—' })}</span>}
        </p>
      )}
      {!running && <ReturnToPrior test={test} readout={readout} addOnSeesOn={addOnSeesOn} t={t} lang={lang} />}
    </div>
  );
}

/**
 * CONN-05, active: on demand only, for a device of the configuration in force (a draft-only device
 * must be saved first). The button stays disabled until the add-on says the test may start, and a
 * start asks once more before anything is switched. A running test is followed until it ends; a
 * finished one asks the add-on to judge the draft again, like a finished watch.
 */
function ActivePhaseTest({ setup, device, readouts, onFinished, t, lang }: { setup: SetupState; device: DeviceDraft; readouts: ReadonlyMap<string, Readout>; onFinished: () => void; t: Messages; lang: Lang }) {
  const deviceId = device.id;
  // The device must be in the configuration in force. After a save in this visit the loaded view is
  // older than the file, so the add-on's own answer (not_declared) decides from then on.
  const queryable = deviceId !== null && (device.existing || setup.outcome?.kind === 'saved');
  const [run, setRun] = useState<ActiveRun>(() => {
    const stored = queryable ? storedActiveTest(deviceId) : null;
    return stored === null ? { kind: 'idle' } : { kind: 'test', id: stored, test: null, lost: false };
  });
  const [confirming, setConfirming] = useState(false);
  const disposed = useRef(false);
  useEffect(
    () => () => {
      disposed.current = true;
    },
    [],
  );
  const update = (next: ActiveRun) => {
    storeActiveTest(deviceId, next.kind === 'test' && !next.lost && isRunning(next) ? next.id : null);
    if (!disposed.current) setRun(next);
  };

  const running = isRunning(run);
  const askFor = queryable && !running ? deviceId : null;
  const preconditions = usePoll<ActiveTestPreconditions | null>(
    (signal) => (askFor === null ? Promise.resolve(null) : fetchActiveTestPreconditions(askFor, ACTIVE_TEST_STAGE, signal)),
    SETUP_SELF_TEST_REFRESH_INTERVAL_MS,
    SETUP_SELF_TEST_REQUEST_TIMEOUT_MS,
    askFor ?? '',
  );

  const pollId = run.kind === 'test' && !run.lost && running ? run.id : null;
  useEffect(() => {
    if (pollId === null) return;
    const timer = setTimeout(
      () => {
        void withTimeout(SETUP_SELF_TEST_REQUEST_TIMEOUT_MS, (signal) => fetchActiveTest(pollId, signal))
          .then((test) => {
            update({ kind: 'test', id: pollId, test, lost: false });
            if (test.state !== 'running' && !disposed.current) onFinished();
          })
          .catch(() => update({ kind: 'test', id: pollId, test: run.kind === 'test' ? run.test : null, lost: true }));
      },
      run.kind === 'test' && run.test === null ? 0 : SETUP_SELF_TEST_REFRESH_INTERVAL_MS,
    );
    return () => clearTimeout(timer);
  }, [pollId, run]);

  const start = async () => {
    setConfirming(false);
    if (deviceId === null) return;
    update({ kind: 'starting' });
    try {
      const result = await withTimeout(SETUP_REQUEST_TIMEOUT_MS, (signal) => startActiveTest(deviceId, ACTIVE_TEST_STAGE, signal));
      update(result.kind === 'started' ? { kind: 'test', id: result.id, test: null, lost: false } : { kind: 'refused', refusals: result.refusals });
    } catch {
      update({ kind: 'failed' });
    }
  };
  const cancel = async (id: string, test: ActiveTest | null) => {
    try {
      await withTimeout(SETUP_REQUEST_TIMEOUT_MS, (signal) => cancelActiveTest(id, signal));
    } catch {
      // Reading the test again shows whether it stopped.
    }
    update({ kind: 'test', id, test, lost: false });
  };

  const live = preconditions.kind === 'live' ? preconditions.data : null;
  const ready = live !== null && live.ok;
  const duration = live !== null && live.expectedDurationS !== null ? formatAge(Math.ceil(live.expectedDurationS), lang) : '—';
  const test = run.kind === 'test' ? run.test : null;
  const actuator = device.actuator;
  const idle = !running && run.kind !== 'starting';
  return (
    <div class="loads-active" data-active-test={run.kind}>
      <h5>{t['setupLoads.active.title']}</h5>
      <p class="sub">{t['setupLoads.active.lead']}</p>
      {!queryable ? (
        <p class="note loads-active-save">{t['setupLoads.active.saveFirst']}</p>
      ) : (
        <>
          {idle && <ActivePreconditions state={preconditions} t={t} lang={lang} />}
          {idle && confirming ? (
            <div class="loads-confirm" role="alertdialog" aria-label={t['setupLoads.active.start']}>
              <p>{actuator === null ? fill(t['setupLoads.active.confirm'], { entity: deviceLabel(device, t), duration }) : withEntity(t['setupLoads.active.confirm'], actuator, { duration })}</p>
              <div class="loads-actions">
                <button type="button" class="log-button loads-primary" disabled={!ready} onClick={() => void start()}>
                  {t['setupLoads.active.confirmYes']}
                </button>
                <button type="button" class="log-button" onClick={() => setConfirming(false)}>
                  {t['setupLoads.active.confirmNo']}
                </button>
              </div>
            </div>
          ) : (
            <div class="loads-actions">
              {idle && (
                <button type="button" class="log-button" disabled={!ready} onClick={() => setConfirming(true)}>
                  {test !== null ? t['setupLoads.active.again'] : t['setupLoads.active.start']}
                </button>
              )}
              {run.kind === 'starting' && <span class="updated">{t['setupLoads.active.starting']}</span>}
              {run.kind === 'test' && running && (
                <button type="button" class="log-button loads-danger" onClick={() => void cancel(run.id, run.test)}>
                  {t['setupLoads.active.cancel']}
                </button>
              )}
            </div>
          )}
          {run.kind === 'refused' && (
            <div class="loads-active-blocked" role="alert">
              <p>{t['setupLoads.active.refused']}</p>
              <Refusals refusals={run.refusals} t={t} lang={lang} />
            </div>
          )}
          {run.kind === 'failed' && <p class="loads-field-issue">{t['setupLoads.active.failed']}</p>}
          {run.kind === 'test' && (
            <div class="loads-active-run" role="status">
              {test !== null && (
                <ActiveTestProgress
                  test={test}
                  draftPhase={device.phase}
                  readout={readouts.get(test.stateEntity)}
                  addOnSeesOn={live !== null && live.refusals.some((r) => r.code === 'device_on')}
                  t={t}
                  lang={lang}
                />
              )}
              {run.lost && (
                <p class="loads-field-issue">
                  {t['setupLoads.active.lost']}{' '}
                  <button type="button" class="linkbutton" onClick={() => update({ ...run, lost: false })}>
                    {t['setupLoads.active.retry']}
                  </button>
                </p>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * CONN-05: the automatic test above (Switchboard switches, on demand, below protection) and the
 * guided watch (the user switches the device themselves while the wizard shows which meter phase
 * stepped). The draft's verdict over everything observed (automatic tests, guided watches and
 * switchings the running add-on recorded) comes from the add-on's validation, which judges it again
 * against this draft's phase and meter mapping.
 */
function PhaseSelfTest({
  setup,
  index,
  device,
  evidence,
  readouts,
  onFinished,
  t,
  lang,
}: {
  setup: SetupState;
  index: number;
  device: DeviceDraft;
  evidence: EvidenceState;
  readouts: ReadonlyMap<string, Readout>;
  onFinished: () => void;
  t: Messages;
  lang: Lang;
}) {
  const stateEntity = device.stateEntity ?? device.actuator;
  // A watch outlives the page: the user leaves for Home Assistant to switch the device, and on the
  // way back the watch is shown again (the add-on keeps judging it on its own timer).
  const [watch, setWatch] = useState<Watch>(() => {
    const stored = storedWatch(stateEntity);
    return stored === null ? { kind: 'idle' } : { kind: 'active', id: stored, test: null, lost: false };
  });
  const disposed = useRef(false);
  useEffect(
    () => () => {
      disposed.current = true;
    },
    [],
  );
  const update = (next: Watch) => {
    const keep = next.kind === 'active' && !next.lost && (next.test === null || next.test.status === 'watching');
    storeWatch(stateEntity, keep ? next.id : null);
    if (!disposed.current) setWatch(next);
  };

  const activeId = watch.kind === 'active' && !watch.lost && (watch.test === null || watch.test.status === 'watching') ? watch.id : null;
  useEffect(() => {
    if (activeId === null) return;
    const timer = setTimeout(
      () => {
        void withTimeout(SETUP_SELF_TEST_REQUEST_TIMEOUT_MS, (signal) => fetchSelfTest(signal, activeId))
          .then((test) => {
            update({ kind: 'active', id: activeId, test, lost: false });
            if (test.status !== 'watching' && !disposed.current) onFinished();
          })
          .catch(() => update({ kind: 'active', id: activeId, test: watch.kind === 'active' ? watch.test : null, lost: true }));
      },
      watch.kind === 'active' && watch.test === null ? 0 : SETUP_SELF_TEST_REFRESH_INTERVAL_MS,
    );
    return () => clearTimeout(timer);
  }, [activeId, watch]);

  const start = async () => {
    update({ kind: 'starting' });
    try {
      const result = await withTimeout(SETUP_REQUEST_TIMEOUT_MS, (signal) => startSelfTest(setup.draft, { device: { index } }, signal));
      if (result.kind === 'started') update({ kind: 'active', id: result.id, test: null, lost: false });
      else if (result.kind === 'busy') update({ kind: 'busy', id: result.id });
      else update({ kind: 'refused', issues: result.issues });
    } catch {
      update({ kind: 'failed' });
    }
  };
  const cancel = async (id: string, after: Watch) => {
    try {
      await cancelSelfTest(id);
    } catch {
      // The watch ends on its own at its timeout; reading it again shows its state.
    }
    update(after);
  };

  // The add-on's verdict holds for the phase and entity it judged: an edit since makes it stale
  // ("rose on L3, declared L3" would contradict itself), so it shows again only after a new check.
  const judgedFor = useRef({ validation: setup.validation, phase: device.phase, stateEntity });
  if (judgedFor.current.validation !== setup.validation) judgedFor.current = { validation: setup.validation, phase: device.phase, stateEntity };
  const current = judgedFor.current.phase === device.phase && judgedFor.current.stateEntity === stateEntity;
  const judged = current ? setup.validation?.selfTest.find((s) => s.field === `devices[${index}].phase`) : undefined;
  const recorded = evidence.kind === 'ready' ? evidence.devices.find((d) => d.stateEntity === stateEntity) : undefined;
  const test = watch.kind === 'active' ? watch.test : null;
  return (
    <section class="loads-selftest" aria-labelledby={`setup-selftest-${index}`} data-device={index}>
      <h4 id={`setup-selftest-${index}`}>{t['setupLoads.selfTest.title']}</h4>
      <p class="sub">{t['setupLoads.selfTest.why']}</p>

      <div class="loads-selftest-verdict" data-field={`devices[${index}].phase`}>
        {judged === undefined ? (
          <span class="sub-inline">{t['setupLoads.history.none']}</span>
        ) : (
          <>
            <VerdictBadge summary={judged} declared={device.phase} t={t} /> <Counts summary={judged} t={t} />
            {judged.verdict === 'mismatch' && <p class="loads-field-issue">{fill(t['setupLoads.verdict.mismatchAdvice'], { observed: judged.observedPhase ?? '—' })}</p>}
          </>
        )}
      </div>
      <div class="loads-actions">
        <button type="button" class="log-button" disabled={setup.busy !== 'idle'} onClick={() => void setup.validate()}>
          {t['setupLoads.history.check']}
        </button>
      </div>

      <ActivePhaseTest setup={setup} device={device} readouts={readouts} onFinished={onFinished} t={t} lang={lang} />

      <h5>{t['setupLoads.selfTest.passiveTitle']}</h5>
      <p class="loads-selftest-passive">
        <strong>{t['setupLoads.selfTest.passive']}</strong>
      </p>
      <div class="loads-actions">
        {(watch.kind === 'idle' || watch.kind === 'refused' || watch.kind === 'failed' || (watch.kind === 'active' && test !== null && test.status !== 'watching')) && (
          <button type="button" class="log-button" disabled={stateEntity === null} onClick={() => void start()}>
            {watch.kind === 'active' ? t['setupLoads.selfTest.again'] : t['setupLoads.selfTest.start']}
          </button>
        )}
        {watch.kind === 'starting' && <span class="updated">{t['setupLoads.selfTest.starting']}</span>}
        {watch.kind === 'active' && (test === null || test.status === 'watching') && (
          <button type="button" class="log-button" onClick={() => void cancel(watch.id, { kind: 'active', id: watch.id, test, lost: false })}>
            {t['setupLoads.selfTest.cancel']}
          </button>
        )}
      </div>

      {watch.kind === 'refused' && <SetupIssues issues={watch.issues} t={t} />}
      {watch.kind === 'failed' && <p class="loads-field-issue">{t['setupLoads.selfTest.failed']}</p>}
      {watch.kind === 'busy' && (
        <p class="note" role="status">
          {t['setupLoads.selfTest.busy']}{' '}
          <button type="button" class="linkbutton" onClick={() => void cancel(watch.id, { kind: 'idle' })}>
            {t['setupLoads.selfTest.stopOther']}
          </button>
        </p>
      )}
      {watch.kind === 'active' && (
        <div class="loads-selftest-watch" role="status">
          {test === null || test.status === 'watching' ? (
            test !== null && <p>{withEntity(t['setupLoads.selfTest.watching'], test.expected.stateEntity, { time: localClock(test.expiresAt, lang) })}</p>
          ) : (
            <p>
              {t[`setupLoads.selfTest.status.${test.status}`]} <VerdictBadge summary={test.verdict} declared={test.expected.phase} t={t} />
              {test.verdict.verdict === 'mismatch' && <span class="loads-field-issue">{fill(t['setupLoads.verdict.mismatchAdvice'], { observed: test.verdict.observedPhase ?? '—' })}</span>}
            </p>
          )}
          {watch.lost && (
            <p class="loads-field-issue">
              {t['setupLoads.selfTest.lost']}{' '}
              <button type="button" class="linkbutton" onClick={() => update({ ...watch, lost: false })}>
                {t['setupLoads.selfTest.retry']}
              </button>
            </p>
          )}
          {test !== null && <Observations observations={test.observations} declared={test.expected.phase} judged t={t} lang={lang} />}
        </div>
      )}

      <details class="loads-selftest-history">
        <summary>{t['setupLoads.history.title']}</summary>
        <p class="sub">{t['setupLoads.history.lead']}</p>
        {evidence.kind === 'unavailable' && <p class="sub">{t['setupLoads.history.unavailable']}</p>}
        {evidence.kind === 'ready' && recorded === undefined && stateEntity !== null && <p class="sub">{withEntity(t['setupLoads.history.empty'], stateEntity)}</p>}
        {recorded !== undefined && (
          <>
            {recorded.observations.length === 0 && <p class="sub">{withEntity(t['setupLoads.history.empty'], recorded.stateEntity)}</p>}
            {recorded.observations.length > 0 && recorded.phase !== device.phase && <p class="sub">{fill(t['setupLoads.history.otherPhase'], { phase: recorded.phase })}</p>}
            <Observations observations={recorded.observations} declared={recorded.phase} judged={recorded.phase === device.phase} t={t} lang={lang} />
          </>
        )}
      </details>
    </section>
  );
}

// ---------------------------------------------------------------------------------------------
// Start policy and stop actuator (control stage, maintainer survey 2026-10-25).

/** The spellings of start: the device parser accepts; the first one is the shipped default (devices.default_start). */
const START_POLICIES = ['restore_only', 'headroom'] as const;
const isStartPolicy = (value: string): value is (typeof START_POLICIES)[number] => (START_POLICIES as readonly string[]).includes(value);

/**
 * A modulating device's stop actuator: the switch that stops it below its minimum (a charger's
 * charging switch). The parser accepts the relay domains of an on/off device, so the picker offers
 * those, and the switches of the control entity's own Home Assistant device are listed first and
 * named as suggestions. Nothing is preselected: only the user declares (CLAUDE.md).
 */
function StopActuatorField({ setup, index, device, entities, t }: { setup: SetupState; index: number; device: DeviceDraft; entities: readonly DiscoveredEntity[] | null; t: Messages }) {
  const field = `devices[${index}].stopActuator`;
  const filter: PickerFilter = { domains: setup.templates.find((tpl) => tpl.type === 'onoff')?.actuatorDomains ?? [] };
  const haDevice = device.actuator === null ? null : (entities?.find((e) => e.entityId === device.actuator)?.deviceId ?? null);
  const sameDevice = entities === null || haDevice === null ? [] : entities.filter((e) => e.deviceId === haDevice && e.derived === null && matchesFilter(e, filter));
  const ordered = entities === null ? null : [...sameDevice, ...entities.filter((e) => !sameDevice.includes(e))];
  return (
    <div class="loads-field loads-stop">
      <EntityPicker id={fieldId(field)} label={t['setupLoads.field.stopActuator']} value={device.stopActuator} onChange={(v) => setup.set(field, v)} entities={ordered} filter={filter} t={t} />
      <span class="sub">{t['setupLoads.field.stopActuatorHint']}</span>
      {entities !== null &&
        (device.actuator === null ? (
          <span class="sub">{t['setupLoads.stop.pickActuator']}</span>
        ) : sameDevice.length === 0 ? (
          <span class="sub loads-stop-none">{withEntity(t['setupLoads.stop.none'], device.actuator)}</span>
        ) : (
          <div class="loads-stop-suggest">
            <span class="sub">{withEntity(t['setupLoads.stop.suggested'], device.actuator)}</span>
            <ul>
              {sameDevice.map((e) => (
                <li key={e.entityId} data-entity={e.entityId}>
                  <button type="button" class="linkbutton" aria-pressed={device.stopActuator === e.entityId} onClick={() => setup.set(field, e.entityId)}>
                    {withEntity(t['setupLoads.stop.use'], e.entityId)}
                  </button>
                  {e.name !== null && <span class="sub-inline"> {e.name}</span>}
                </li>
              ))}
            </ul>
            <span class="sub">{t['setupLoads.stop.check']}</span>
          </div>
        ))}
      <FieldErrors issues={setup.issues} field={field} t={t} />
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// The device form (shared with step 3's storage devices).

export interface EditorContext {
  entities: readonly DiscoveredEntity[] | null;
  readouts: ReadonlyMap<string, Readout>;
  evidence: EvidenceState | null;
  onFinishedSelfTest: () => void;
}

/** What the user sees as a device's title: its name, else its id, else "New device". */
export function deviceLabel(device: DeviceDraft, t: Messages): string {
  return device.name ?? device.id ?? t['setupLoads.device.unnamed'];
}

function RemoveControl({ setup, index, device, t }: { setup: SetupState; index: number; device: DeviceDraft; t: Messages }) {
  const [confirming, setConfirming] = useState(false);
  if (!confirming) {
    return (
      <button type="button" class="log-button loads-remove" onClick={() => setConfirming(true)}>
        {t['setupLoads.remove']}
      </button>
    );
  }
  return (
    <div class="loads-confirm" role="alertdialog" aria-label={t['setupLoads.remove']}>
      <p>{fill(t['setupLoads.remove.confirm'], { device: deviceLabel(device, t) })}</p>
      <div class="loads-actions">
        <button type="button" class="log-button loads-danger" onClick={() => setup.removeDevice(index)}>
          {t['setupLoads.remove.yes']}
        </button>
        <button type="button" class="log-button" onClick={() => setConfirming(false)}>
          {t['setupLoads.remove.no']}
        </button>
      </div>
    </div>
  );
}

/**
 * One device of the draft (UI-02, CONN-02, UI-06). An existing device keeps its id and type (the
 * add-on decides again from the file on a save); a device the wizard cannot represent is shown as
 * kept as written and can only be removed. Removal always asks first.
 */
export function DeviceEditor({ setup, index, device, context, t, lang }: { setup: SetupState; index: number; device: DeviceDraft; context: EditorContext; t: Messages; lang: Lang }) {
  const path = `devices[${index}]`;
  const f = (name: string) => `${path}.${name}`;
  const id = (name: string) => fieldId(f(name));
  const set = (name: string, value: unknown) => setup.set(f(name), value);
  const spec = deviceSpec(device, setup.templates);
  const relay = device.type === 'onoff';
  const setpoint = device.type === 'modulating' || device.type === 'storage';
  const state = device.stateEntity ?? device.actuator;
  const others = setup.draft.devices.filter((_, i) => i !== index);
  const rotationNames = [...new Set(others.map((d) => d.rotationGroup).filter((g): g is string => g !== null))];
  const circuitNames = [...new Set(others.map((d) => d.circuitGroup).filter((g): g is string => g !== null))];
  const declaredGroups = setup.view?.preserved.find((p) => p.path === 'circuit_groups')?.summary ?? null;
  const inherited = (name: 'reactionTime' | 'minOn' | 'minOff') =>
    spec.inherited === null ? t['setupLoads.inheritedUnknown'] : fill(t['setupLoads.inherited'], { value: spec.inherited[name] });
  const setDraw = (key: 'current' | 'power' | 'maxAge', value: string | null) => {
    const next: NonNullable<DeviceDraft['draw']> = { ...drawOf(device.draw), [key]: value };
    if (key !== 'maxAge') {
      // The unit comes with the entity: the add-on reads the draw in it, so a kW sensor is never read as W.
      const reported = value === null ? null : (context.entities?.find((e) => e.entityId === value)?.unit ?? context.readouts.get(value)?.unit ?? null);
      next[DRAW_UNIT_KEY[key]] = drawUnit(reported, key);
    }
    set('draw', next.current === null && next.power === null && next.maxAge === null ? null : next);
  };

  // An entity typed by hand (no discovery) gets its unit from the live readout once it arrives.
  const drawUnits = (['current', 'power'] as const).map((key) => {
    const entity = device.draw?.[key] ?? null;
    return entity === null ? null : drawUnit(context.readouts.get(entity)?.unit ?? null, key);
  });
  useEffect(() => {
    (['current', 'power'] as const).forEach((key, i) => {
      const unit = drawUnits[i];
      if (unit !== null && device.draw !== null && unit !== (device.draw[DRAW_UNIT_KEY[key]] ?? null)) set(`draw.${DRAW_UNIT_KEY[key]}`, unit);
    });
  }, [drawUnits[0], drawUnits[1]]);

  const badge = !device.editable ? (
    <StateBadge tone="muted" label={t['setupLoads.badge.kept']} />
  ) : device.existing ? (
    <StateBadge tone="ok" label={t['setupLoads.badge.existing']} />
  ) : (
    <StateBadge tone="warn" label={t['setupLoads.badge.new']} />
  );

  return (
    <article class="loads-device" id={fieldId(path)} tabIndex={-1} aria-labelledby={`${fieldId(path)}-title`} data-device={device.id ?? undefined} data-existing={device.existing ? 'true' : undefined}>
      <header class="loads-device-head">
        <h4 id={`${fieldId(path)}-title`}>{deviceLabel(device, t)}</h4>
        {badge}
        <span class="sub-inline">
          {typeLabel(device.type, t)}
          {device.phase !== null ? ` · ${device.phase === 'three_phase' ? t['setupLoads.phase.three_phase'] : device.phase}` : ''}
        </span>
      </header>

      {!device.editable ? (
        <>
          <p class="sub">{t['setupLoads.kept.note']}</p>
          {device.id !== null && <p class="mono">{device.id}</p>}
          <RemoveControl setup={setup} index={index} device={device} t={t} />
        </>
      ) : (
        <>
          <div class="loads-grid">
            <Field label={t['setupLoads.field.id']} htmlFor={id('id')} field={f('id')} setup={setup} t={t} hint={device.existing ? t['setupLoads.field.idFixed'] : t['setupLoads.field.idHint']}>
              {device.existing ? (
                <input id={id('id')} type="text" class="mono" value={device.id ?? ''} readOnly />
              ) : (
                <TextInput id={id('id')} value={device.id} onChange={(v) => set('id', v)} invalid={hasError(setup, f('id'))} mono trim />
              )}
            </Field>
            <Field label={t['setupLoads.field.name']} htmlFor={id('name')} field={f('name')} setup={setup} t={t}>
              <TextInput id={id('name')} value={device.name} onChange={(v) => set('name', v)} invalid={hasError(setup, f('name'))} />
            </Field>
            <Field label={t['setupLoads.field.type']} htmlFor={id('type')} field={f('type')} setup={setup} t={t}>
              <input id={id('type')} type="text" value={typeLabel(device.type, t)} readOnly />
            </Field>
          </div>

          <div class="loads-grid">
            <div class="loads-field">
              <EntityPicker
                id={id('actuator')}
                label={t['setupLoads.field.actuator']}
                value={device.actuator}
                onChange={(v) => set('actuator', v)}
                entities={context.entities}
                filter={{ domains: spec.actuatorDomains }}
                t={t}
              />
              <span class="sub">{t['setupLoads.field.actuatorHint']}</span>
              <FieldErrors issues={setup.issues} field={f('actuator')} t={t} />
            </div>
            <div class="loads-field">
              <EntityPicker
                id={id('stateEntity')}
                label={t['setupLoads.field.stateEntity']}
                value={device.stateEntity}
                onChange={(v) => set('stateEntity', v)}
                entities={context.entities}
                filter={{ domains: spec.stateDomains }}
                t={t}
              />
              <span class="sub">{t['setupLoads.field.stateEntityHint']}</span>
              <FieldErrors issues={setup.issues} field={f('stateEntity')} t={t} />
            </div>
            {device.type === 'modulating' && <StopActuatorField setup={setup} index={index} device={device} entities={context.entities} t={t} />}
          </div>
          {state !== null && (
            <p class="loads-readout">
              <span class="sub-inline">{t['setupLoads.readout.label']}</span> <span class="mono">{state}</span> <ReadoutLine readout={context.readouts.get(state)} t={t} lang={lang} />
            </p>
          )}

          <div class="loads-grid">
            <Field label={t['setupLoads.field.stateReports']} htmlFor={id('stateReports')} field={f('stateReports')} setup={setup} t={t} hint={t['setupLoads.field.stateReportsHint']}>
              <select id={id('stateReports')} value={device.stateReports ?? ''} onChange={(e) => set('stateReports', (e.currentTarget as HTMLSelectElement).value || null)}>
                <option value="">{t['setupLoads.stateReports.default']}</option>
                {device.stateReports === 'age' && <option value="age">{t['setupLoads.stateReports.age']}</option>}
                <option value="on_change">{t['setupLoads.stateReports.on_change']}</option>
              </select>
            </Field>
            <Field label={t['setupLoads.field.phase']} htmlFor={id('phase')} field={f('phase')} setup={setup} t={t} hint={t['setupLoads.field.phaseHint']}>
              <select id={id('phase')} value={device.phase ?? ''} aria-invalid={hasError(setup, f('phase')) ? true : undefined} onChange={(e) => set('phase', (e.currentTarget as HTMLSelectElement).value || null)}>
                <option value="">{t['setupLoads.phase.choose']}</option>
                {PHASES.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
                <option value="three_phase">{t['setupLoads.phase.three_phase']}</option>
              </select>
            </Field>
          </div>

          <div class="loads-grid">
            {relay && (
              <>
                <Field label={`${t['setupLoads.field.ratedCurrentA']} (A)`} htmlFor={id('ratedCurrentA')} field={f('ratedCurrentA')} setup={setup} t={t}>
                  <NumberInput id={id('ratedCurrentA')} value={device.ratedCurrentA} onChange={(v) => set('ratedCurrentA', v)} invalid={hasError(setup, f('ratedCurrentA'))} t={t} />
                </Field>
                <Field label={`${t['setupLoads.field.ratedPowerW']} (W)`} htmlFor={id('ratedPowerW')} field={f('ratedPowerW')} setup={setup} t={t}>
                  <NumberInput id={id('ratedPowerW')} value={device.ratedPowerW} onChange={(v) => set('ratedPowerW', v)} invalid={hasError(setup, f('ratedPowerW'))} t={t} />
                </Field>
              </>
            )}
            {setpoint &&
              (['min', 'max', 'step'] as const).map((name) => (
                <Field key={name} label={`${t[`setupLoads.field.${name}`]} (A)`} htmlFor={id(name)} field={f(name)} setup={setup} t={t}>
                  <NumberInput id={id(name)} value={device[name]} onChange={(v) => set(name, v)} invalid={hasError(setup, f(name))} t={t} />
                </Field>
              ))}
          </div>
          {device.type === 'storage' && <p class="sub">{t['setupLoads.storage.range']}</p>}

          <fieldset class="loads-fieldset">
            <legend>{t['setupLoads.lockouts']}</legend>
            <div class="loads-grid">
              {(['reactionTime', 'minOn', 'minOff'] as const).map((name) => (
                <Field key={name} label={t[`setupLoads.field.${name}`]} htmlFor={id(name)} field={f(name)} setup={setup} t={t}>
                  <TextInput id={id(name)} value={device[name]} onChange={(v) => set(name, v)} invalid={hasError(setup, f(name))} placeholder={inherited(name)} trim />
                </Field>
              ))}
            </div>
            <span class="sub">{t['setupLoads.lockoutsHint']}</span>
          </fieldset>

          <div class="loads-grid">
            <Field label={t['setupLoads.field.valuePerKwh']} htmlFor={id('valuePerKwh')} field={f('valuePerKwh')} setup={setup} t={t} hint={t['setupLoads.field.valuePerKwhHint']}>
              <NumberInput id={id('valuePerKwh')} value={device.valuePerKwh} onChange={(v) => set('valuePerKwh', v)} invalid={hasError(setup, f('valuePerKwh'))} t={t} />
            </Field>
            {device.type !== 'storage' && (
              <Field label={t['setupLoads.field.start']} htmlFor={id('start')} field={f('start')} setup={setup} t={t} hint={t['setupLoads.field.startHint']}>
                {/* No choice yet shows the shipped default; the add-on writes start: only when it differs from the file's default or the file had it. */}
                <select id={id('start')} value={device.start ?? 'restore_only'} aria-invalid={hasError(setup, f('start')) ? true : undefined} onChange={(e) => set('start', (e.currentTarget as HTMLSelectElement).value)}>
                  {START_POLICIES.map((policy) => (
                    <option key={policy} value={policy}>
                      {t[`setupLoads.start.${policy}`]}
                    </option>
                  ))}
                  {device.start !== null && !isStartPolicy(device.start) && <option value={device.start}>{device.start}</option>}
                </select>
              </Field>
            )}
            <Field
              label={t['setupLoads.field.circuitGroup']}
              htmlFor={id('circuitGroup')}
              field={f('circuitGroup')}
              setup={setup}
              t={t}
              hint={declaredGroups === null ? t['setupLoads.field.circuitGroupHint'] : `${t['setupLoads.field.circuitGroupHint']} ${fill(t['setupLoads.circuitGroups.declared'], { summary: declaredGroups })}`}
            >
              <TextInput id={id('circuitGroup')} value={device.circuitGroup} onChange={(v) => set('circuitGroup', v)} invalid={hasError(setup, f('circuitGroup'))} mono trim list={`${id('circuitGroup')}-names`} />
              <datalist id={`${id('circuitGroup')}-names`}>
                {circuitNames.map((name) => (
                  <option key={name} value={name} />
                ))}
              </datalist>
            </Field>
            {relay && (
              <Field label={t['setupLoads.field.rotationGroup']} htmlFor={id('rotationGroup')} field={f('rotationGroup')} setup={setup} t={t} hint={t['setupLoads.field.rotationGroupHint']}>
                <TextInput id={id('rotationGroup')} value={device.rotationGroup} onChange={(v) => set('rotationGroup', v)} invalid={hasError(setup, f('rotationGroup'))} mono trim list={`${id('rotationGroup')}-names`} />
                <datalist id={`${id('rotationGroup')}-names`}>
                  {rotationNames.map((name) => (
                    <option key={name} value={name} />
                  ))}
                </datalist>
              </Field>
            )}
          </div>

          <fieldset class="loads-fieldset" id={id('draw')} tabIndex={-1}>
            <legend>{t['setupLoads.draw.title']}</legend>
            <span class="sub">{t['setupLoads.draw.hint']}</span>
            {spec.drawRecommended && device.draw === null && <p class="note">{t['setupLoads.draw.recommended']}</p>}
            <div class="loads-grid">
              {(['current', 'power'] as const).map((key) => {
                const entity = device.draw?.[key] ?? null;
                return (
                  <div class="loads-field" key={key}>
                    <EntityPicker
                      id={fieldId(f(`draw.${key}`))}
                      label={t[`setupLoads.draw.${key}`]}
                      value={entity}
                      onChange={(v) => setDraw(key, v)}
                      entities={context.entities}
                      filter={key === 'current' ? CURRENT_SENSOR : POWER_SENSOR}
                      t={t}
                    />
                    {entity !== null && <ReadoutLine readout={context.readouts.get(entity)} t={t} lang={lang} />}
                    {entity !== null && <RefreshCheck setup={setup} field={f(`draw.${key}`)} onSuggest={(v) => setDraw('maxAge', v)} t={t} lang={lang} />}
                    <FieldErrors issues={setup.issues} field={f(`draw.${key}`)} t={t} />
                  </div>
                );
              })}
              <Field label={t['setupLoads.draw.maxAge']} htmlFor={fieldId(f('draw.maxAge'))} field={f('draw.maxAge')} setup={setup} t={t}>
                <TextInput
                  id={fieldId(f('draw.maxAge'))}
                  value={device.draw?.maxAge ?? null}
                  onChange={(v) => setDraw('maxAge', v)}
                  invalid={hasError(setup, f('draw.maxAge'))}
                  placeholder={t['setupLoads.inheritedUnknown']}
                  trim
                />
              </Field>
            </div>
          </fieldset>

          {relay && device.phase !== null && device.phase !== 'three_phase' ? (
            <PhaseSelfTest
              setup={setup}
              index={index}
              device={device}
              evidence={context.evidence ?? { kind: 'unavailable' }}
              readouts={context.readouts}
              onFinished={context.onFinishedSelfTest}
              t={t}
              lang={lang}
            />
          ) : (
            device.type !== 'storage' && <p class="sub loads-selftest-na">{t['setupLoads.selfTest.notApplicable']}</p>
          )}

          <RemoveControl setup={setup} index={index} device={device} t={t} />
        </>
      )}
    </article>
  );
}

// ---------------------------------------------------------------------------------------------
// Check and continue (UI-05): the add-on validates the whole draft; this step moves on only when
// none of its own fields carries an error, and otherwise lands on the first one.

/** The device index a field belongs to (devices[2].phase → 2), or null. */
export function deviceIndexOf(field: string | null): number | null {
  const match = /^devices\[(\d+)\]/.exec(field ?? '');
  return match === null ? null : Number(match[1]);
}

export function CheckAndContinue({ setup, label, owns, next, goTo, t }: { setup: SetupState; label: string; owns: (issue: SetupIssue) => boolean; next: StepName; goTo: (step: StepName) => void; t: Messages }) {
  const [failed, setFailed] = useState(false);
  const check = async () => {
    const result = await setup.validate();
    setFailed(result === null);
    if (result === null) return;
    const first = result.issues.find((issue) => issue.severity === 'error' && owns(issue));
    if (first === undefined) {
      goTo(next);
      return;
    }
    // The field exists on this step; focus lands on it after the issues render.
    setTimeout(() => document.getElementById(fieldId(first.field!))?.focus(), 0);
  };
  return (
    <div class="loads-actions loads-check">
      <button type="button" class="log-button loads-primary" disabled={setup.busy !== 'idle'} onClick={() => void check()}>
        {setup.busy === 'validating' ? t['setupLoads.checking'] : label}
      </button>
      {failed && (
        <span class="loads-field-issue" role="alert">
          {t['setupLoads.checkFailed']}
        </span>
      )}
    </div>
  );
}

const DRAW_UNIT_KEY = { current: 'currentUnit', power: 'powerUnit' } as const;

/** A draw as a complete object, for an edit of one of its keys. */
function drawOf(draw: DeviceDraft['draw']): NonNullable<DeviceDraft['draw']> {
  return {
    current: draw?.current ?? null,
    power: draw?.power ?? null,
    maxAge: draw?.maxAge ?? null,
    currentUnit: draw?.currentUnit ?? null,
    powerUnit: draw?.powerUnit ?? null,
  };
}

/** Home Assistant's unit of a draw entity when the signal layer accepts it (A or mA, W or kW); null otherwise (the add-on then names the mismatch). */
export function drawUnit(reported: string | null, key: 'current' | 'power'): string | null {
  const accepted: readonly string[] = key === 'current' ? CURRENT_UNITS : POWER_UNITS;
  return reported !== null && accepted.includes(reported) ? reported : null;
}

/** The entities a device's readouts follow: its state (or actuator) and its draw. */
export function deviceEntities(device: DeviceDraft): (string | null)[] {
  return [device.stateEntity ?? device.actuator, device.draw?.current ?? null, device.draw?.power ?? null];
}

// ---------------------------------------------------------------------------------------------

/**
 * Step 4 (Loads): template cards, then every device of the draft (existing ones first, as the file
 * lists them, so re-running the wizard shows and keeps them all), each with its pickers, live
 * readouts, phase, groups and start policy, a charger's stop actuator, and the phase self-test
 * (automatic and passive) of an on/off device. Storage devices are edited in step 3.
 */
export function SetupLoads({ setup, t, lang, goTo }: StepProps) {
  const discovery = useDiscovery();
  const evidence = useEvidence();
  const [focusIndex, setFocusIndex] = useState<number | null>(null);
  const devices = setup.draft.devices.map((device, index) => ({ device, index })).filter(({ device }) => device.type !== 'storage');
  const storage = new Set(setup.draft.devices.flatMap((d, i) => (d.type === 'storage' ? [i] : [])));
  const readouts = useReadoutMap(devices.flatMap(({ device }) => deviceEntities(device)));
  const owns = (issue: SetupIssue) => stepOfField(issue.field) === 'loads' && !storage.has(deviceIndexOf(issue.field) ?? -1);
  const context: EditorContext = {
    entities: discoveredEntities(discovery.state),
    readouts,
    evidence: evidence.state,
    onFinishedSelfTest: () => {
      evidence.refresh();
      void setup.validate();
    },
  };

  useEffect(() => {
    if (focusIndex === null) return;
    const field = document.getElementById(fieldId(`devices[${focusIndex}].actuator`));
    if (field !== null) {
      field.focus();
      setFocusIndex(null);
    }
  });

  return (
    <section class="card setup-step" aria-labelledby="setup-loads-title" data-step="loads">
      <header class="cardhead">
        <h2 id="setup-loads-title">{t['setupLoads.title']}</h2>
        <p class="sub">{t['setupLoads.stepLine']}</p>
      </header>
      <p>{t['setupLoads.lead']}</p>
      <SetupIssues issues={setup.issuesOf('loads').filter(owns)} t={t} />
      <DiscoveryNotice state={discovery.state} refresh={discovery.refresh} t={t} />
      <TemplateCards setup={setup} t={t} lang={lang} onAdded={setFocusIndex} />

      <div class="loads-block">
        <h3>{t['setupLoads.devices.title']}</h3>
        {devices.length === 0 ? (
          <p class="sub">{t['setupLoads.devices.none']}</p>
        ) : (
          <p class="note loads-added">
            {t['setupLoads.devices.added']}{' '}
            {devices.map(({ device, index }, n) => (
              <span key={index}>
                {n > 0 && ', '}
                <strong>{deviceLabel(device, t)}</strong>
                {device.phase !== null && ` (${device.phase === 'three_phase' ? t['setupLoads.phase.three_phase'] : device.phase})`}
              </span>
            ))}
          </p>
        )}
        {discovery.state.kind !== 'loading' &&
          devices.map(({ device, index }) => (
            // Keyed by position and count: a removal or an addition starts every form's local state afresh.
            <DeviceEditor key={`${index}/${setup.draft.devices.length}`} setup={setup} index={index} device={device} context={context} t={t} lang={lang} />
          ))}
      </div>

      <CheckAndContinue setup={setup} label={t['setupLoads.check']} owns={owns} next="mode" goTo={goTo} t={t} />
    </section>
  );
}
