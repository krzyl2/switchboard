import type { Decision } from './api/decisions';
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
import { formatAge, formatNumber, formatQuantity } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { describeDecision, modeLabel, reasonSentence } from './reasons';
import { StateLabel, Timestamp, type Tone } from './ui';
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
      <DecisionsCard {...props} updatedAt={view.updatedAt} />
      <TiersCard {...props} />
    </>
  );
}
