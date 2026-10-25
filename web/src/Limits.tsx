import { useState } from 'preact/hooks';
import type { Excursion, ExcursionDecision, Excursions, PhaseExcursions } from './api/excursions';
import type { DeviceConfig, Devices } from './api/devices';
import { parseGuard } from './api/guard';
import type { Breaker, BudgetTrend, DeviceGuardState, GuardDevice, GuardPhase, GuardStatus, PhaseGuardState, Tier } from './api/guard';
import { MARKER_RADIUS, REDUCING_ACTIONS, excursionChart, type ExcursionGeometry } from './excursionChart';
import { formatAge, formatAmps, formatFixed, formatInterval, formatNumber, formatQuantity } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { localClock, localDay } from './localTime';
import { modeLabel, reasonSentence } from './reasons';
import { Age, PollStatus, StateLabel, Timestamp, type Tone } from './ui';
import { useDevices } from './useDevices';
import { useExcursions } from './useExcursions';
import { useGuard } from './useGuard';
import type { PollView } from './usePoll';
import './limits.css';

// Screen 05 — Limits and overloads (BRK-01 … BRK-05, DISP-02, SAFE-03), route #/guard. Structure and
// vocabulary follow docs/mockups "Limity i przeciążenia": the tier table of this breaker in amperes
// and kilowatts, the overload budget per phase, a real recorded excursion as a chart, the shed order,
// and the kill switch. Per-device dispatch detail lives on the dashboard queue and the device card.

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

const BUDGET_TONE: Record<BudgetTrend, Tone> = { full: 'ok', recovering: 'warn', consuming: 'crit', held: 'muted' };

const NO_VALUE = '—';

const KILL_SWITCH_URL = 'api/guard/kill-switch';

/**
 * The add-on answers a kill switch change only after the guard runner evaluated it, waiting up to its
 * own Guard:ApiEvaluationTimeout (2 s); longer than that, so a late evaluation is reported as such.
 */
const KILL_SWITCH_REQUEST_TIMEOUT_MS = 5_000;

interface Props {
  t: Messages;
  lang: Lang;
}

type KillSwitchRefusal = 'late' | 'failed';

class KillSwitchError extends Error {
  constructor(readonly refusal: KillSwitchRefusal) {
    super(`Kill switch change refused: ${refusal}.`);
  }
}

/**
 * SAFE-03 from the page. A JSON body, as the add-on requires (a cross-site form cannot send one
 * without a CORS preflight). The answer is the guard's view after it evaluated the change; 503 is an
 * evaluation that came late (the kill switch holds in memory either way). It changes the add-on's
 * own state only: no Home Assistant entity is commanded by it.
 */
async function postKillSwitch(engaged: boolean, signal: AbortSignal): Promise<GuardStatus> {
  const response = await fetch(KILL_SWITCH_URL, {
    method: 'POST',
    signal,
    cache: 'no-store',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ engaged }),
  });
  if (!response.ok) throw new KillSwitchError(response.status === 503 ? 'late' : 'failed');
  return parseGuard(await response.json());
}

const multiple = (value: number, lang: Lang) => formatNumber(value, lang, 2);

/**
 * A current with one decimal, rounded to whole milliamperes first: the add-on sends 1.13 × 25 A as
 * 28.249999999999996, which must read 28.3 A like the tier table's 28.25, not 28.2.
 */
function amps(value: number, lang: Lang): string {
  return formatAmps(Math.round(value * 1000) / 1000, lang);
}

/** Kilowatts with two decimals, rounded in whole tens of watts so 5175 W reads 5.18 kW as in the mockup. */
function kilowatts(watts: number, lang: Lang): string {
  return `${formatFixed(Math.round(watts / 10) / 100, lang, 2)} kW`;
}

function tierRange(tiers: Tier[], index: number, { t, lang }: Props): string {
  const upTo = tiers[index]?.upTo ?? null;
  const from = index === 0 ? null : (tiers[index - 1]?.upTo ?? null);
  if (upTo === null) return fill(t.tierAbove, { from: from === null ? '0' : multiple(from, lang) });
  if (from === null) return fill(t.tierUpTo, { upTo: multiple(upTo, lang) });
  return fill(t.tierBetween, { from: multiple(from, lang), upTo: multiple(upTo, lang) });
}

/** A device's display name (name: from switchboard.yaml), else its id. */
type Names = (id: string) => string;

function namesOf(view: PollView<Devices>): Names {
  const names = new Map(view.kind === 'live' ? view.data.devices.map((d) => [d.id, d.name ?? d.id]) : []);
  return (id) => names.get(id) ?? id;
}

function dayLabel(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit' }).format(at);
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
        </div>
      )}
      {safeState.active && (
        <div class="card banner" data-tone="crit" role="alert">
          <p>
            <StateLabel tone="crit" label={safeState.scope === 'all' ? t.safeStateAll : fill(t.safeStatePhases, { phases: safeState.phases.join(', ') })} />
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
            <StateLabel tone="warn" label={stateFile.status === 'reset_corrupt' ? t.stateFileReset : t.stateFileWarning} />
          </p>
          {stateFile.warning !== null && <p class="sub">{stateFile.warning}</p>}
        </div>
      )}
    </>
  );
}

function breakerSummary(breaker: Breaker, { t, lang }: Props): string {
  return fill(t.breakerSummary, {
    rating: formatQuantity(breaker.ratingA, 'A', lang),
    characteristic: breaker.characteristic === 'unknown' ? t.characteristicUnknown : breaker.characteristic,
    continuous: formatQuantity(breaker.continuousA, 'A', lang),
    multiple: multiple(breaker.continuousMultiple, lang),
  });
}

function Overview({ guard, ...props }: { guard: GuardStatus } & Props) {
  const { t, lang } = props;
  const { mode, breaker } = guard;
  return (
    <>
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
      <p class="note">
        <a class="lim-link" href="#/log">
          {t['limits.decisionLog']}
        </a>
      </p>
    </>
  );
}

/** BRK-01: the tier table of the configured breaker, in amperes and kilowatts for this installation. */
function TiersCard({ guard, breaker, ...props }: { guard: GuardStatus; breaker: Breaker } & Props) {
  const { t, lang } = props;
  const title = breaker.characteristic === 'unknown' ? t.breakerTitle : `${t.breakerTitle} ${breaker.characteristic}${formatNumber(breaker.ratingA, lang, 2)}`;
  return (
    <section class="card" aria-labelledby="tiers-title">
      <header class="cardhead">
        <h2 id="tiers-title">{title}</h2>
        <p class="sub">{t['limits.tiers.lead']}</p>
      </header>
      <div class="tablewrap">
        <table>
          <thead>
            <tr>
              <th scope="col">{t.colMultiple}</th>
              <th scope="col">{t.colCurrent}</th>
              <th scope="col">{t['limits.tiers.colPower']}</th>
              <th scope="col">{t.colAllowed}</th>
              <th scope="col">{t.colReaction}</th>
              <th scope="col">{t.colPhasesNow}</th>
            </tr>
          </thead>
          <tbody>
            {breaker.tiers.map((tier, i) => {
              const now = guard.phases.filter((p) => p.band === i).map((p) => p.phase);
              return (
                <tr key={i} data-band={i} data-current={now.length > 0 ? 'true' : undefined}>
                  <td class="mono">{tierRange(breaker.tiers, i, props)}</td>
                  <td class="num">{tier.upToA === null ? NO_VALUE : amps(tier.upToA, lang)}</td>
                  <td class="num">{tier.upToW === null ? NO_VALUE : kilowatts(tier.upToW, lang)}</td>
                  <td class="num">{tier.allowedS === null ? t.unlimited : formatAge(tier.allowedS, lang)}</td>
                  <td>{t[`tierReaction.${tier.reaction}`]}</td>
                  <td class="mono">{now.join(', ') || NO_VALUE}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p class="note">{fill(t.tiersNote, { continuous: multiple(breaker.continuousMultiple, lang) })}</p>
      <p class="note">
        {t['limits.tiers.powerNote']} {t['limits.tiers.readOnly']}
      </p>
    </section>
  );
}

/** The phase's latest record above the reserve threshold, for the budget's "last used" line. */
function lastExcursion(view: PollView<Excursions>, phase: GuardPhase['phase']): Excursion | null {
  if (view.kind !== 'live') return null;
  return view.data.phases.find((p) => p.phase === phase)?.last ?? null;
}

/** BRK-02 / BRK-04: per phase the budget, its trend and the time to full, next to the phase's state. */
function BudgetCard({ guard, breaker, excursions, ...props }: { guard: GuardStatus; breaker: Breaker; excursions: PollView<Excursions> } & Props) {
  const { t, lang } = props;
  return (
    <section class="card lim-budget" aria-labelledby="budget-title">
      <header class="cardhead">
        <h2 id="budget-title">{t.rowBudget}</h2>
        <p class="sub">
          {fill(t['limits.budget.lead'], {
            step: formatInterval(breaker.budgetRecoveryTimeS / 100, lang),
            continuous: amps(breaker.continuousA, lang),
            time: formatAge(breaker.budgetRecoveryTimeS, lang),
          })}
        </p>
      </header>
      {guard.phases.map((phase) => {
        const percent = Math.min(100, Math.max(0, phase.budget * 100));
        const last = lastExcursion(excursions, phase.phase);
        return (
          <div key={phase.phase} class="lim-budgetrow" data-phase={phase.phase}>
            <div class="lim-budgethead">
              <span class="mono pname">{phase.phase}</span>
              <StateLabel tone={PHASE_TONE[phase.state]} label={t[`phaseState.${phase.state}`]} />
              {phase.currentA !== null && (
                <span class="mono">
                  {amps(phase.currentA, lang)}
                  {phase.multiple !== null && ` (${multiple(phase.multiple, lang)}×)`}
                </span>
              )}
              {phase.direction !== null && <span class="sub-inline">{t[`direction.${phase.direction}`]}</span>}
              <Age reportedAt={phase.reportedAt} now={guard.at} lang={lang} t={t} template="age.reported" />
              <span class="mono lim-pct">{formatNumber(percent, lang, 0)}%</span>
            </div>
            <div class="budgetbar" aria-hidden="true">
              <i data-tone={BUDGET_TONE[phase.budgetTrend]} style={{ width: `${percent}%` }} />
            </div>
            <p class="sub">
              {t[`budgetTrend.${phase.budgetTrend}`]}
              {phase.recoveryRemainingS > 0 && ` · ${fill(t.budgetRecovery, { time: formatAge(phase.recoveryRemainingS, lang) })}`}
            </p>
            {phase.invalidReason !== null && <p class="sub">{t[`invalidReason.${phase.invalidReason}`]}</p>}
            {phase.reductionRequested && <p class="sub">{t.reductionRequested}</p>}
            {last !== null && (
              <p class="sub">{fill(t['limits.budget.lastExcursion'], { time: localClock(last.start, lang), peak: amps(last.peakA, lang) })}</p>
            )}
          </div>
        );
      })}
    </section>
  );
}

interface ExcursionChoice {
  key: string;
  phase: PhaseExcursions['phase'];
  which: 'worst' | 'last';
  excursion: Excursion;
}

function choicesOf(excursions: Excursions): ExcursionChoice[] {
  return excursions.phases.flatMap((p) => {
    const choices: ExcursionChoice[] = [];
    if (p.worst !== null) choices.push({ key: `${p.phase}:worst`, phase: p.phase, which: 'worst', excursion: p.worst });
    // The most recent one is offered only when it is another excursion than the worst one.
    if (p.last !== null && (p.worst === null || p.last.start.getTime() !== p.worst.start.getTime())) {
      choices.push({ key: `${p.phase}:last`, phase: p.phase, which: 'last', excursion: p.last });
    }
    return choices;
  });
}

/** The default chart: the worst excursion of any phase, by its peak against the breaker rating. */
function defaultChoice(choices: ExcursionChoice[]): ExcursionChoice | undefined {
  const worst = choices.filter((c) => c.which === 'worst');
  return [...worst].sort((a, b) => b.excursion.peakA / b.excursion.ratingA - a.excursion.peakA / a.excursion.ratingA)[0] ?? choices[0];
}

/** What a decision during an excursion did, in words; a reduction names its device. */
function decisionText(decision: ExcursionDecision, names: Names, t: Messages): string {
  if (REDUCING_ACTIONS.has(decision.action) && decision.device !== null) {
    const action = decision.action as 'switch_off' | 'reduce_to_minimum' | 'set_setpoint' | 'deferred';
    return fill(t[`limits.${decision.commanded ? 'did' : 'would'}.${action}`], { device: names(decision.device) });
  }
  return t[`action.${decision.action}`];
}

function decisionReason(decision: ExcursionDecision, t: Messages): string {
  return decision.action === 'foreign_load_detected' ? t['reason.foreign_load'] : reasonSentence(t, decision.reason);
}

/** Current over time on one phase with the three limits and the decisions marked (docs/05 "a real recorded excursion"). */
function ExcursionChart({ excursion, g, phase, names, t, lang }: { excursion: Excursion; g: ExcursionGeometry; phase: string; names: Names } & Props) {
  const { box } = g;
  // The shed moment is when a reduction takes effect; a deferral (a reduction waiting for its
  // lockout) is annotated only when nothing took effect during the excursion.
  const reducing =
    g.markers.find((m) => REDUCING_ACTIONS.has(m.decision.action) && m.decision.action !== 'deferred') ??
    g.markers.find((m) => m.decision.action === 'deferred');
  const labelOf: Record<'reserve' | 'breaker' | 'continuous', 'bar.reserve' | 'bar.breaker' | 'bar.continuous'> = {
    reserve: 'bar.reserve',
    breaker: 'bar.breaker',
    continuous: 'bar.continuous',
  };
  const aria = fill(t['limits.excursion.aria'], {
    phase,
    from: localClock(g.from, lang),
    to: localClock(g.to, lang),
    peak: amps(excursion.peakA, lang),
    peakAt: localClock(excursion.peakAt, lang),
    reserve: amps(excursion.reserveThresholdA, lang),
    breaker: amps(excursion.ratingA, lang),
    continuous: amps(excursion.continuousA, lang),
  });
  const middle = (box.left + box.right) / 2;
  return (
    <svg class="lim-chart" viewBox={`0 0 ${box.width} ${box.height}`} role="img" aria-label={aria}>
      <g class="axis">
        {g.yTicks.map((tick) => (
          <g key={tick.value}>
            <line class="gridline" x1={box.left} x2={box.right} y1={tick.y} y2={tick.y} />
            <text x={box.left - 3} y={tick.y + 3} text-anchor="end">
              {formatNumber(tick.value, lang, 1)}
            </text>
          </g>
        ))}
        {g.xTicks.map((tick, i) => (
          <text key={i} x={tick.x} y={box.bottom + 12} text-anchor={i === 0 ? 'start' : i === g.xTicks.length - 1 ? 'end' : 'middle'}>
            {localClock(tick.at, lang)}
          </text>
        ))}
        <text x={box.left - 3} y={box.top - 3} text-anchor="end">
          A
        </text>
      </g>
      {g.mirrored.map((line) => (
        <line key={`m-${line.kind}`} class={`limit ${line.kind}`} data-mirrored="true" x1={box.left} x2={box.right} y1={line.y} y2={line.y} />
      ))}
      {g.limits.map((line) => (
        <g key={line.kind} data-limit={line.kind}>
          <line class={`limit ${line.kind}`} x1={box.left} x2={box.right} y1={line.y} y2={line.y} />
          <text class={`label ${line.kind}`} x={box.right} y={line.labelY} text-anchor="end">
            {fill(t[labelOf[line.kind]], { value: amps(line.value, lang) })}
          </text>
        </g>
      ))}
      <path class="trace" d={g.path} />
      {g.markers.map((m) => (
        <g key={m.decision.id} class="mark" data-kind={REDUCING_ACTIONS.has(m.decision.action) ? 'reduce' : 'event'}>
          {m.markY !== m.y && <line class="leader" x1={m.x} x2={m.x} y1={m.markY + MARKER_RADIUS} y2={m.y} />}
          <circle cx={m.x} cy={m.markY} r={MARKER_RADIUS} />
          <text x={m.x} y={m.markY + 2.3}>
            {m.number}
          </text>
        </g>
      ))}
      {reducing !== undefined && (
        // Two short lines beside the marker, on the side with more room, so the label stays inside
        // the chart at any width; the list below the chart words the decision in full.
        <text
          class="annot"
          x={reducing.x > middle ? reducing.x - MARKER_RADIUS - 3 : reducing.x + MARKER_RADIUS + 3}
          y={Math.min(box.bottom - 12, Math.max(box.top + 8, reducing.markY - 2))}
          text-anchor={reducing.x > middle ? 'end' : 'start'}
        >
          <tspan>{t[reducing.decision.commanded ? 'limits.chart.shed' : 'limits.chart.wouldShed']}</tspan>
          <tspan x={reducing.x > middle ? reducing.x - MARKER_RADIUS - 3 : reducing.x + MARKER_RADIUS + 3} dy="9">
            {reducing.decision.device === null ? '' : names(reducing.decision.device)}
          </tspan>
        </text>
      )}
    </svg>
  );
}

function ExcursionDetail({ choice, at, names, t, lang }: { choice: ExcursionChoice; at: Date; names: Names } & Props) {
  const { excursion, phase } = choice;
  const end = excursion.end ?? at;
  const duration = formatAge((end.getTime() - excursion.start.getTime()) / 1000, lang);
  const lastSample = excursion.samples.at(-1);
  const summary = excursion.active
    ? fill(t['limits.excursion.summaryOngoing'], {
        peak: amps(excursion.peakA, lang),
        time: localClock(excursion.peakAt, lang),
        duration,
        age: formatAge(lastSample === undefined ? 0 : Math.max(0, (at.getTime() - lastSample.at.getTime()) / 1000), lang),
      })
    : fill(t['limits.excursion.summary'], { peak: amps(excursion.peakA, lang), time: localClock(excursion.peakAt, lang), duration });
  const g = excursionChart(excursion);
  const markers = g.markers;
  const wouldOnly = excursion.decisions.some((d) => REDUCING_ACTIONS.has(d.action) && !d.commanded);
  return (
    <>
      <ExcursionChart excursion={excursion} g={g} phase={phase} names={names} t={t} lang={lang} />
      <p class="lim-summary">{summary}</p>
      {wouldOnly && <p class="sub">{t['limits.excursion.shadow']}</p>}
      {excursion.decimated && <p class="sub">{t['limits.excursion.decimated']}</p>}
      <h3>{t['limits.excursion.decisions']}</h3>
      {markers.length === 0 ? (
        <p class="sub">{t['limits.excursion.noDecisions']}</p>
      ) : (
        <ol class="lim-marks">
          {markers.map((m) => (
            <li key={m.decision.id} data-action={m.decision.action}>
              <span class="lim-marknum mono" data-kind={REDUCING_ACTIONS.has(m.decision.action) ? 'reduce' : 'event'} aria-hidden="true">
                {m.number}
              </span>
              <span>
                <Timestamp at={m.decision.at} lang={lang} /> {decisionText(m.decision, names, t)}
                <span class="sub">
                  {decisionReason(m.decision, t)} · {fill(t['limits.excursion.decisionAt'], { current: amps(m.current, lang) })}
                </span>
              </span>
            </li>
          ))}
        </ol>
      )}
    </>
  );
}

/** BRK-04 on screen: a real recorded excursion, the worst within the retention window by default. */
function ExcursionCard({ view, names, t, lang }: { view: PollView<Excursions>; names: Names } & Props) {
  const [selected, setSelected] = useState<string | null>(null);
  const choices = view.kind === 'live' ? choicesOf(view.data) : [];
  const choice = choices.find((c) => c.key === selected) ?? defaultChoice(choices);
  const at = view.kind === 'live' ? view.data.at : null;
  let span: string | null = null;
  if (choice !== undefined && at !== null) {
    const { excursion } = choice;
    const day = localDay(excursion.start).from.getTime() === localDay(at).from.getTime() ? '' : `${dayLabel(excursion.start, lang)}, `;
    span =
      day +
      (excursion.end === null
        ? fill(t['limits.excursion.ongoing'], { phase: choice.phase, from: localClock(excursion.start, lang) })
        : fill(t['limits.excursion.span'], { phase: choice.phase, from: localClock(excursion.start, lang), to: localClock(excursion.end, lang) }));
  }
  const file = view.kind === 'live' ? view.data.stateFile : null;
  return (
    <section class="card" aria-labelledby="excursion-title">
      <header class="cardhead">
        <h2 id="excursion-title">{t['limits.excursion.title']}</h2>
        {span !== null && <p class="sub">{span}</p>}
      </header>
      <PollStatus view={view} lang={lang} t={t} />
      {file !== null && file.status === 'reset_corrupt' && (
        <p class="sub" role="status">
          <StateLabel tone="warn" label={t['limits.file.reset']} />
          {file.warning !== null && <span class="sub">{file.warning}</span>}
        </p>
      )}
      {file !== null && file.writeError !== null && (
        <p class="sub" role="status">
          <StateLabel tone="warn" label={fill(t['limits.file.writeError'], { error: file.writeError })} />
        </p>
      )}
      {view.kind === 'live' && choice === undefined && <p>{t['limits.excursion.none']}</p>}
      {choices.length > 1 && choice !== undefined && (
        <label class="lim-choose">
          <span class="sub-inline">{t['limits.excursion.choose']}</span>
          <select value={choice.key} onChange={(event) => setSelected((event.currentTarget as HTMLSelectElement).value)}>
            {choices.map((c) => (
              <option key={c.key} value={c.key}>
                {fill(t[c.which === 'worst' ? 'limits.excursion.worst' : 'limits.excursion.last'], { phase: c.phase, peak: amps(c.excursion.peakA, lang) })}
              </option>
            ))}
          </select>
        </label>
      )}
      {choice !== undefined && at !== null && <ExcursionDetail choice={choice} at={at} names={names} t={t} lang={lang} />}
    </section>
  );
}

interface ShedRow {
  device: GuardDevice;
  /** Position among the steps that relieve something; null for a row that is no step. */
  order: number | null;
  step: string;
  first: boolean;
}

/**
 * The shed order of 04 §2 and DISP-02 as steps: devices by shed rank; a stepped device's stages
 * highest first; a modulating device reduced to its minimum, then stopped only through a stop
 * actuator; a storage device's charge stopped. A no_shed device is listed last, never as a step.
 */
function shedRows(guard: GuardStatus, config: (id: string) => DeviceConfig | undefined, { t, lang }: Props): ShedRow[] {
  const devices = [...guard.devices].sort((a, b) => (a.shedRank ?? Infinity) - (b.shedRank ?? Infinity));
  let order = 0;
  return devices.flatMap((device) => {
    if (device.noShed) return [{ device, order: null, step: t.noShed, first: true }];
    const steps: { step: string; isStep: boolean }[] = [];
    switch (device.type) {
      case 'on_off':
        steps.push({ step: t['limits.shed.switchOff'], isStep: true });
        break;
      case 'stepped':
        for (const stage of [...device.stages].sort((a, b) => b.stage - a.stage)) {
          steps.push({ step: fill(t['limits.shed.stageOff'], { stage: String(stage.stage) }), isStep: true });
        }
        break;
      case 'modulating': {
        const range = config(device.id)?.range ?? null;
        steps.push({ step: range === null ? t['limits.shed.reduceToMinimum'] : fill(t['limits.shed.reduceTo'], { value: formatQuantity(range.minA, 'A', lang) }), isStep: true });
        steps.push(device.canSwitchOff ? { step: t['limits.shed.stop'], isStep: true } : { step: t.cannotSwitchOff, isStep: false });
        break;
      }
      case 'storage':
        steps.push({ step: t['limits.shed.stopCharging'], isStep: true });
        break;
    }
    return steps.map((s, i) => ({ device, order: s.isStep ? ++order : null, step: s.step, first: i === 0 }));
  });
}

/** DISP-02: what is shed first, with each device's value per kWh and how fast its draw changes. */
function ShedOrderCard({ guard, devices, names, ...props }: { guard: GuardStatus; devices: PollView<Devices>; names: Names } & Props) {
  const { t, lang } = props;
  const configs = new Map(devices.kind === 'live' ? devices.data.devices.map((d) => [d.id, d]) : []);
  const config = (id: string) => configs.get(id);
  const rows = shedRows(guard, config, props);
  return (
    <section class="card" aria-labelledby="shed-title">
      <header class="cardhead">
        <h2 id="shed-title">{t['limits.shed.title']}</h2>
        <p class="sub">{t['limits.shed.lead']}</p>
      </header>
      {guard.mode.effective === 'shadow' && <p class="sub">{t.devicesShadow}</p>}
      {rows.length === 0 ? (
        <p>{t.noDevices}</p>
      ) : (
        <div class="tablewrap">
          <table>
            <thead>
              <tr>
                <th scope="col">{t['limits.shed.colOrder']}</th>
                <th scope="col">{t.colDevice}</th>
                <th scope="col">{t.colPhase}</th>
                <th scope="col">{t['limits.shed.colStep']}</th>
                <th scope="col">{t['limits.shed.colValue']}</th>
                <th scope="col">{t['limits.shed.colSpeed']}</th>
                <th scope="col">{t['limits.shed.colNow']}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => {
                const { device } = row;
                const reaction = config(device.id)?.reactionTimeS;
                return (
                  <tr key={i} data-shed-device={device.id} data-order={row.order ?? undefined}>
                    <td class="mono">{row.order ?? NO_VALUE}</td>
                    <td>
                      <span class="lim-name">{names(device.id)}</span>
                      {names(device.id) !== device.id && <span class="sub mono">{device.id}</span>}
                    </td>
                    <td class="mono">{device.phases.join(', ')}</td>
                    <td>{row.step}</td>
                    <td class="num">
                      {formatNumber(device.valuePerKwh, lang, 2)}
                      {device.shedPriority !== null && <span class="sub">{fill(t.shedPriority, { value: formatNumber(device.shedPriority, lang, 2) })}</span>}
                    </td>
                    <td class="num">{reaction === undefined ? NO_VALUE : formatInterval(reaction, lang)}</td>
                    <td>
                      {row.first && (
                        <>
                          <StateLabel tone={DEVICE_TONE[device.guardState]} label={t[`deviceGuardState.${device.guardState}`]} />
                          {device.reason !== null && <span class="sub">{reasonSentence(t, device.reason)}</span>}
                        </>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <p class="note">{t['limits.shed.notes']}</p>
    </section>
  );
}

/** SAFE-03: the kill switch's state, where it was set, and the control to engage or release it. */
function KillSwitchCard({ guard, onAnswer, t, lang }: { guard: GuardStatus; onAnswer: (guard: GuardStatus) => void } & Props) {
  const [saving, setSaving] = useState(false);
  const [refusal, setRefusal] = useState<KillSwitchRefusal | null>(null);
  const { killSwitch } = guard;
  const change = async () => {
    setSaving(true);
    setRefusal(null);
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), KILL_SWITCH_REQUEST_TIMEOUT_MS);
    try {
      onAnswer(await postKillSwitch(!killSwitch.engaged, controller.signal));
    } catch (error) {
      setRefusal(error instanceof KillSwitchError ? error.refusal : 'failed');
    } finally {
      clearTimeout(timeout);
      setSaving(false);
    }
  };
  return (
    <section class="card" aria-labelledby="kill-title">
      <header class="cardhead">
        <h2 id="kill-title">{t['limits.kill.title']}</h2>
      </header>
      <p class="lim-killstate">
        <StateLabel tone={killSwitch.engaged ? 'crit' : 'ok'} label={killSwitch.engaged ? t.killSwitchTitle : t['limits.kill.released']} />
        {(killSwitch.source !== null || killSwitch.changedAt !== null) && (
          <span class="sub">
            {killSwitch.source !== null && t[`killSwitchSource.${killSwitch.source}`]}
            {killSwitch.changedAt !== null && (
              <>
                {killSwitch.source !== null && ', '}
                {t.since} <Timestamp at={killSwitch.changedAt} lang={lang} />
              </>
            )}
          </span>
        )}
      </p>
      <p class="note">{t['limits.kill.what']}</p>
      <p class="lim-killaction">
        <button type="button" class="lim-button" disabled={saving} onClick={() => void change()}>
          {killSwitch.engaged ? t['limits.kill.release'] : t['limits.kill.engage']}
        </button>
        {saving && <span class="sub-inline">{t.overrideSaving}</span>}
      </p>
      {refusal !== null && (
        <p class="sub" role="status">
          {t[refusal === 'late' ? 'limits.kill.late' : 'limits.kill.failed']}
        </p>
      )}
      <p class="sub">{t['limits.kill.how']}</p>
    </section>
  );
}

export function Limits({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = useGuard();
  const excursions = useExcursions();
  const devices = useDevices();
  // A kill switch change answers with the guard's view after it was evaluated; it is shown until a
  // newer poll arrives, so the page never flips back to the state before the change.
  const [answer, setAnswer] = useState<GuardStatus | null>(null);
  const names = namesOf(devices);
  const props = { t, lang };

  const guard = view.kind !== 'live' ? null : answer !== null && answer.at.getTime() >= view.guard.at.getTime() ? answer : view.guard;
  const breaker = guard?.breaker ?? null;

  return (
    <>
      <section class="card" aria-labelledby="limits-title">
        <header class="cardhead">
          <h2 id="limits-title">{t.guardTitle}</h2>
        </header>
        <p class="lim-lead">{t['limits.lead']}</p>
        {view.kind === 'loading' && <p class="updated">{t.guardLoading}</p>}
        {view.kind === 'unavailable' && (
          <div role="alert">
            <p class="unavailable">
              <StateLabel tone="crit" label={t.guardUnavailable} />
            </p>
            <p class="updated">
              {t.lastSuccessfulUpdate}: {view.lastSuccessAt ? <Timestamp at={view.lastSuccessAt} lang={lang} /> : t.never}
            </p>
          </div>
        )}
        {guard !== null && <Overview guard={guard} {...props} />}
      </section>
      {guard !== null && <Banners guard={guard} {...props} />}
      {guard !== null && breaker !== null && <TiersCard guard={guard} breaker={breaker} {...props} />}
      <div class="lim-grid">
        {guard !== null && breaker !== null && guard.phases.length > 0 && <BudgetCard guard={guard} breaker={breaker} excursions={excursions} {...props} />}
        <ExcursionCard view={excursions} names={names} {...props} />
      </div>
      {guard !== null && (breaker !== null || guard.devices.length > 0) && <ShedOrderCard guard={guard} devices={devices} names={names} {...props} />}
      {guard !== null && <KillSwitchCard guard={guard} onAnswer={setAnswer} {...props} />}
    </>
  );
}
