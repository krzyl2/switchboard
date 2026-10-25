import { useState } from 'preact/hooks';
import type { Control } from './api/control';
import type { DeviceDraft, SaveOutcome, SetupDraft, SetupIssue, SignalDraft } from './api/setup';
import { ControlBlock, modeKind } from './ControlBlock';
import { formatNumber } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { issueText, SetupIssues } from './SetupIssues';
import { fieldId, stepOfField, type StepName } from './setupDraft';
import { StateBadge, StateLabel, type Tone } from './ui';
import { useControl } from './useControl';
import type { SetupState, StepProps } from './useSetup';
import './setupMode.css';

// Step 5 (Mode) and the finish flow (SAFE-06, UI-05, UI-06, MODE-02). The mode is shown as it is,
// in the control block: the add-on option, the "control allowed" switch, the mode in force and what
// keeps it in shadow, and the observation advice. The wizard records no mode and saving never turns
// control on; the switch is the user's own act, with its confirmation. Finishing needs the
// not-a-protective-device acknowledgement, saves through the add-on (which validates with the
// runtime parser and refuses with the fields to fix), and after a save reads the control state
// again, since a valid configuration may change the mode in force. Nothing here decides validity:
// the add-on is the only judge.

type Acknowledgement = keyof SetupDraft['acknowledgements'];

const TOPOLOGIES = ['three_phase', 'three_phase_summed', 'single_phase'] as const;
const PHASE_ROWS = ['L1', 'L2', 'L3'] as const;
/** The file's device types (onoff) under the runtime screens' labels (on_off). */
const DEVICE_TYPE_LABEL = {
  onoff: 'deviceType.on_off',
  stepped: 'deviceType.stepped',
  modulating: 'deviceType.modulating',
  storage: 'deviceType.storage',
} as const;

/** The mode in force after a save, as the add-on reports it. */
function savedModeLine(t: Messages, control: Control): string {
  const kind = modeKind(control.mode);
  const effective = kind === 'active' ? t.modeActive : kind === 'offs_only' ? t['control.effective.offsOnly'] : t.modeShadow;
  return fill(t['setupMode.saved.mode'], { effective });
}

/** Focuses the input a field names, or the nearest enclosing one that has an input (devices[1].phase → devices[1]). */
function focusField(field: string): void {
  let path = field;
  for (;;) {
    const element = document.getElementById(fieldId(path));
    if (element !== null) {
      element.focus();
      return;
    }
    const cut = Math.max(path.lastIndexOf('.'), path.lastIndexOf('['));
    if (cut <= 0) return;
    path = path.slice(0, cut);
  }
}

/** UI-05: a refusal opens the step that shows its field and focuses it there. */
function openField(field: string, goTo: (step: StepName) => void): void {
  const step = stepOfField(field);
  if (step === 'mode') {
    focusField(field);
    return;
  }
  // The other step renders after the route changes; focus once it has.
  window.addEventListener('hashchange', () => setTimeout(() => focusField(field), 0), { once: true });
  goTo(step);
}

export function SetupMode({ setup, t, lang, goTo }: StepProps) {
  // MODE-02: the add-on option (ceiling), the control switch and the mode in force, as they are.
  const control = useControl();
  const [running, setRunning] = useState(false);
  const [noAnswer, setNoAnswer] = useState(false);
  const [modeResult, setModeResult] = useState<Control | 'failed' | null>(null);

  const view = setup.view;
  const acks = setup.draft.acknowledgements;
  const missing: string[] = [];
  if (!acks.notProtective) missing.push(t['setupMode.finish.needsStatement']);
  if (view?.hasComments === true && !acks.commentsDropped) missing.push(t['setupMode.finish.needsComments']);
  if (view?.unreadable === true && !acks.replaceUnreadable) missing.push(t['setupMode.finish.needsUnreadable']);

  const outcome = setup.outcome;
  const saved = outcome !== null && outcome.kind === 'saved' ? outcome : null;
  const refusal = outcome !== null && outcome.kind !== 'saved' && !noAnswer ? outcome : null;
  // The issues at this step's own fields (the acknowledgements, a removed section) stand next to
  // them; a refusal's issues at other steps are listed in the refusal, each opening its field.
  const own = setup.issues.filter((i) => i.field !== null && stepOfField(i.field) === 'mode');
  const elsewhere = setup.issues.filter((i) => i.severity === 'error' && (i.field === null || stepOfField(i.field) !== 'mode'));

  const finish = async () => {
    setRunning(true);
    setNoAnswer(false);
    setModeResult(null);
    try {
      const result = await setup.save();
      if (result === null) {
        setNoAnswer(true);
        return;
      }
      if (result.kind !== 'saved') return;
      // The saved configuration may change the mode in force (a valid one counts): read it again.
      setModeResult((await control.refresh()) ?? 'failed');
    } finally {
      setRunning(false);
    }
  };

  return (
    <section class="card setup-step" aria-labelledby="setup-mode-title" data-step="mode">
      <header class="cardhead">
        <h2 id="setup-mode-title">{t['setupMode.title']}</h2>
        <p class="sub">{t['setupMode.stepLine']}</p>
      </header>
      <p>{t['setupMode.lead']}</p>

      <ControlBlock state={control} t={t} lang={lang} place="setup" />

      <div class="smode-statement" role="note">
        <p>
          <strong>{t['setupMode.statement.title']}</strong> {t['setupMode.statement.body']}
        </p>
        <Ack name="notProtective" label={t['setupMode.ack.notProtective']} setup={setup} />
      </div>
      {view?.hasComments === true && (
        <div class="smode-statement">
          <p>{t['setupMode.comments.text']}</p>
          <Ack name="commentsDropped" label={t['setupMode.ack.comments']} setup={setup} />
        </div>
      )}
      {view?.unreadable === true && (
        <div class="smode-statement">
          <p>{t['setupMode.unreadable.text']}</p>
          <Ack name="replaceUnreadable" label={t['setupMode.ack.unreadable']} setup={setup} />
        </div>
      )}

      <Review setup={setup} t={t} lang={lang} />

      <div class="smode-finish">
        {refusal !== null && <Refusal issues={elsewhere} conflict={refusal.kind === 'conflict'} setup={setup} t={t} goTo={goTo} />}
        <SetupIssues issues={own} t={t} />
        {noAnswer && (
          <div role="alert">
            <p>
              <StateLabel tone="crit" label={t['setupMode.finish.noAnswer']} />
            </p>
            <button type="button" class="log-button" onClick={setup.reload}>
              {t['setupMode.reload']}
            </button>
          </div>
        )}
        {saved !== null ? (
          <Saved saved={saved} previousRevision={view?.revision ?? null} modeResult={modeResult} running={running} setup={setup} t={t} />
        ) : (
          <>
            {missing.length > 0 && (
              <div id="smode-finish-needs">
                <p class="sub">{t['setupMode.finish.before']}</p>
                <ul class="plainlist">
                  {missing.map((text) => (
                    <li key={text}>{text}</li>
                  ))}
                </ul>
              </div>
            )}
            <button
              type="button"
              class="log-button smode-primary"
              disabled={missing.length > 0 || running || setup.busy !== 'idle'}
              aria-describedby={missing.length > 0 ? 'smode-finish-needs' : undefined}
              onClick={() => void finish()}
            >
              {t['setupMode.finish']}
            </button>
            {running && setup.busy === 'saving' && (
              <p class="sub" role="status">
                {t['setupMode.finish.saving']}
              </p>
            )}
          </>
        )}
      </div>
    </section>
  );
}

/** One required confirmation; its id is the field's, so a refusal at it focuses it (UI-05). */
function Ack({ name, label, setup }: { name: Acknowledgement; label: string; setup: SetupState }) {
  const field = `acknowledgements.${name}`;
  const refused = setup.issuesAt(field).some((i) => i.severity === 'error');
  return (
    <label class="smode-ack">
      <input
        type="checkbox"
        id={fieldId(field)}
        checked={setup.draft.acknowledgements[name]}
        aria-invalid={refused ? 'true' : undefined}
        onChange={(event) => setup.set(field, event.currentTarget.checked)}
      />
      <span>{label}</span>
    </label>
  );
}

/** A refused save (UI-05): nothing was written; every error names its step and opens its field. */
function Refusal({ issues, conflict, setup, t, goTo }: { issues: SetupIssue[]; conflict: boolean; setup: SetupState; t: Messages; goTo: (step: StepName) => void }) {
  return (
    <div class="smode-refusal" role="alert">
      <h3>
        <StateLabel tone="crit" label={t['setupMode.refused.title']} />
      </h3>
      <p class="sub">{t['setupMode.refused.lead']}</p>
      {issues.length > 0 && (
        <ul>
          {issues.map((issue, index) => (
            <li key={index} data-issue={issue.code} data-field={issue.field ?? undefined}>
              {issue.field !== null ? (
                <a
                  href={`#${fieldId(issue.field)}`}
                  onClick={(event) => {
                    // Hash routes: the link must not navigate by itself, only open the step and the field.
                    event.preventDefault();
                    openField(issue.field!, goTo);
                  }}
                >
                  {issueText(issue, t)}
                </a>
              ) : (
                <span>{issueText(issue, t)}</span>
              )}{' '}
              <span class="sub-inline">{issue.field !== null ? t[`setup.step.${stepOfField(issue.field)}`] : t['setupMode.refused.file']}</span>
              {issue.code === 'document_invalid' && <span class="sub">{issue.message}</span>}
              {issue.field === null && issue.documentPath !== null && <span class="sub mono">{issue.documentPath}</span>}
            </li>
          ))}
        </ul>
      )}
      {conflict && (
        <button type="button" class="log-button" onClick={setup.reload}>
          {t['setupMode.reload']}
        </button>
      )}
    </div>
  );
}

/** The saved file, the backup, and whether the running add-on loaded it (hot reload, no restart). */
function Saved({
  saved,
  previousRevision,
  modeResult,
  running,
  setup,
  t,
}: {
  saved: Extract<SaveOutcome, { kind: 'saved' }>;
  previousRevision: string | null;
  modeResult: Control | 'failed' | null;
  running: boolean;
  setup: SetupState;
  t: Messages;
}) {
  const unchanged = previousRevision !== null && saved.revision === previousRevision;
  const [tone, loadText]: [Tone, string] = !saved.reloaded
    ? ['warn', t['setupMode.saved.notConfirmed']]
    : saved.loadedStatus === 'valid'
      ? ['ok', t['setupMode.saved.loaded']]
      : saved.loadedStatus === 'invalid'
        ? ['crit', t['setupMode.saved.loadedInvalid']]
        : saved.loadedStatus === 'missing'
          ? ['crit', t['setupMode.saved.loadedMissing']]
          : ['warn', t['setupMode.saved.notConfirmed']];
  return (
    <div class="smode-saved" role="status" aria-labelledby="smode-saved-title">
      <h3 id="smode-saved-title">
        <StateLabel tone="ok" label={t['setupMode.saved.title']} />
      </h3>
      {unchanged ? (
        <p>{t['setupMode.saved.unchanged']}</p>
      ) : (
        <>
          {saved.backup !== null ? (
            <p>
              {t['setupMode.saved.backup']} <span class="mono">{saved.backup}</span>
            </p>
          ) : (
            <p>{t['setupMode.saved.noBackup']}</p>
          )}
          <p data-reload={saved.reloaded ? (saved.loadedStatus ?? 'unknown') : 'not_confirmed'}>
            <StateLabel tone={tone} label={loadText} />
          </p>
        </>
      )}
      {modeResult !== null && modeResult !== 'failed' && (
        <p data-saved-mode={modeKind(modeResult.mode)}>
          {savedModeLine(t, modeResult)}
          {modeResult.mode.missing.length > 0 && <span class="sub">{t['setupMode.saved.modeMissing']}</span>}
        </p>
      )}
      {modeResult === 'failed' && <p>{t['setupMode.saved.modeFailed']}</p>}
      <p class="sub">{t['setupMode.saved.next']}</p>
      <div class="smode-links">
        <a class="log-button" href="#/">
          {t['nav.dashboard']}
        </a>
        <a class="log-button" href="#/log">
          {t['nav.log']}
        </a>
        <button type="button" class="log-button" disabled={running} onClick={setup.reload}>
          {t['setupMode.saved.again']}
        </button>
      </div>
    </div>
  );
}

/** One mapped entity in monospace, or nothing when the slot is empty. */
function entityOf(signal: SignalDraft | null | undefined): string | null {
  return signal?.entity !== undefined && signal.entity !== null && signal.entity !== '' ? signal.entity : null;
}

/**
 * What a save writes (UI-06), from the draft as it stands: the file it replaces or creates, the
 * connection, the meter, the inverter and every load, which loads leave the file, and which parts
 * of the file stay as written. A preserved part the runtime refuses can be removed here, with the
 * backup keeping it, so an invalid section the wizard does not edit is never a dead end.
 */
function Review({ setup, t, lang }: { setup: SetupState; t: Messages; lang: Lang }) {
  const { draft, view } = setup;
  const n = (value: number, digits = 0) => formatNumber(value, lang, digits);
  const { connection, meter } = draft;
  const topology = connection.topology;
  const reserve = connection.reserveW;
  const inverter = draft.sources.inverter;
  const inverterPower = entityOf(inverter?.power);

  const kept = new Set(draft.devices.map((d) => d.id));
  const removed = (view?.draft.devices ?? []).filter((d) => d.existing && d.id !== null && !kept.has(d.id)).map((d) => d.id!);

  const sectionRefused = (path: string) =>
    setup.issues.some(
      (i) =>
        i.severity === 'error' &&
        i.field === null &&
        i.documentPath !== null &&
        (i.documentPath === path || i.documentPath.startsWith(`${path}.`) || i.documentPath.startsWith(`${path}[`)),
    );
  const setDropped = (path: string, drop: boolean) =>
    setup.set('dropPreserved', drop ? [...draft.dropPreserved.filter((p) => p !== path), path] : draft.dropPreserved.filter((p) => p !== path));

  return (
    <section class="smode-review" aria-labelledby="smode-review-title">
      <h3 id="smode-review-title">{t['setupMode.review.title']}</h3>
      <p>{view === null || view.fileState === 'missing' ? t['setupMode.review.newFile'] : t['setupMode.review.replaceFile']}</p>
      <p class="sub">{t['setupMode.review.reload']}</p>
      <dl>
        <dt>{t['setupMode.review.connection']}</dt>
        <dd data-review="topology">
          {topology === null ? t['setupMode.review.notSet'] : (TOPOLOGIES as readonly string[]).includes(topology) ? t[`topology.${topology as (typeof TOPOLOGIES)[number]}`] : <span class="mono">{topology}</span>}
        </dd>
        <dt>{t['setupMode.review.breaker']}</dt>
        <dd data-review="breaker">
          {connection.breakerRatingA === null ? t['setupMode.review.notSet'] : `${n(connection.breakerRatingA, 1)} A`}
          {connection.characteristic !== null && `, ${t.characteristic} ${connection.characteristic}`}
        </dd>
        <dt>{t['setupMode.review.voltage']}</dt>
        <dd>{connection.voltageV === null ? t['setupMode.review.notSet'] : `${n(connection.voltageV)} V`}</dd>
        <dt>{t['setupMode.review.reserve']}</dt>
        <dd data-review="reserve">
          {reserve === null
            ? t['setupMode.review.default']
            : PHASE_ROWS.map((p) => `${p} ${reserve[p] === null ? t['setupMode.review.default'] : `${n(reserve[p]!)} W`}`).join(' · ')}
        </dd>
        <dt>{t['setupMode.review.meter']}</dt>
        <dd data-review="meter">
          <MeterRows meter={meter} t={t} />
        </dd>
        <dt>{t['setupMode.review.inverter']}</dt>
        <dd data-review="inverter">
          {inverterPower === null ? (
            t['setupMode.review.noInverter']
          ) : (
            <>
              <span class="mono">{inverterPower}</span>
              {inverter?.unavailableAsZero === true && <span class="sub">{t['setupMode.review.asZero']}</span>}
            </>
          )}
        </dd>
        <dt>{t['setupMode.review.devices']}</dt>
        <dd data-review="devices">
          {draft.devices.length === 0 ? (
            t['setupMode.review.noDevices']
          ) : (
            <ul class="plainlist">
              {draft.devices.map((device, index) => (
                <DeviceRow key={device.id ?? `new-${index}`} device={device} t={t} />
              ))}
            </ul>
          )}
          {removed.length > 0 && (
            <p class="smode-removed" data-removed={removed.join(' ')}>
              <StateLabel tone="warn" label={fill(t['setupMode.review.removed'], { devices: removed.join(', ') })} />
            </p>
          )}
        </dd>
        {view !== null && view.preserved.length > 0 && (
          <>
            <dt>{t['setupMode.review.preserved']}</dt>
            <dd data-review="preserved">
              <ul class="plainlist">
                {view.preserved.map((section) => {
                  const dropped = draft.dropPreserved.includes(section.path);
                  const refusedHere = sectionRefused(section.path);
                  return (
                    <li key={section.path} data-path={section.path}>
                      <span class="mono">{section.path}</span> <span class="sub-inline">{section.summary}</span>
                      {(refusedHere || dropped) && (
                        <>
                          {refusedHere && <span class="sub">{t['setupMode.review.invalidSection']}</span>}
                          <label class="smode-ack">
                            <input type="checkbox" checked={dropped} onChange={(event) => setDropped(section.path, event.currentTarget.checked)} />
                            <span>{t['setupMode.review.drop']}</span>
                          </label>
                        </>
                      )}
                    </li>
                  );
                })}
              </ul>
            </dd>
          </>
        )}
      </dl>
      <p class="sub">{t['setupMode.review.notWritten']}</p>
    </section>
  );
}

type SignalLabel = 'setupMode.review.current' | 'setupMode.review.power' | 'setupMode.review.voltageSignal';

function MeterRows({ meter, t }: { meter: SetupDraft['meter']; t: Messages }) {
  const rows: { label: string; current: string | null; power: string | null; voltage: string | null }[] = PHASE_ROWS.map((phase) => {
    const row = meter.phases[phase];
    return { label: phase, current: entityOf(row?.current), power: entityOf(row?.power), voltage: entityOf(row?.voltage) };
  });
  rows.push({ label: t['setupMode.review.total'], current: entityOf(meter.total.current), power: entityOf(meter.total.power), voltage: null });
  const mapped = rows.filter((r) => r.current !== null || r.power !== null || r.voltage !== null);
  if (mapped.length === 0) return <>{t['setupMode.review.notSet']}</>;
  const parts = (r: (typeof rows)[number]) => {
    const all: [SignalLabel, string | null][] = [
      ['setupMode.review.current', r.current],
      ['setupMode.review.power', r.power],
      ['setupMode.review.voltageSignal', r.voltage],
    ];
    return all.filter((part): part is [SignalLabel, string] => part[1] !== null);
  };
  return (
    <ul class="plainlist">
      {mapped.map((r) => (
        <li key={r.label}>
          <strong>{r.label}</strong>{' '}
          {parts(r).map(([key, entity], index) => (
            <span key={key}>
              {index > 0 && ' · '}
              {t[key]} <span class="mono">{entity}</span>
            </span>
          ))}
        </li>
      ))}
    </ul>
  );
}

function DeviceRow({ device, t }: { device: DeviceDraft; t: Messages }) {
  const typeKey = device.type !== null && device.type in DEVICE_TYPE_LABEL ? DEVICE_TYPE_LABEL[device.type as keyof typeof DEVICE_TYPE_LABEL] : null;
  const [tone, badge]: [Tone, string] = !device.existing
    ? ['ok', t['setupMode.review.device.new']]
    : device.editable
      ? ['muted', t['setupMode.review.device.existing']]
      : ['muted', t['setupMode.review.device.kept']];
  return (
    <li data-device={device.id ?? undefined}>
      <strong>{device.name ?? device.id ?? t['setupMode.review.notSet']}</strong>
      {device.name !== null && device.id !== null && <span class="mono"> {device.id}</span>}{' '}
      <StateBadge tone={tone} label={badge} />
      <span class="sub">
        {typeKey !== null ? t[typeKey] : (device.type ?? t['setupMode.review.notSet'])}
        {' · '}
        {device.phase === 'three_phase' ? t['setupMode.review.allPhases'] : (device.phase ?? t['setupMode.review.notSet'])}
        {device.actuator !== null && (
          <>
            {' · '}
            <span class="mono">{device.actuator}</span>
          </>
        )}
      </span>
    </li>
  );
}
