import type { ComponentChildren } from 'preact';
import { useState } from 'preact/hooks';
import { OVERRIDE_MODES } from './api/decisions';
import type { OverrideMode, Verdict } from './api/decisions';
import type { DeviceConfig } from './api/devices';
import type { DispatchDevice, DispatchStatus, HistoryEntry, OverrideRefusal } from './api/dispatch';
import type { Economics, EconomicsDevice } from './api/economics';
import type { Power } from './api/power';
import { economicsDevice, priceText, SourcesText } from './Economics';
import { GoalRow } from './GoalView';
import { formatAmps, formatCountdown, formatKw, formatNumber, roundTo } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { levelText, reasonSentence } from './reasons';
import { Age, PollStatus, StateBadge, StateLabel, type Tone } from './ui';
import { useDevices } from './useDevices';
import { useEconomics } from './useEconomics';
import { useDispatch, type DispatchView } from './useDispatch';
import type { PollView } from './usePoll';
import { usePlan, type PlanControl } from './usePlan';
import { usePower } from './usePower';
import './DeviceCard.css';

// Screen 04 — Device card (UI-12, docs/mockups "Karta urządzenia"), route #/devices/<id> (#/devices
// opens the first device). Two columns, connection and energy value, then dispatch's state of the
// device with the manual override, then the command verification history (DISP-07). Everything is
// read-only except the override; devices are switched only while control is in force.

interface Props {
  t: Messages;
  lang: Lang;
}

type SetOverride = (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null>;

const NO_VALUE = '—';

// A mismatch is the diagnostic this table exists for ("why isn't my car charging"); a timeout is a
// lost sample; the rest says nothing about the device.
const VERDICT_TONE: Record<Verdict, Tone> = {
  match: 'ok',
  mismatch: 'warn',
  inconclusive: 'muted',
  not_applied: 'muted',
  unverifiable: 'muted',
  timed_out: 'warn',
  not_delivered: 'muted',
};

/** Records that carry no measurement at all: the add-on writes 0 for both values. */
const NOTHING_MEASURED: ReadonlySet<Verdict> = new Set<Verdict>(['not_applied', 'timed_out', 'unverifiable', 'not_delivered']);

const label = (device: DeviceConfig) => device.name ?? device.id;

/** A setpoint device (modulating, storage) is verified by its new ceiling against its own draw. */
const isSetpoint = (device: DeviceConfig) => device.type === 'modulating' || device.type === 'storage';

// Local wall-clock time only here, at the presentation edge.
function clock(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
}

function localDay(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit' }).format(at);
}

/** A configured duration, exact: "90 s", "6 min", "2 min 30 s" (a truncated age would misstate it). */
function duration(seconds: number, lang: Lang): string {
  if (seconds < 120) return `${formatNumber(seconds, lang, 1)} s`;
  const minutes = Math.floor(seconds / 60);
  const rest = Math.round(seconds - minutes * 60);
  return rest === 0 ? `${formatNumber(minutes, lang, 0)} min` : `${formatNumber(minutes, lang, 0)} min ${formatNumber(rest, lang, 0)} s`;
}

/** A change with its sign: "+1.70 kW", "-1.65 kW", "+7.0 A" (rounded first, so never "-0.00"). */
function signed(value: number, unit: 'A' | 'W', lang: Lang): string {
  const [shown, digits, suffix] = unit === 'W' ? [value / 1000, 2, 'kW'] : [value, 1, 'A'];
  const rounded = roundTo(shown, digits);
  const text = new Intl.NumberFormat(lang, { minimumFractionDigits: digits, maximumFractionDigits: digits, signDisplay: 'exceptZero' }).format(rounded === 0 ? 0 : rounded);
  return `${text} ${suffix}`;
}

function phasesText(phases: readonly string[], t: Messages): string {
  return phases.length === 3 ? `${t['device.threePhase']} (${phases.join(', ')})` : phases.join(', ');
}

/**
 * The expected and measured columns of one history record. For a switched device both are changes
 * (Δ) with their sign; for a setpoint device the expected value is the new ceiling and the measured
 * one the draw. A dash where nothing was measured: not applied (shadow), timed out and unverifiable
 * records carry 0 for both, and an inconclusive record without a fresh sample carries 0 as its
 * measured value (and, for a setpoint, as its ceiling). A placeholder 0 must never read as a
 * measured 0 (the add-on's contract cannot tell them apart; a ceiling of 0 is never inconclusive).
 */
export function measurement(entry: HistoryEntry, setpoint: boolean, lang: Lang, t: Messages): { expected: string; measured: string } {
  if (NOTHING_MEASURED.has(entry.verdict)) return { expected: NO_VALUE, measured: NO_VALUE };
  const unit = entry.unit === 'amperes' ? 'A' : 'W';
  const inconclusive = entry.verdict === 'inconclusive';
  if (setpoint && unit === 'A') {
    if (inconclusive && entry.expected === 0 && entry.measured === 0) return { expected: NO_VALUE, measured: NO_VALUE };
    return { expected: fill(t['device.history.ceiling'], { value: formatAmps(entry.expected, lang) }), measured: formatAmps(entry.measured, lang) };
  }
  if (inconclusive && entry.measured === 0) {
    return { expected: entry.expected === 0 ? NO_VALUE : signed(entry.expected, unit, lang), measured: NO_VALUE };
  }
  return { expected: signed(entry.expected, unit, lang), measured: signed(entry.measured, unit, lang) };
}

function Field({ name, children }: { name: string; children: ComponentChildren }) {
  return (
    <div class="dc-field">
      <dt>{name}</dt>
      <dd>{children}</dd>
    </div>
  );
}

function Entity({ id }: { id: string }) {
  return <code class="entity mono">{id}</code>;
}

function Limits({ device, t, lang }: { device: DeviceConfig } & Props) {
  const range = device.range;
  if (range !== null) {
    return (
      <>
        <Field name={t['device.minCurrent']}>
          <span class="mono">{formatAmps(range.minA, lang)}</span>
        </Field>
        <Field name={t['device.maxCurrent']}>
          <span class="mono">{formatAmps(range.maxA, lang)}</span>
          <span class="sub">{fill(t['device.rangeDetail'], { step: formatAmps(range.stepA, lang), unit: range.unit })}</span>
        </Field>
      </>
    );
  }
  return (
    <Field name={t['device.rated']}>
      {device.stages.map((stage) => (
        <span key={stage.stage} class="line">
          {device.stages.length > 1 && <>{fill(t['device.stage'], { stage: String(stage.stage) })}: </>}
          <span class="mono">{formatAmps(stage.ratedCurrentA, lang)}</span>
          {' · '}
          {stage.ratedPowerW === null ? <span class="sub-inline">{t['device.ratedPowerMissing']}</span> : <span class="mono">{formatKw(stage.ratedPowerW, lang)}</span>}
        </span>
      ))}
    </Field>
  );
}

function Connection({ device, ...props }: { device: DeviceConfig } & Props) {
  const { t, lang } = props;
  const draw = device.draw;
  return (
    <section class="card" aria-labelledby="device-connection-title">
      <h2 id="device-connection-title">{t['device.connection.title']}</h2>
      <dl class="dc-fields">
        <Field name={t['device.actuator']}>
          {device.stages.map((stage) => (
            <span key={stage.stage} class="line" data-stage={stage.stage}>
              <Entity id={stage.actuator} />
              {device.stages.length > 1 && (
                <span class="sub-inline">
                  {' '}
                  {fill(t['device.stage'], { stage: String(stage.stage) })} · {stage.phases.join(', ')}
                </span>
              )}
              {stage.stateEntity !== stage.actuator && (
                <span class="sub">
                  {t['device.stateEntity']} <Entity id={stage.stateEntity} />
                </span>
              )}
            </span>
          ))}
          <span class="sub">{t['device.actuatorNote']}</span>
        </Field>
        {(device.stopActuator !== null || isSetpoint(device)) && (
          <Field name={t['device.stopActuator']}>
            {device.stopActuator === null ? <span class="sub-inline">{t['device.noStopActuator']}</span> : <Entity id={device.stopActuator} />}
          </Field>
        )}
        <Field name={t['device.phase']}>
          <span class="mono">{phasesText(device.phases, t)}</span>
        </Field>
        <Limits device={device} {...props} />
        <Field name={t['device.reactionTime']}>
          <span class="mono">{duration(device.reactionTimeS, lang)}</span>
          <span class="sub">{t['device.reactionTimeHint']}</span>
        </Field>
        <Field name={t['device.lockouts']}>
          <span class="line">{fill(t['device.minOn'], { time: duration(device.minOnS, lang) })}</span>
          <span class="line">{fill(t['device.minOff'], { time: duration(device.minOffS, lang) })}</span>
        </Field>
        <Field name={t['device.stateReports']}>
          {device.stateMaxAgeS === null
            ? t['device.stateReports.on_change']
            : fill(t['device.stateReports.age'], { age: duration(device.stateMaxAgeS, lang) })}
        </Field>
        <Field name={t['device.draw']}>
          {draw === null ? (
            <span class="sub-inline">{isSetpoint(device) ? t['device.noDraw.setpoint'] : t['device.noDraw.relay']}</span>
          ) : (
            <>
              {draw.current.map((e) => (
                <span key={`current-${e.phase}`} class="line">
                  {t['device.drawCurrent']} {e.phase}: <Entity id={e.entity} />
                </span>
              ))}
              {draw.power.map((e) => (
                <span key={`power-${e.phase}`} class="line">
                  {t['device.drawPower']} {e.phase}: <Entity id={e.entity} />
                </span>
              ))}
              <span class="sub">{fill(t['device.drawMaxAge'], { age: duration(draw.maxAgeS, lang) })}</span>
            </>
          )}
        </Field>
        {(device.rotationGroup !== null || device.circuitGroup !== null) && (
          <Field name={t['device.groups']}>
            {device.rotationGroup !== null && (
              <span class="line">
                {t['device.rotationGroup']} <Entity id={device.rotationGroup} />
              </span>
            )}
            {device.circuitGroup !== null && (
              <span class="line">
                {t['device.circuitGroup']} <Entity id={device.circuitGroup} />
              </span>
            )}
          </Field>
        )}
      </dl>
      <p class="note">{t['device.connection.readOnly']}</p>
    </section>
  );
}

/** The value per kWh now, with what it follows and why (UI-12, ECO-13): only with an economics section, which gives the label and the prices. */
function ValueNow({ eco, currency, t, lang }: { eco: EconomicsDevice; currency: string } & Props) {
  const factor = formatNumber(eco.valueFactor, lang, 3);
  return (
    <>
      <span class="line" data-value="static">
        {t['device.eco.valueStatic']}: <span class="mono">{priceText(t, lang, eco.valuePerKwh, currency)}</span>
      </span>
      <span class="line" data-value="now">
        {t['device.eco.valueNow']}: <span class="mono">{priceText(t, lang, eco.vEff, currency)}</span>
      </span>
      {eco.valueFollows !== 'static' && <span class="sub">{fill(t[eco.valueFollows === 'import_price' ? 'device.eco.followsImport' : 'device.eco.followsExport'], { factor })}</span>}
      <span class="sub" data-value="basis">
        {t[`device.eco.valueBasis.${eco.valueBasis}`]}
      </span>
    </>
  );
}

/** The sources in words, what was declared when it differs (a dropped zone), or that none is declared. */
function SourcesNow({ eco, t }: { eco: EconomicsDevice; t: Messages }) {
  if (eco.sources === null) return <span class="sub-inline" data-sources="undeclared">{t['device.eco.sources.undeclared']}</span>;
  return (
    <>
      <span class="line" data-sources={eco.sources}>
        <SourcesText sources={eco.sources} t={t} />
      </span>
      {eco.sourcesDeclared !== null && eco.sourcesDeclared !== eco.sources && (
        <span class="sub">
          {t['device.eco.sources.declared']} <span class="mono">{eco.sourcesDeclared}</span>. {t['device.eco.sources.narrowed']}
        </span>
      )}
    </>
  );
}

/** Which energy class is open to the device in dispatch's last step, and the reason it gave (the same sentence the state card shows). */
function AllowedNow({ eco, economics, state, t }: { eco: EconomicsDevice; economics: Economics; state: DispatchDevice | null; t: Messages }) {
  const zone = economics.tariff?.zone ?? null;
  return (
    <>
      <span class="line" data-allowed={eco.energyClass ?? 'unknown'}>
        {eco.energyClass === null ? (
          t['device.eco.class.unknown']
        ) : eco.energyClass === 'zone' && zone !== null ? (
          <>
            {t['device.eco.class.zone']} <span class="mono">{zone}</span>
          </>
        ) : (
          t[`device.eco.class.${eco.energyClass}`]
        )}
      </span>
      {state !== null && state.reason !== null && <span class="sub">{reasonSentence(t, state.reason)}</span>}
      <span class="sub">{t['device.eco.allowedNote']}</span>
    </>
  );
}

function Economics({ device, economics, state, plan, t, lang }: { device: DeviceConfig; economics: Economics | null; state: DispatchDevice | null; plan: PlanControl } & Props) {
  const eco = economicsDevice(economics, device.id);
  // Without an economics: section nothing prices a kWh: the 0.9 words stay, and say so.
  const configured = economics !== null && economics.configured && economics.currency !== null && eco !== null;
  return (
    <section class="card" aria-labelledby="device-economics-title">
      <h2 id="device-economics-title">{t['device.economics.title']}</h2>
      <dl class="dc-fields">
        <Field name={t['device.value']}>
          {configured ? (
            <ValueNow eco={eco} currency={economics.currency!} t={t} lang={lang} />
          ) : (
            <span class="line">{fill(t['device.valueAmount'], { value: formatNumber(device.valuePerKwh, lang, 2) })}</span>
          )}
          <span class="sub">{t[configured ? 'device.eco.valueHint' : 'device.valueHint']}</span>
          {device.shedPriority !== null && <span class="sub">{fill(t['device.shedPriority'], { value: formatNumber(device.shedPriority, lang, 2) })}</span>}
          {device.noShed && <span class="sub">{t['device.noShed']}</span>}
        </Field>
        <Field name={t['device.goal']}>
          <GoalRow device={device.id} control={plan} t={t} lang={lang} />
        </Field>
        <Field name={t['device.sources']}>
          {configured ? (
            <SourcesNow eco={eco} t={t} />
          ) : (
            <span class="sub-inline" data-unavailable="sources">
              {t['device.sourcesNotAvailable']}
            </span>
          )}
        </Field>
        {configured && eco.sources !== null && (
          <Field name={t['device.eco.allowed']}>
            <AllowedNow eco={eco} economics={economics} state={state} t={t} />
          </Field>
        )}
      </dl>
    </section>
  );
}

function PowerNow({ device, state, power, t, lang }: { device: DeviceConfig; state: DispatchDevice; power: PollView<Power> } & Props) {
  const draw = state.draw;
  const drawLine =
    draw === null ? null : draw.fresh && draw.currentA !== null ? (
      <span class="sub">{fill(t['device.drawCurrentNow'], { value: formatAmps(draw.currentA, lang) })}</span>
    ) : !draw.fresh ? (
      <span class="sub">{t['device.drawStale']}</span>
    ) : null;
  if (power.kind === 'loading') return <>{t['common.loading']}</>;
  if (power.kind === 'unavailable') {
    return (
      <>
        <StateLabel
          tone="crit"
          label={power.lastSuccessAt === null ? t['device.powerUnavailableNever'] : fill(t['device.powerUnavailable'], { time: clock(power.lastSuccessAt, lang) })}
        />
        {drawLine}
      </>
    );
  }
  const entry = power.data.devices.find((d) => d.id === device.id);
  if (entry === undefined) return <>{NO_VALUE}</>;
  // The oldest measured input is what the figure is as old as; a rated value has no report time.
  const reported = entry.phases.map((p) => p.reportedAt).filter((at): at is Date => at !== null);
  const oldest = reported.length === 0 ? null : new Date(Math.min(...reported.map((at) => at.getTime())));
  return (
    <>
      {entry.totalW === null ? (
        <span class="unknown">{t['device.powerUnknown']}</span>
      ) : (
        <span class="line">
          <span class="mono">{formatKw(entry.totalW, lang)}</span> · {t[`source.${entry.source}`]} <Age reportedAt={oldest} now={power.data.at} lang={lang} t={t} />
        </span>
      )}
      {drawLine}
    </>
  );
}

function deferredLines(state: DispatchDevice, at: Date, t: Messages, lang: Lang): string[] {
  const deferred = state.deferred;
  if (deferred === null) return [];
  const when = deferred.dueAt === null ? t['device.deferredRecheck'] : fill(t['device.deferredDue'], { time: formatCountdown((deferred.dueAt.getTime() - at.getTime()) / 1000) });
  return [`${fill(t['device.deferredTo'], { level: levelText(deferred.target, t, lang) })}: ${reasonSentence(t, deferred.refusedBy)} (${when})`, fill(t['device.deferredWanted'], { reason: reasonSentence(t, deferred.reason) })];
}

/** What keeps the device from its intention: not responding, a deferred change, an open window, a waiting increase, lockouts. */
function Waiting({ state, at, t, lang }: { state: DispatchDevice; at: Date } & Props) {
  const lockout = state.lockout;
  const deferred = deferredLines(state, at, t, lang);
  const items = [
    state.unresponsive !== null && (
      <span key="unresponsive" class="line">
        <StateLabel tone="crit" label={fill(t['device.unresponsiveSince'], { time: clock(state.unresponsive.since, lang) })} />
        {state.unresponsive.retryAt !== null && <span class="sub">{fill(t['device.retryAt'], { time: clock(state.unresponsive.retryAt, lang) })}</span>}
      </span>
    ),
    state.mismatches > 0 && (
      <span key="mismatches" class="sub">
        {fill(t['device.mismatches'], { count: String(state.mismatches) })}
      </span>
    ),
    deferred.length > 0 && (
      <span key="deferred" class="line">
        <StateLabel tone="warn" label={deferred[0]!} />
        <span class="sub">{deferred[1]}</span>
      </span>
    ),
    state.settling !== 'none' && (
      <span key="settling" class="line">
        <StateLabel tone={state.settling === 'open' ? 'muted' : 'warn'} label={t[`device.settling.${state.settling}`]} />
      </span>
    ),
    state.pending !== null && (
      <span key="pending" class="line">
        {fill(t['device.pending'], { reason: reasonSentence(t, state.pending) })}
      </span>
    ),
    lockout.minOnRemainingS > 0 && (
      <span key="min-on" class="sub">
        {fill(t['device.lockoutMinOn'], { time: formatCountdown(lockout.minOnRemainingS) })}
      </span>
    ),
    lockout.minOffRemainingS > 0 && (
      <span key="min-off" class="sub">
        {fill(t['device.lockoutMinOff'], { time: formatCountdown(lockout.minOffRemainingS) })}
      </span>
    ),
  ].filter(Boolean);
  return items.length === 0 ? <>{t['device.nothingWaiting']}</> : <>{items}</>;
}

/** SAFE-04 from the card: the answer is dispatch's status after the add-on evaluated the change. */
function OverrideControl({ device, state, setOverride, t, lang }: { device: DeviceConfig; state: DispatchDevice; setOverride: SetOverride } & Props) {
  const [saving, setSaving] = useState(false);
  const [refusal, setRefusal] = useState<OverrideRefusal | null>(null);
  const current = state.override;
  const change = async (mode: OverrideMode) => {
    setSaving(true);
    setRefusal(null);
    const result = await setOverride(device.id, mode);
    setSaving(false);
    setRefusal(result);
  };
  return (
    <>
      <select
        aria-label={fill(t['device.overrideLabel'], { device: label(device) })}
        value={current.mode}
        disabled={saving}
        onChange={(event) => void change((event.currentTarget as HTMLSelectElement).value as OverrideMode)}
      >
        {OVERRIDE_MODES.map((mode) => (
          <option key={mode} value={mode}>
            {t[`override.${mode}`]}
          </option>
        ))}
      </select>
      {saving && <span class="sub">{t.overrideSaving}</span>}
      {refusal !== null && (
        <span class="sub" role="status">
          {t[`overrideRefused.${refusal}`]}
        </span>
      )}
      {current.source !== null && (
        <span class="sub">
          {t[`overrideSource.${current.source}`]}
          {current.changedAt !== null && `, ${clock(current.changedAt, lang)}`}
        </span>
      )}
      <span class="sub">{t.overrideNote}</span>
    </>
  );
}

function asPoll(view: DispatchView): PollView<DispatchStatus> {
  return view.kind === 'live' ? { kind: 'live', data: view.dispatch, updatedAt: view.updatedAt } : view;
}

function StateCard({
  device,
  view,
  state,
  power,
  setOverride,
  ...props
}: { device: DeviceConfig; view: DispatchView; state: DispatchDevice | null; power: PollView<Power>; setOverride: SetOverride } & Props) {
  const { t, lang } = props;
  const status = view.kind === 'live' ? view.dispatch : null;
  return (
    <section class="card" aria-labelledby="device-state-title">
      <header class="cardhead">
        <h2 id="device-state-title">{t['device.state.title']}</h2>
        {state !== null && state.unresponsive !== null && <StateBadge tone="crit" label={t['device.badge.unresponsive']} />}
        {state !== null && state.override.mode !== 'auto' && (
          <StateBadge tone="warn" label={fill(t['device.badge.override'], { mode: t[`override.${state.override.mode}`] })} />
        )}
      </header>
      <PollStatus view={asPoll(view)} lang={lang} t={t} />
      {status !== null && (
        <>
          {!status.commandPath && <p class="sub">{t['device.state.shadow']}</p>}
          {status.mode.note !== 'none' && <p class="sub">{t[`modeNote.${status.mode.note}`]}</p>}
          {state === null ? (
            <p>{t['device.state.none']}</p>
          ) : (
            <dl class="dc-fields dc-state">
              <Field name={t['device.intended']}>
                <StateLabel
                  tone={state.intended === null ? 'muted' : state.matches ? 'ok' : 'warn'}
                  label={state.intended === null ? t['device.intendedFollows'] : levelText(state.intended, t, lang)}
                />
                {!state.matches && <span class="sub">{t['device.wouldChange']}</span>}
              </Field>
              <Field name={t['device.reported']}>
                {state.reported === null ? <StateLabel tone="crit" label={t['device.reportedUnknown']} /> : levelText(state.reported, t, lang)}
              </Field>
              <Field name={t['device.powerNow']}>
                <PowerNow device={device} state={state} power={power} {...props} />
              </Field>
              <Field name={t['device.why']}>
                {state.reason === null ? NO_VALUE : reasonSentence(t, state.reason)}
                {state.origin !== null && !(state.origin === 'override' && state.reason === 'manual_override') && (
                  <span class="sub">{t[`origin.${state.origin}`]}</span>
                )}
              </Field>
              <Field name={t['device.startOrder']}>
                {state.startRank === null ? t['device.notRanked'] : fill(t['device.startRank'], { rank: String(state.startRank) })}
              </Field>
              <Field name={t['device.waiting']}>
                <Waiting state={state} at={status.at} {...props} />
              </Field>
              <Field name={t['device.override']}>
                <OverrideControl key={device.id} device={device} state={state} setOverride={setOverride} {...props} />
              </Field>
            </dl>
          )}
          <p class="updated">
            <time dateTime={status.at.toISOString()}>{fill(t['device.state.evaluatedAt'], { time: clock(status.at, lang) })}</time>
          </p>
        </>
      )}
    </section>
  );
}

function HistoryRow({ entry, setpoint, at, t, lang }: { entry: HistoryEntry; setpoint: boolean; at: Date } & Props) {
  // The time of the decision (or of the switching seen); the check comes a reaction time later.
  const when = entry.commandedAt ?? entry.at;
  const values = measurement(entry, setpoint, lang, t);
  return (
    <tr data-kind={entry.kind} data-verdict={entry.verdict}>
      <td class="mono">
        <time dateTime={when.toISOString()}>{clock(when, lang)}</time>
        {localDay(when, lang) !== localDay(at, lang) && <span class="sub">{localDay(when, lang)}</span>}
        {entry.commandedAt !== null && <span class="sub">{fill(t['device.history.checkedAt'], { time: clock(entry.at, lang) })}</span>}
      </td>
      <td>
        <span class="line">{entry.from !== null && entry.to !== null ? `${levelText(entry.from, t, lang)} → ${levelText(entry.to, t, lang)}` : NO_VALUE}</span>
        <span class="sub">{t[`device.history.kind.${entry.kind}`]}</span>
      </td>
      <td class="num">{values.expected}</td>
      <td class="num">{values.measured}</td>
      <td>
        <StateBadge tone={VERDICT_TONE[entry.verdict]} label={t[`verdict.${entry.verdict}`]} />
      </td>
    </tr>
  );
}

/** The command verification table (UI-12, DISP-07): the main diagnostic for "why isn't it running". */
function HistoryCard({ device, view, state, ...props }: { device: DeviceConfig; view: DispatchView; state: DispatchDevice | null } & Props) {
  const { t, lang } = props;
  const history = state?.history ?? [];
  return (
    <section class="card" aria-labelledby="device-history-title">
      <header class="cardhead">
        <h2 id="device-history-title">{t['device.history.title']}</h2>
        {history.length > 0 && <p class="sub">{fill(t['device.history.count'], { count: String(history.length) })}</p>}
      </header>
      {view.kind === 'loading' && <p class="updated">{t['common.loading']}</p>}
      {view.kind === 'unavailable' && (
        // The state card above carries the alert; this one only says why the table is gone.
        <p class="updated unavailable">
          <StateLabel tone="crit" label={view.lastSuccessAt === null ? t['poll.unavailableNever'] : fill(t['poll.unavailable'], { time: clock(view.lastSuccessAt, lang) })} />
        </p>
      )}
      {view.kind === 'live' && state !== null && history.length === 0 && <p>{t['device.history.empty']}</p>}
      {view.kind === 'live' && history.length > 0 && (
        <div class="tablewrap dc-history">
          <table>
            <thead>
              <tr>
                <th scope="col">{t['device.history.time']}</th>
                <th scope="col">{t['device.history.command']}</th>
                <th scope="col">{t['device.history.expected']}</th>
                <th scope="col">{t['device.history.measured']}</th>
                <th scope="col">{t['device.history.verdict']}</th>
              </tr>
            </thead>
            <tbody>
              {history.map((entry, i) => (
                <HistoryRow key={`${entry.at.getTime()}-${i}`} entry={entry} setpoint={isSetpoint(device)} at={view.dispatch.at} {...props} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p class="note">{t['device.history.note']}</p>
      <p class="note">{t['device.history.unresponsiveNote']}</p>
    </section>
  );
}

/** The parts that need dispatch and the power picture: polled only while a declared device is on screen. */
function DeviceDetail({ device, ...props }: { device: DeviceConfig } & Props) {
  const dispatch = useDispatch();
  const power = usePower();
  const economics = useEconomics();
  const plan = usePlan();
  const state = dispatch.view.kind === 'live' ? (dispatch.view.dispatch.devices.find((d) => d.id === device.id) ?? null) : null;
  return (
    <>
      <div class="dc-grid">
        <Connection device={device} {...props} />
        <Economics device={device} economics={economics.kind === 'live' ? economics.data : null} state={state} plan={plan} {...props} />
      </div>
      <StateCard device={device} view={dispatch.view} state={state} power={power} setOverride={dispatch.setOverride} {...props} />
      <HistoryCard device={device} view={dispatch.view} state={state} {...props} />
    </>
  );
}

export function DeviceCard({ lang, id }: { lang: Lang; id: string | null }) {
  const t = messages[lang];
  const view = useDevices();
  const live = view.kind === 'live' ? view.data : null;
  const devices = live?.devices ?? [];
  const device = devices.find((d) => d.id === id) ?? (id === null ? devices[0] : undefined);
  return (
    <>
      <section class="card" aria-labelledby="device-title">
        <header class="cardhead">
          <h2 id="device-title">{device === undefined ? t['device.title'] : label(device)}</h2>
          {device !== undefined && device.name !== null && <span class="sub-inline mono">{device.id}</span>}
          {device !== undefined && (
            <span class="badge" data-tone="muted">
              {phasesText(device.phases, t)} · {t[`deviceType.${device.type}`]}
            </span>
          )}
          <p class="sub">{t['device.lead']}</p>
        </header>
        <PollStatus view={view} lang={lang} t={t} />
        {live !== null && live.configuration !== 'valid' && <p>{t[`device.configuration.${live.configuration}`]}</p>}
        {live !== null && live.configuration === 'valid' && devices.length === 0 && <p>{t['device.noDevices']}</p>}
        {live !== null && devices.length > 0 && device === undefined && id !== null && <p role="alert">{fill(t['device.unknown'], { id })}</p>}
        {devices.length > 0 && (
          <nav class="devicelist" aria-label={t['nav.device']}>
            {devices.map((d) => (
              <a key={d.id} href={`#/devices/${encodeURIComponent(d.id)}`} aria-current={d.id === device?.id ? 'page' : undefined}>
                {label(d)}
              </a>
            ))}
          </nav>
        )}
      </section>
      {device !== undefined && <DeviceDetail device={device} t={t} lang={lang} />}
    </>
  );
}
