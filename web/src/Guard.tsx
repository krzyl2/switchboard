import { useState } from 'preact/hooks';
import type { Decision, OverrideMode } from './api/decisions';
import { OVERRIDE_MODES } from './api/decisions';
import type { DispatchDevice, DispatchPhase, DispatchStatus, OverrideRefusal } from './api/dispatch';
import type {
  Breaker,
  DeviceGuardState,
  GuardDevice,
  GuardPhase,
  GuardStage,
  GuardStatus,
  PhaseGuardState,
  Tier,
} from './api/guard';
import { formatAge, formatCountdown, formatNumber, formatQuantity } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { describeDecision, levelText, modeLabel, reasonSentence, verificationText } from './reasons';
import { StateLabel, Timestamp, type Tone } from './ui';
import { useDispatch, type DispatchView } from './useDispatch';
import { useGuard } from './useGuard';

// Shedding and safe state are the conditions the user must not miss; hold and reducing are the
// guard working as intended; "no data yet" is neither good nor bad.
const PHASE_TONE: Record<PhaseGuardState, Tone> = {
  unknown: 'muted',
  normal: 'ok',
  hold: 'warn',
  reducing: 'warn',
  shedding: 'crit',
  safe_state: 'crit',
};

const DEVICE_TONE: Record<DeviceGuardState, Tone> = { allowed: 'ok', reduced: 'warn', shed: 'crit', unsafe: 'crit' };

const BUDGET_TONE: Record<GuardPhase['budgetTrend'], Tone> = {
  full: 'ok',
  recovering: 'warn',
  consuming: 'crit',
  held: 'muted',
};

const NO_VALUE = '—';

interface Props {
  t: Messages;
  lang: Lang;
}

const amperes = (value: number, lang: Lang) => formatQuantity(value, 'A', lang);
const multiple = (value: number, lang: Lang) => formatNumber(value, lang, 2);

/** Seconds from `at` to the guard's evaluation; both come from the add-on, so no browser clock is involved. */
function ageAt(at: Date, evaluatedAt: Date): number {
  return (evaluatedAt.getTime() - at.getTime()) / 1000;
}

function tierRange(tiers: Tier[], index: number, { t, lang }: Props): string {
  const upTo = tiers[index]?.upTo ?? null;
  const from = index === 0 ? null : (tiers[index - 1]?.upTo ?? null);
  if (upTo === null) return fill(t.tierAbove, { from: from === null ? '0' : multiple(from, lang) });
  if (from === null) return fill(t.tierUpTo, { upTo: multiple(upTo, lang) });
  return fill(t.tierBetween, { from: multiple(from, lang), upTo: multiple(upTo, lang) });
}

function Banners({ guard, t, lang }: { guard: GuardStatus } & Props) {
  const { killSwitch, safeState, decisionLog, stateFile } = guard;
  const shadow = guard.mode.effective === 'shadow';
  return (
    <>
      {killSwitch.engaged && (
        <div class="card banner" data-tone="crit" role="alert">
          <p>
            <StateLabel tone="crit" label={t.killSwitchTitle} />
          </p>
          <p class="sub">
            {killSwitch.source !== null && t[`killSwitchSource.${killSwitch.source}`]}
            {killSwitch.changedAt !== null && (
              <>
                {killSwitch.source !== null && ', '}
                {t.since} <Timestamp at={killSwitch.changedAt} lang={lang} />
              </>
            )}
          </p>
          <p>{shadow ? t.killSwitchShadow : t.killSwitchActive}</p>
          <p class="sub">{t.killSwitchHow}</p>
        </div>
      )}
      {safeState.active && (
        <div class="card banner" data-tone="crit" role="alert">
          <p>
            <StateLabel
              tone="crit"
              label={
                safeState.scope === 'all'
                  ? t.safeStateAll
                  : fill(t.safeStatePhases, { phases: safeState.phases.join(', ') })
              }
            />
          </p>
          {safeState.since !== null && (
            <p class="sub">
              {t.since} <Timestamp at={safeState.since} lang={lang} />
            </p>
          )}
          {safeState.reason !== null && <p class="sub">{reasonSentence(t, safeState.reason)}</p>}
          <p>
            {safeState.scope === 'all'
              ? shadow
                ? t.safeStateShadowAll
                : t.safeStateActiveAll
              : shadow
                ? t.safeStateShadowPhases
                : t.safeStateActivePhases}
          </p>
        </div>
      )}
      {!decisionLog.writable && (
        <div class="card banner" data-tone="warn" role="status">
          <p>
            <StateLabel tone="warn" label={t.decisionLogUnwritable} />
          </p>
          {decisionLog.lastError !== null && <p class="sub">{decisionLog.lastError}</p>}
          {decisionLog.dropped > 0 && (
            <p class="sub">
              {t.decisionLogDropped}: {decisionLog.dropped}
            </p>
          )}
        </div>
      )}
      {(stateFile.status === 'reset_corrupt' || stateFile.warning !== null) && (
        <div class="card banner" data-tone="warn" role="status">
          <p>
            <StateLabel
              tone="warn"
              label={stateFile.status === 'reset_corrupt' ? t.stateFileReset : t.stateFileWarning}
            />
          </p>
          {stateFile.warning !== null && <p class="sub">{stateFile.warning}</p>}
        </div>
      )}
    </>
  );
}

function breakerSummary(breaker: Breaker, { t, lang }: Props): string {
  return fill(t.breakerSummary, {
    rating: amperes(breaker.ratingA, lang),
    characteristic: breaker.characteristic === 'unknown' ? t.characteristicUnknown : breaker.characteristic,
    continuous: amperes(breaker.continuousA, lang),
    multiple: multiple(breaker.continuousMultiple, lang),
  });
}

function Overview({ guard, ...props }: { guard: GuardStatus } & Props) {
  const { t, lang } = props;
  const { mode, breaker } = guard;
  return (
    <section class="card">
      <h2>{t.guardTitle}</h2>
      <dl class="status">
        <dt>{t.rowMode}</dt>
        <dd>
          <span>{modeLabel(t, mode.effective)}</span>
          <span class="sub">{mode.effective === 'shadow' ? t.modeShadowNote : t.modeActiveNote}</span>
          {mode.note !== 'none' && <span class="sub">{t[`modeNote.${mode.note}`]}</span>}
        </dd>
        <dt>{t.rowBreaker}</dt>
        <dd>{breaker === null ? NO_VALUE : breakerSummary(breaker, props)}</dd>
        <dt>{t.rowEvaluated}</dt>
        <dd>
          <Timestamp at={guard.at} lang={lang} />
        </dd>
      </dl>
      {breaker === null && (
        <p class="issues">
          <StateLabel tone="crit" label={t.noConfiguration} />
        </p>
      )}
    </section>
  );
}

/**
 * Current against the limits, drawn: the dashed reserve threshold, the breaker's rated current and
 * the continuous limit. Decorative only; the same numbers are written out in the legend below it.
 */
function CurrentMeter({ phase, breaker }: { phase: GuardPhase; breaker: Breaker }) {
  const lastFinite = breaker.tiers.at(-2)?.upToA ?? breaker.continuousA;
  const scale = Math.max(lastFinite, breaker.continuousA, Math.abs(phase.currentA ?? 0));
  const at = (value: number) => `${Math.min(100, Math.max(0, (value / scale) * 100))}%`;
  return (
    <div class="meter" aria-hidden="true">
      {phase.currentA !== null && (
        <div class="fill" data-tone={PHASE_TONE[phase.state]} style={{ width: at(Math.abs(phase.currentA)) }} />
      )}
      <div class="tick reserve" style={{ left: at(phase.reserveThresholdA) }} />
      <div class="tick rating" style={{ left: at(breaker.ratingA) }} />
      <div class="tick continuous" style={{ left: at(phase.continuousA) }} />
    </div>
  );
}

function PhaseBlock({ phase, guard, ...props }: { phase: GuardPhase; guard: GuardStatus } & Props) {
  const { t, lang } = props;
  const breaker = guard.breaker;
  const tone = PHASE_TONE[phase.state];
  return (
    <div class="phaseblock" data-phase={phase.phase}>
      <div class="phasehead">
        <span class="mono pname">{phase.phase}</span>
        <StateLabel tone={tone} label={t[`phaseState.${phase.state}`]} />
        <span class="pval">
          <span class="mono">{phase.currentA === null ? NO_VALUE : amperes(phase.currentA, lang)}</span>
          {phase.direction !== null && <span class="sub">{t[`direction.${phase.direction}`]}</span>}
        </span>
      </div>
      {breaker !== null && phase.dataValid && <CurrentMeter phase={phase} breaker={breaker} />}
      <p class="legend">
        <span>
          <i class="key reserve" aria-hidden="true" />
          {t.meterReserve} {amperes(phase.reserveThresholdA, lang)}
        </span>
        {breaker !== null && (
          <span>
            <i class="key rating" aria-hidden="true" />
            {t.meterRating} {amperes(breaker.ratingA, lang)}
          </span>
        )}
        <span>
          <i class="key continuous" aria-hidden="true" />
          {t.meterContinuous} {amperes(phase.continuousA, lang)}
        </span>
      </p>
      <dl class="status facts">
        <dt>{t.colMeterData}</dt>
        <dd>
          <StateLabel
            tone={phase.dataValid ? 'ok' : 'crit'}
            label={phase.dataValid ? t.meterDataValid : t.meterDataInvalid}
          />
          {phase.invalidReason !== null && <span class="sub">{t[`invalidReason.${phase.invalidReason}`]}</span>}
          {phase.reportedAt !== null && (
            <span class="sub">
              {t.balanceAge} {formatAge(ageAt(phase.reportedAt, guard.at), lang)}
            </span>
          )}
        </dd>
        {breaker !== null && phase.band !== null && (
          <>
            <dt>{t.rowTier}</dt>
            <dd>
              <span>
                {tierRange(breaker.tiers, phase.band, props)}
                {phase.multiple !== null && <span class="mono"> ({multiple(phase.multiple, lang)}×)</span>}
              </span>
              {phase.reaction !== null && <span class="sub">{t[`tierReaction.${phase.reaction}`]}</span>}
              {phase.effectiveCurrentA !== null && phase.effectiveCurrentA !== phase.currentA && (
                <span class="sub">{fill(t.effectiveCurrent, { current: amperes(phase.effectiveCurrentA, lang) })}</span>
              )}
            </dd>
          </>
        )}
        <dt>{t.rowBudget}</dt>
        <dd>
          <span class="mono">{formatNumber(phase.budget * 100, lang, 0)}%</span>
          <div class="budgetbar" aria-hidden="true">
            <i data-tone={BUDGET_TONE[phase.budgetTrend]} style={{ width: `${Math.min(100, Math.max(0, phase.budget * 100))}%` }} />
          </div>
          <span class="sub">{t[`budgetTrend.${phase.budgetTrend}`]}</span>
          {phase.recoveryRemainingS > 0 && (
            <span class="sub">{fill(t.budgetRecovery, { time: formatAge(phase.recoveryRemainingS, lang) })}</span>
          )}
        </dd>
        <dt>{t.rowHeadroom}</dt>
        <dd>
          {phase.headroomA === null ? (
            NO_VALUE
          ) : (
            <span class="mono">
              {amperes(phase.headroomA, lang)}
              {phase.headroomW !== null && ` (${formatQuantity(phase.headroomW, 'W', lang)})`}
            </span>
          )}
          <span class="sub">{t.headroomToReserve}</span>
          {phase.breakerHeadroomA !== null && (
            <>
              <span class="mono">{amperes(phase.breakerHeadroomA, lang)}</span>
              <span class="sub">{t.headroomToContinuous}</span>
            </>
          )}
        </dd>
        <dt>{t.rowIncreases}</dt>
        <dd>
          <StateLabel
            tone={phase.increasesAllowed ? 'ok' : 'warn'}
            label={phase.increasesAllowed ? t.increasesAllowed : t.increasesStopped}
          />
          {phase.reductionRequested && <span class="sub">{t.reductionRequested}</span>}
        </dd>
      </dl>
    </div>
  );
}

function PhasesCard({ guard, ...props }: { guard: GuardStatus } & Props) {
  if (guard.phases.length === 0) return null;
  return (
    <section class="card">
      <h2>{props.t.phasesTitle}</h2>
      <div class="phases">
        {guard.phases.map((phase) => (
          <PhaseBlock key={phase.phase} phase={phase} guard={guard} {...props} />
        ))}
      </div>
    </section>
  );
}

function StageState({ stage, staged, t, lang }: { stage: GuardStage; staged: boolean } & Props) {
  let reported: string;
  if (stage.applied !== null) reported = t[`relay.${stage.applied}`];
  else if (stage.setpointA !== null) reported = amperes(stage.setpointA, lang);
  else reported = t.stateUnknown;
  const known = stage.lastKnown;
  return (
    <div class="stage">
      <span>
        {staged && `${t.stage} ${stage.stage}: `}
        <StateLabel tone={stage.fresh ? 'ok' : 'crit'} label={reported} />
      </span>
      {stage.invalidReason !== null && <span class="sub">{t[`deviceStateInvalid.${stage.invalidReason}`]}</span>}
      {!stage.fresh && known !== null && (
        <span class="sub">
          {t.lastKnown}: {known.applied !== null ? t[`relay.${known.applied}`] : known.setpointA !== null ? amperes(known.setpointA, lang) : NO_VALUE}
          , <Timestamp at={known.at} lang={lang} />
        </span>
      )}
      {stage.limit !== null && <span class="sub">{t[`guardTarget.${stage.limit}`]}</span>}
    </div>
  );
}

function DeviceRow({ device, ...props }: { device: GuardDevice } & Props) {
  const { t, lang } = props;
  const staged = device.stages.length > 1;
  return (
    <tr data-device={device.id}>
      <td>
        <span class="mono">{device.id}</span>
        <span class="sub">{t[`deviceType.${device.type}`]}</span>
      </td>
      <td class="mono">{device.phases.join(', ')}</td>
      <td>
        <StateLabel tone={DEVICE_TONE[device.guardState]} label={t[`deviceGuardState.${device.guardState}`]} />
        {device.reason !== null && <span class="sub">{reasonSentence(t, device.reason)}</span>}
        {!device.canSwitchOff && <span class="sub">{t.cannotSwitchOff}</span>}
      </td>
      <td>
        {device.stages.map((stage) => (
          <StageState key={stage.stage} stage={stage} staged={staged} {...props} />
        ))}
      </td>
      <td>
        {device.noShed ? (
          <span>{t.noShed}</span>
        ) : (
          <span class="mono">{device.shedRank ?? NO_VALUE}</span>
        )}
        <span class="sub">{fill(t.valuePerKwh, { value: formatNumber(device.valuePerKwh, lang, 2) })}</span>
        {device.shedPriority !== null && (
          <span class="sub">{fill(t.shedPriority, { value: formatNumber(device.shedPriority, lang, 2) })}</span>
        )}
      </td>
      <td>
        {device.minOnRemainingS <= 0 && device.minOffRemainingS <= 0 && NO_VALUE}
        {device.minOnRemainingS > 0 && (
          <span class="line">{fill(t.lockoutMinOn, { time: formatAge(device.minOnRemainingS, lang) })}</span>
        )}
        {device.minOffRemainingS > 0 && (
          <span class="line">{fill(t.lockoutMinOff, { time: formatAge(device.minOffRemainingS, lang) })}</span>
        )}
      </td>
      <td>
        {device.unsafeSignals.length === 0
          ? NO_VALUE
          : device.unsafeSignals.map((name) => (
              <span key={name} class="mono line">
                {name}
              </span>
            ))}
      </td>
    </tr>
  );
}

function DevicesCard({ guard, ...props }: { guard: GuardStatus } & Props) {
  const { t } = props;
  if (guard.breaker === null && guard.devices.length === 0) return null;
  // Shed order first, as the mockup's "Co system zrzuca najpierw"; a no_shed device goes last.
  const devices = [...guard.devices].sort((a, b) => (a.shedRank ?? Infinity) - (b.shedRank ?? Infinity));
  return (
    <section class="card">
      <h2>{t.devicesTitle}</h2>
      {guard.mode.effective === 'shadow' && <p class="sub">{t.devicesShadow}</p>}
      {devices.length === 0 ? (
        <p>{t.noDevices}</p>
      ) : (
        <div class="tablewrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t.colDevice}</th>
                <th scope="col">{t.colPhase}</th>
                <th scope="col">{t.colLimit}</th>
                <th scope="col">{t.colReported}</th>
                <th scope="col">{t.colShedOrder}</th>
                <th scope="col">{t.colLockout}</th>
                <th scope="col">{t.colUnsafeSignals}</th>
              </tr>
            </thead>
            <tbody>
              {devices.map((device) => (
                <DeviceRow key={device.id} device={device} {...props} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/** Who decided what the device is intended at: a protective hold, dispatch, or a manual override. */
function intendedTone(device: DispatchDevice): Tone {
  if (device.intended === null) return 'muted';
  return device.matches ? 'ok' : 'warn';
}

function DispatchPhases({ dispatch, t, lang }: { dispatch: DispatchStatus } & Props) {
  if (dispatch.phases.length === 0) return null;
  const line = (phase: DispatchPhase) => {
    const parts: string[] = [];
    if (phase.envelopeA !== null) parts.push(fill(t.envelope, { value: amperes(phase.envelopeA, lang) }));
    if (phase.inFlightA !== 0) parts.push(fill(t.inFlight, { value: amperes(phase.inFlightA, lang) }));
    if (phase.foreignW !== null) parts.push(fill(t.foreignLoad, { value: formatQuantity(phase.foreignW, 'W', lang) }));
    if (phase.rank !== null) parts.push(fill(t.phaseRank, { rank: String(phase.rank) }));
    return parts;
  };
  return (
    <>
      <h3>{t.dispatchPhasesTitle}</h3>
      <ul class="dispatchphases">
        {dispatch.phases.map((phase) => (
          <li key={phase.phase} data-dispatch-phase={phase.phase}>
            <span class="mono pname">{phase.phase}</span>
            {!phase.dataValid && (
              <StateLabel
                tone="crit"
                label={phase.invalidReason === null ? t.meterDataInvalid : t[`dispatchInvalid.${phase.invalidReason}`]}
              />
            )}
            {line(phase).map((part) => (
              <span key={part}>{part}</span>
            ))}
            {phase.groups.map((group) => (
              <span key={group.group} class="sub">
                <span class="mono">{group.group}</span>{' '}
                {group.envelopeA === null ? NO_VALUE : fill(t.envelope, { value: amperes(group.envelopeA, lang) })}
              </span>
            ))}
          </li>
        ))}
      </ul>
    </>
  );
}

function deferredLabel(device: DispatchDevice, at: Date, t: Messages): string | null {
  const deferred = device.deferred;
  if (deferred === null) return null;
  const left = deferred.dueAt === null ? 0 : ageAt(at, deferred.dueAt);
  switch (deferred.refusedBy) {
    case 'lockout_min_off':
    case 'lockout_min_on':
      return fill(t[`deferred.${deferred.refusedBy}`], { time: formatCountdown(left) });
    case 'headroom_insufficient':
    case 'guard_budget_low':
      return t[`deferred.${deferred.refusedBy}`];
    default:
      return `${t.deferredOther}: ${reasonSentence(t, deferred.refusedBy)}`;
  }
}

function Waiting({ device, at, t, lang }: { device: DispatchDevice; at: Date } & Props) {
  const deferred = deferredLabel(device, at, t);
  const lockout = device.lockout;
  const nothing =
    device.unresponsive === null &&
    deferred === null &&
    device.settling === 'none' &&
    device.pending === null &&
    device.mismatches === 0 &&
    lockout.minOnRemainingS <= 0 &&
    lockout.minOffRemainingS <= 0;
  if (nothing) return <>{NO_VALUE}</>;
  return (
    <>
      {device.unresponsive !== null && (
        <span class="line">
          <StateLabel tone="crit" label={t.badgeUnresponsive} />
          {device.unresponsive.retryAt !== null && (
            <span class="sub">
              {t.unresponsiveRetry} <Timestamp at={device.unresponsive.retryAt} lang={lang} />
            </span>
          )}
        </span>
      )}
      {device.mismatches > 0 && <span class="sub">{fill(t.mismatches, { count: String(device.mismatches) })}</span>}
      {deferred !== null && (
        <span class="line">
          <StateLabel tone="warn" label={deferred} />
        </span>
      )}
      {device.settling === 'open' && (
        <span class="line">
          <StateLabel tone="muted" label={t.badgeSettlingOpen} />
        </span>
      )}
      {device.settling === 'timed_out' && (
        <span class="line">
          <StateLabel tone="warn" label={t.badgeSettlingTimedOut} />
        </span>
      )}
      {device.pending !== null && (
        <span class="sub">
          {t.pendingIncrease}: {reasonSentence(t, device.pending)}
        </span>
      )}
      {lockout.minOnRemainingS > 0 && (
        <span class="sub">{fill(t.lockoutMinOn, { time: formatAge(lockout.minOnRemainingS, lang) })}</span>
      )}
      {lockout.minOffRemainingS > 0 && (
        <span class="sub">{fill(t.lockoutMinOff, { time: formatAge(lockout.minOffRemainingS, lang) })}</span>
      )}
    </>
  );
}

function OverrideControl({
  device,
  setOverride,
  t,
  lang,
}: { device: DispatchDevice; setOverride: (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null> } & Props) {
  const [saving, setSaving] = useState(false);
  const [refusal, setRefusal] = useState<OverrideRefusal | null>(null);
  const current = device.override;
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
        aria-label={fill(t.overrideLabel, { device: device.id })}
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
          {current.changedAt !== null && (
            <>
              , <Timestamp at={current.changedAt} lang={lang} />
            </>
          )}
        </span>
      )}
    </>
  );
}

function DispatchRow({
  device,
  at,
  setOverride,
  ...props
}: { device: DispatchDevice; at: Date; setOverride: (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null> } & Props) {
  const { t, lang } = props;
  const draw = device.draw;
  const drawParts: string[] = [];
  if (draw !== null && draw.currentA !== null) drawParts.push(amperes(draw.currentA, lang));
  if (draw !== null && draw.powerW !== null) drawParts.push(formatQuantity(draw.powerW, 'W', lang));
  const last = device.history[0];
  return (
    <tr data-dispatch-device={device.id}>
      <td>
        <span class="mono">{device.id}</span>
        <span class="sub">
          {t[`deviceType.${device.type}`]} · <span class="mono">{device.phases.join(', ')}</span>
        </span>
        {device.rotationGroup !== null && (
          <span class="sub">{fill(t.rotationGroup, { group: device.rotationGroup })}</span>
        )}
        {device.circuitGroup !== null && <span class="sub">{fill(t.groupDetail, { group: device.circuitGroup })}</span>}
      </td>
      <td>
        <StateLabel
          tone={intendedTone(device)}
          label={device.intended === null ? t.intendedFollows : levelText(device.intended, t, lang)}
        />
        {!device.matches && <span class="sub">{t.intendedDiffers}</span>}
        {device.origin !== null && !(device.origin === 'override' && device.reason === 'manual_override') && (
          <span class="sub">{t[`origin.${device.origin}`]}</span>
        )}
        {device.reason !== null && <span class="sub">{reasonSentence(t, device.reason)}</span>}
        {device.startRank !== null && <span class="sub">{fill(t.startRank, { rank: String(device.startRank) })}</span>}
      </td>
      <td>
        {device.reported === null ? (
          <StateLabel tone="crit" label={t.stateUnknown} />
        ) : (
          <span>{levelText(device.reported, t, lang)}</span>
        )}
        {draw !== null &&
          (draw.fresh && drawParts.length > 0 ? (
            <span class="sub">{fill(t.drawValue, { value: drawParts.join(', ') })}</span>
          ) : (
            <span class="sub">{t.drawStale}</span>
          ))}
      </td>
      <td>
        <Waiting device={device} at={at} {...props} />
      </td>
      <td>
        <OverrideControl device={device} setOverride={setOverride} {...props} />
      </td>
      <td>
        {last === undefined ? (
          NO_VALUE
        ) : (
          <>
            <span class="line">
              {t[`historyKind.${last.kind}`]}, <Timestamp at={last.at} lang={lang} />
            </span>
            <span class="sub">{verificationText(last, t, lang)}</span>
          </>
        )}
      </td>
    </tr>
  );
}

/**
 * Dispatch's view of every device next to what Home Assistant reports (DISP-04 … DISP-10,
 * SAFE-04, UI-10): a device that is not running keeps its row and its reason. In this version
 * nothing is switched, so "intended" is only what Switchboard would have the device at.
 */
function DispatchCard({
  view,
  setOverride,
  ...props
}: { view: DispatchView; setOverride: (device: string, mode: OverrideMode) => Promise<OverrideRefusal | null> } & Props) {
  const { t, lang } = props;
  if (view.kind === 'loading') {
    return (
      <section class="card">
        <h2>{t.dispatchTitle}</h2>
        <p>{t.dispatchLoading}</p>
      </section>
    );
  }
  if (view.kind === 'unavailable') {
    // Not an alert: protection is shown above; only dispatch's view is missing.
    return (
      <section class="card">
        <h2>{t.dispatchTitle}</h2>
        <div role="status">
          <p class="unavailable">
            <StateLabel tone="crit" label={t.dispatchUnavailable} />
          </p>
          <p class="updated">
            {t.lastSuccessfulUpdate}:{' '}
            {view.lastSuccessAt ? <Timestamp at={view.lastSuccessAt} lang={lang} /> : t.never}
          </p>
        </div>
      </section>
    );
  }
  const { dispatch } = view;
  // Dispatch's start order: the device given headroom first comes first; one it never starts last.
  const devices = [...dispatch.devices].sort((a, b) => (a.startRank ?? Infinity) - (b.startRank ?? Infinity));
  return (
    <section class="card">
      <h2>{t.dispatchTitle}</h2>
      {!dispatch.commandPath && <p class="sub">{t.dispatchShadow}</p>}
      {dispatch.mode.note !== 'none' && <p class="sub">{t[`modeNote.${dispatch.mode.note}`]}</p>}
      <DispatchPhases dispatch={dispatch} {...props} />
      {devices.length === 0 ? (
        <p>{t.noDispatchDevices}</p>
      ) : (
        <div class="tablewrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t.colDevice}</th>
                <th scope="col">{t.colIntended}</th>
                <th scope="col">{t.colReported}</th>
                <th scope="col">{t.colWaiting}</th>
                <th scope="col">{t.colOverride}</th>
                <th scope="col">{t.colLastCheck}</th>
              </tr>
            </thead>
            <tbody>
              {devices.map((device) => (
                <DispatchRow key={device.id} device={device} at={dispatch.at} setOverride={setOverride} {...props} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p class="note">{t.overrideNote}</p>
      <p class="updated">
        {t.lastUpdated}: <Timestamp at={view.updatedAt} lang={lang} />
      </p>
    </section>
  );
}

function localDay(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit' }).format(at);
}

function DecisionRow({ decision, evaluatedAt, ...props }: { decision: Decision; evaluatedAt: Date } & Props) {
  const { t, lang } = props;
  const text = describeDecision(decision, t, lang);
  const day = localDay(decision.at, lang);
  return (
    <tr data-reason={decision.reason}>
      <td class="num">
        <Timestamp at={decision.at} lang={lang} />
        {day !== localDay(evaluatedAt, lang) && <span class="sub">{day}</span>}
      </td>
      <td>
        {decision.scope === 'device' && decision.device !== null ? (
          <>
            <span class="mono">{decision.device}</span>
            {decision.stage !== null && (
              <span class="sub">
                {t.stage} {decision.stage}
              </span>
            )}
            {decision.phase !== null && <span class="sub mono">{decision.phase}</span>}
          </>
        ) : decision.scope === 'phase' && decision.phase !== null ? (
          <span class="mono">{decision.phase}</span>
        ) : decision.scope === 'group' && decision.group !== null ? (
          <>
            <span class="mono">{decision.group}</span>
            {decision.phase !== null && <span class="sub mono">{decision.phase}</span>}
          </>
        ) : (
          <span>{t.subjectSystem}</span>
        )}
      </td>
      <td>
        <span>{text.change}</span>
        {text.command !== null && <span class="sub">{text.command}</span>}
      </td>
      <td>
        <span>{text.reason}</span>
        {text.details.map((detail, i) => (
          <span key={i} class="sub">
            {detail}
          </span>
        ))}
      </td>
    </tr>
  );
}

function DecisionsCard({ guard, updatedAt, ...props }: { guard: GuardStatus; updatedAt: Date } & Props) {
  const { t, lang } = props;
  return (
    <section class="card">
      <h2>{t.decisionsTitle}</h2>
      {guard.decisions.length === 0 ? (
        <p>{t.noDecisions}</p>
      ) : (
        <div class="tablewrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t.colTime}</th>
                <th scope="col">{t.colSubject}</th>
                <th scope="col">{t.colChange}</th>
                <th scope="col">{t.colReason}</th>
              </tr>
            </thead>
            <tbody>
              {guard.decisions.map((decision) => (
                <DecisionRow key={decision.id} decision={decision} evaluatedAt={guard.at} {...props} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p class="updated">
        {t.lastUpdated}: <Timestamp at={updatedAt} lang={lang} />
      </p>
    </section>
  );
}

function TiersCard({ guard, ...props }: { guard: GuardStatus } & Props) {
  const { t, lang } = props;
  const breaker = guard.breaker;
  if (breaker === null) return null;
  const title = breaker.characteristic === 'unknown' ? t.breakerTitle : `${t.breakerTitle} ${breaker.characteristic}${formatNumber(breaker.ratingA, lang, 2)}`;
  return (
    <section class="card">
      <h2>{title}</h2>
      <div class="tablewrap">
        <table>
          <thead>
            <tr>
              <th scope="col">{t.colMultiple}</th>
              <th scope="col">{t.colCurrent}</th>
              <th scope="col">{t.colAllowed}</th>
              <th scope="col">{t.colReaction}</th>
              <th scope="col">{t.colPhasesNow}</th>
            </tr>
          </thead>
          <tbody>
            {breaker.tiers.map((tier, i) => (
              <tr key={i} data-band={i}>
                <td class="mono">{tierRange(breaker.tiers, i, props)}</td>
                <td class="num">{tier.upToA === null ? NO_VALUE : amperes(tier.upToA, lang)}</td>
                <td class="num">{tier.allowedS === null ? t.unlimited : formatAge(tier.allowedS, lang)}</td>
                <td>{t[`tierReaction.${tier.reaction}`]}</td>
                <td class="mono">
                  {guard.phases
                    .filter((p) => p.band === i)
                    .map((p) => p.phase)
                    .join(', ') || NO_VALUE}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p class="note">{fill(t.tiersNote, { continuous: multiple(breaker.continuousMultiple, lang) })}</p>
    </section>
  );
}

export function GuardPage({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = useGuard();
  // Polled with the page, shown only next to a live guard: intentions without protection's picture
  // would read as a calm installation.
  const dispatch = useDispatch();

  if (view.kind === 'loading') {
    return (
      <section class="card">
        <p>{t.guardLoading}</p>
      </section>
    );
  }
  if (view.kind === 'unavailable') {
    return (
      <section class="card">
        <div role="alert">
          <p class="unavailable">
            <StateLabel tone="crit" label={t.guardUnavailable} />
          </p>
          <p class="updated">
            {t.lastSuccessfulUpdate}:{' '}
            {view.lastSuccessAt ? <Timestamp at={view.lastSuccessAt} lang={lang} /> : t.never}
          </p>
        </div>
      </section>
    );
  }
  const props = { guard: view.guard, t, lang };
  return (
    <>
      <Banners {...props} />
      <Overview {...props} />
      <PhasesCard {...props} />
      <DevicesCard {...props} />
      <DispatchCard view={dispatch.view} setOverride={dispatch.setOverride} t={t} lang={lang} />
      <DecisionsCard {...props} updatedAt={view.updatedAt} />
      <TiersCard {...props} />
    </>
  );
}
