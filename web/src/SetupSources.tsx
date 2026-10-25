import { useEffect, useRef, useState } from 'preact/hooks';
import type { DiscoveredEntity, Readout, SetupDraft, SetupIssue, SignalDraft } from './api/setup';
import { PHASES } from './api/signals';
import { EntityPicker, type PickerFilter } from './EntityPicker';
import { fill, type Lang, type Messages } from './i18n';
import { ReadoutLine } from './Readout';
import {
  CURRENT_SENSOR,
  CURRENT_UNITS,
  CheckAndContinue,
  DeviceEditor,
  DiscoveryNotice,
  Field,
  FieldErrors,
  POWER_SENSOR,
  POWER_UNITS,
  RefreshCheck,
  TextInput,
  deviceEntities,
  deviceIndexOf,
  discoveredEntities,
  hasError,
  uniqueDeviceId,
  useDiscovery,
  useReadoutMap,
  type EditorContext,
} from './SetupLoads';
import { SetupIssues } from './SetupIssues';
import { fieldId, newDevice, stepOfField } from './setupDraft';
import type { SetupState, StepProps } from './useSetup';
import './setupSources.css';

// Step 3 (Sources and storage, UI-01): the PV inverter with what unavailable_as_zero means, and a
// battery as a storage device (devices of type storage, edited with the same form as step 4's
// loads). Protection never reads generation; dispatch needs it to tell an idle phase from one whose
// load is covered by local production.

type Inverter = NonNullable<SetupDraft['sources']['inverter']>;

const NO_INVERTER: Inverter = { power: null, sign: null, unavailableAsZero: null, phase: null, phaseCurrents: null };
const INVERTER_SIGNS = ['positive_import', 'positive_export'] as const;
const isInverterSign = (sign: string): sign is (typeof INVERTER_SIGNS)[number] => (INVERTER_SIGNS as readonly string[]).includes(sign);

/**
 * One mapped entity of the inverter: the picker (filtered by device class or unit), its live value
 * and age (UI-04), its unit (taken from Home Assistant when picked) and max age with the add-on's
 * refresh check (SIG-07).
 */
function SignalRow({
  setup,
  path,
  label,
  signal,
  filter,
  units,
  entities,
  readouts,
  t,
  lang,
}: {
  setup: SetupState;
  path: string;
  label: string;
  signal: SignalDraft | null;
  filter: PickerFilter;
  units: readonly string[];
  entities: readonly DiscoveredEntity[] | null;
  readouts: ReadonlyMap<string, Readout>;
  t: Messages;
  lang: Lang;
}) {
  const write = (next: SignalDraft) => setup.set(path, next.entity === null && next.unit === null && next.maxAge === null ? null : next);
  const current: SignalDraft = signal ?? { entity: null, unit: null, maxAge: null };
  const pick = (entity: string | null) => {
    const found = entity === null ? undefined : entities?.find((e) => e.entityId === entity);
    const unit = found?.unit != null && units.includes(found.unit) ? found.unit : current.unit;
    write({ ...current, entity, unit });
  };
  const entity = current.entity;
  return (
    <div class="sources-signal" data-path={path}>
      <div class="loads-field">
        <EntityPicker id={fieldId(`${path}.entity`)} label={label} value={entity} onChange={pick} entities={entities} filter={filter} t={t} />
        {entity !== null && <ReadoutLine readout={readouts.get(entity)} t={t} lang={lang} />}
        <FieldErrors issues={setup.issues} field={`${path}.entity`} t={t} />
      </div>
      <div class="loads-grid">
        <Field label={t['setupSources.unit']} htmlFor={fieldId(`${path}.unit`)} field={`${path}.unit`} setup={setup} t={t}>
          <select
            id={fieldId(`${path}.unit`)}
            class="mono"
            value={current.unit ?? ''}
            aria-invalid={hasError(setup, `${path}.unit`) ? true : undefined}
            onChange={(e) => write({ ...current, unit: (e.currentTarget as HTMLSelectElement).value || null })}
          >
            <option value="">{t['setupSources.unit.choose']}</option>
            {[...units, ...(current.unit !== null && !units.includes(current.unit) ? [current.unit] : [])].map((unit) => (
              <option key={unit} value={unit}>
                {unit}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t['setupSources.maxAge']} htmlFor={fieldId(`${path}.maxAge`)} field={`${path}.maxAge`} setup={setup} t={t} hint={t['setupSources.maxAgeHint']}>
          <TextInput id={fieldId(`${path}.maxAge`)} value={current.maxAge} onChange={(maxAge) => write({ ...current, maxAge })} invalid={hasError(setup, `${path}.maxAge`)} mono trim />
          <RefreshCheck setup={setup} field={path} onSuggest={(maxAge) => write({ ...current, maxAge })} t={t} lang={lang} />
        </Field>
      </div>
    </div>
  );
}

function InverterForm({ setup, inverter, entities, readouts, t, lang }: { setup: SetupState; inverter: Inverter; entities: readonly DiscoveredEntity[] | null; readouts: ReadonlyMap<string, Readout>; t: Messages; lang: Lang }) {
  const set = (name: string, value: unknown) => setup.set(`sources.inverter.${name}`, value);
  const sign = inverter.sign;
  return (
    <div class="sources-inverter">
      <SignalRow setup={setup} path="sources.inverter.power" label={t['setupSources.pv.power']} signal={inverter.power} filter={POWER_SENSOR} units={POWER_UNITS} entities={entities} readouts={readouts} t={t} lang={lang} />
      <div class="loads-grid">
        <Field label={t['setupSources.pv.sign']} htmlFor={fieldId('sources.inverter.sign')} field="sources.inverter.sign" setup={setup} t={t}>
          <select
            id={fieldId('sources.inverter.sign')}
            value={sign ?? ''}
            aria-invalid={hasError(setup, 'sources.inverter.sign') ? true : undefined}
            onChange={(e) => set('sign', (e.currentTarget as HTMLSelectElement).value || null)}
          >
            <option value="">{t['setupSources.pv.sign.choose']}</option>
            {INVERTER_SIGNS.map((s) => (
              <option key={s} value={s}>
                {t[`setupSources.pv.sign.${s}`]}
              </option>
            ))}
            {sign !== null && !isInverterSign(sign) && <option value={sign}>{sign}</option>}
          </select>
        </Field>
        <Field label={t['setupSources.pv.phase']} htmlFor={fieldId('sources.inverter.phase')} field="sources.inverter.phase" setup={setup} t={t}>
          <select id={fieldId('sources.inverter.phase')} value={inverter.phase ?? ''} onChange={(e) => set('phase', (e.currentTarget as HTMLSelectElement).value || null)}>
            <option value="">{t['setupSources.pv.phase.three']}</option>
            {PHASES.map((p) => (
              <option key={p} value={p}>
                {fill(t['setupSources.pv.phase.single'], { phase: p })}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <div class="loads-field sources-asleep">
        <label class="sources-check" for={fieldId('sources.inverter.unavailableAsZero')}>
          <input
            id={fieldId('sources.inverter.unavailableAsZero')}
            type="checkbox"
            checked={inverter.unavailableAsZero === true}
            onChange={(e) => set('unavailableAsZero', (e.currentTarget as HTMLInputElement).checked ? true : null)}
          />{' '}
          {t['setupSources.pv.asleep']}
        </label>
        <span class="sub">{t['setupSources.pv.asleepHint']}</span>
        <FieldErrors issues={setup.issues} field="sources.inverter.unavailableAsZero" t={t} />
      </div>
      <fieldset class="loads-fieldset" id={fieldId('sources.inverter.phaseCurrents')} tabIndex={-1}>
        <legend>{t['setupSources.pv.currents']}</legend>
        <span class="sub">{t['setupSources.pv.currentsHint']}</span>
        {inverter.phaseCurrents === null ? (
          <button type="button" class="log-button" onClick={() => set('phaseCurrents', { L1: null, L2: null, L3: null })}>
            {t['setupSources.pv.currents.add']}
          </button>
        ) : (
          <>
            {PHASES.map((p) => (
              <SignalRow
                key={p}
                setup={setup}
                path={`sources.inverter.phaseCurrents.${p}`}
                label={fill(t['setupSources.pv.current'], { phase: p })}
                signal={inverter.phaseCurrents![p]}
                filter={CURRENT_SENSOR}
                units={CURRENT_UNITS}
                entities={entities}
                readouts={readouts}
                t={t}
                lang={lang}
              />
            ))}
            <button type="button" class="log-button" onClick={() => set('phaseCurrents', null)}>
              {t['setupSources.pv.currents.remove']}
            </button>
          </>
        )}
      </fieldset>
    </div>
  );
}

/**
 * Step 3 (Sources and storage). Choosing "No PV" keeps the inverter's mapping for this visit, so
 * choosing the inverter again brings it back; a save without it removes it from the file.
 */
export function SetupSources({ setup, t, lang, goTo }: StepProps) {
  const discovery = useDiscovery();
  const inverter = setup.draft.sources.inverter;
  const lastInverter = useRef<Inverter | null>(inverter);
  if (inverter !== null) lastInverter.current = inverter;
  const [focusIndex, setFocusIndex] = useState<number | null>(null);
  const storage = setup.draft.devices.map((device, index) => ({ device, index })).filter(({ device }) => device.type === 'storage');
  const storageIndexes = new Set(storage.map(({ index }) => index));
  const readouts = useReadoutMap([
    inverter?.power?.entity,
    ...PHASES.map((p) => inverter?.phaseCurrents?.[p]?.entity),
    ...storage.flatMap(({ device }) => deviceEntities(device)),
  ]);
  const owns = (issue: SetupIssue) => stepOfField(issue.field) === 'sources' || storageIndexes.has(deviceIndexOf(issue.field) ?? -1);
  const issues = [...setup.issuesOf('sources'), ...setup.issuesOf('loads').filter(owns)].sort((a, b) => (a.severity === b.severity ? 0 : a.severity === 'error' ? -1 : 1));
  const entities = discoveredEntities(discovery.state);
  const context: EditorContext = { entities, readouts, evidence: null, onFinishedSelfTest: () => {} };

  useEffect(() => {
    if (focusIndex === null) return;
    const field = document.getElementById(fieldId(`devices[${focusIndex}].actuator`));
    if (field !== null) {
      field.focus();
      setFocusIndex(null);
    }
  });

  return (
    <section class="card setup-step" aria-labelledby="setup-sources-title" data-step="sources">
      <header class="cardhead">
        <h2 id="setup-sources-title">{t['setupSources.title']}</h2>
        <p class="sub">{t['setupSources.stepLine']}</p>
      </header>
      <p>{t['setupSources.lead']}</p>
      <SetupIssues issues={issues} t={t} />
      <DiscoveryNotice state={discovery.state} refresh={discovery.refresh} t={t} />

      <fieldset class="loads-fieldset sources-pv" id={fieldId('sources.inverter')} tabIndex={-1}>
        <legend>{t['setupSources.pv.title']}</legend>
        <p class="note">{t['setupSources.pv.why']}</p>
        <div class="sources-choices" role="radiogroup" aria-label={t['setupSources.pv.title']}>
          <label class="sources-choice">
            <input type="radio" name="setup-pv" checked={inverter === null} onChange={() => setup.set('sources.inverter', null)} /> {t['setupSources.pv.choice.none']}
          </label>
          <label class="sources-choice">
            <input type="radio" name="setup-pv" checked={inverter !== null} onChange={() => setup.set('sources.inverter', lastInverter.current ?? NO_INVERTER)} />{' '}
            {t['setupSources.pv.choice.inverter']}
          </label>
        </div>
        {inverter !== null && discovery.state.kind !== 'loading' && <InverterForm setup={setup} inverter={inverter} entities={entities} readouts={readouts} t={t} lang={lang} />}
      </fieldset>

      <div class="loads-block sources-storage">
        <h3>{t['setupSources.storage.title']}</h3>
        <p class="sub">{t['setupSources.storage.lead']}</p>
        <p class="note">{t['setupSources.storage.notUsed']}</p>
        {storage.length === 0 && <p class="sub">{t['setupSources.storage.none']}</p>}
        {discovery.state.kind !== 'loading' &&
          storage.map(({ device, index }) => (
            <DeviceEditor key={`${index}/${setup.draft.devices.length}`} setup={setup} index={index} device={device} context={context} t={t} lang={lang} />
          ))}
        <button
          type="button"
          class="log-button"
          onClick={() => {
            setup.addDevice(newDevice({ type: 'storage', id: uniqueDeviceId('battery', setup.draft.devices) }));
            setFocusIndex(setup.draft.devices.length);
          }}
        >
          {t['setupSources.storage.add']}
        </button>
      </div>

      <CheckAndContinue setup={setup} label={t['setupSources.check']} owns={owns} next="loads" goTo={goTo} t={t} />
    </section>
  );
}
