import type { Level, ReasonCode } from './api/decisions';
import { deviceLabel, type DeviceConfig } from './api/devices';
import { fetchDispatch, type DispatchDevice, type DispatchStatus } from './api/dispatch';
import type { DeviceType } from './api/guard';
import { SEGMENT_KINDS, type Balance, type DevicePower, type PhasePower, type PlannedChange, type Power, type Quantity, type ReserveBanner } from './api/power';
import { DISPATCH_REFRESH_INTERVAL_MS, DISPATCH_REQUEST_TIMEOUT_MS } from './config';
import { formatAge, formatAmps, formatCountdown, formatFixed, formatKw, formatKwh, formatNumber, formatQuantity, roundTo } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { browserZone, localClock, localHourStart } from './localTime';
import { levelText, reasonSentence } from './reasons';
import { Age, PhaseBar, PollStatus, QuantityText, StateBadge, StateLabel, unknownReason, type Tone } from './ui';
import { useDevices } from './useDevices';
import { usePoll, type PollView } from './usePoll';
import { usePower } from './usePower';
import { useStatistics } from './useStatistics';
import './dashboard.css';

// Screen 01 — Dashboard (docs/05-UI-SPEC.md, docs/mockups "Pulpit"): the phase bars (UI-08 in the
// dashboard form, UI-11), the power balance, a banner per phase over its reserve threshold, and the
// allocation queue in dispatch start order (UI-09, UI-10). It only reads: GET api/power for the
// bars, the balance and the banners, api/dispatch for the queue, api/devices for display names and
// limits, and api/statistics for the current hour. Nothing here can change what Switchboard does.

interface Ctx {
  t: Messages;
  lang: Lang;
  /** The IANA zone local times are shown in (the browser's); only this screen's edge knows it. */
  zone: string;
}

type NameOf = (id: string) => string;

const HOUR_MS = 3_600_000;

/** A local wall-clock time with seconds, "14:02:02", in the zone the page shows. */
function clockText(at: Date, lang: Lang, zone: string): string {
  return new Intl.DateTimeFormat(lang, { timeZone: zone, hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).format(at);
}

function secondsBetween(from: Date, to: Date): number {
  return (to.getTime() - from.getTime()) / 1000;
}

function joinSentences(...parts: (string | null)[]): string {
  return parts.filter((part): part is string => part !== null).join(' ');
}

/** A device level that draws nothing: every relay off, a stopped setpoint device, a storage device at 0 A. */
function isOff(level: Level, type: DeviceType | null): boolean {
  if (level.relays !== null) return level.relays.every((on) => !on);
  if (!level.enabled) return true;
  // A modulating device at its minimum setpoint still runs; only storage has a setpoint that draws nothing.
  return type === 'storage' && level.setpointA === 0;
}

// ---------------------------------------------------------------------------------------------
// Power balance

/** The rows of the balance card in kW, as displayed (two decimals); null where a figure is unknown. */
export interface BalanceFigures {
  production: number | null;
  grid: number | null;
  controlled: number | null;
  storage: number | null;
  house: number | null;
}

/**
 * The rounding rule of the balance card. Every figure is rounded half away from zero to two
 * decimals of a kW (formatFixed), and something "not declared" counts as 0. The house load is the
 * add-on's only residual (Uncontrolled = Load − Controlled − Storage), so its displayed value is the
 * residual of the displayed figures: production + grid = house + declared devices + storage then
 * holds exactly on screen, and the house never shows the whole load (which would count the declared
 * devices twice). It differs from the house's own rounding by at most 0.02 kW.
 */
export function balanceFigures(balance: Balance): BalanceFigures {
  const kw = (q: Quantity): number | null => (q.w !== null ? roundTo(q.w / 1000, 2) : q.unknown === 'not_declared' ? 0 : null);
  const production = kw(balance.generation);
  const grid = kw(balance.netImport);
  const controlled = kw(balance.controlled);
  const storage = kw(balance.storage);
  const house =
    balance.uncontrolled.w === null || production === null || grid === null || controlled === null || storage === null
      ? null
      : roundTo(production + grid - controlled - storage, 2);
  return { production, grid, controlled, storage, house };
}

function kwText(kw: number, lang: Lang): string {
  return `${formatFixed(kw, lang, 2)} kW`;
}

/** A current with one decimal; float noise is rounded off first, so 1.13 × 25 A reads 28.3 A, not 28.2 A. */
function amps(value: number, lang: Lang): string {
  return formatAmps(roundTo(value, 6), lang);
}

function Withdrawn<T>({ view, t, lang, zone }: { view: PollView<T> } & Ctx) {
  // The freshness line itself (with its alert) is the first card's; here only why nothing is shown.
  if (view.kind === 'loading') return <p class="updated">{t['common.loading']}</p>;
  if (view.kind === 'live') return null;
  return (
    <p class="updated unavailable">
      <StateLabel tone="crit" label={view.lastSuccessAt === null ? t['poll.unavailableNever'] : fill(t['poll.unavailable'], { time: clockText(view.lastSuccessAt, lang, zone) })} />
    </p>
  );
}

/** Export minus import since the start of the local hour, from the add-on's integrated energy. */
function HourBalance({ at, t, lang, zone }: { at: Date } & Ctx) {
  const from = localHourStart(at, zone);
  const view = useStatistics({ from, to: new Date(from.getTime() + HOUR_MS) });
  if (view.kind === 'loading') return <span class="sub-inline">{t['common.loading']}</span>;
  const range = view.kind === 'live' ? view.data.range : null;
  if (view.kind !== 'live' || range === null) return <span class="unknown">{t['dashboard.hourUnavailable']}</span>;
  const { gridExportKwh, gridImportKwh, observedS } = range.totals;
  const net = gridExportKwh - gridImportKwh;
  // The denominator is the elapsed time of the hour, never the sum of buckets: downtime has no bucket.
  const elapsed = Math.max(0, secondsBetween(from, view.data.at));
  return (
    <>
      <span class="mono">{`${roundTo(net, 2) > 0 ? '+' : ''}${formatKwh(net, lang)}`}</span>
      <span class="sub">
        {fill(t['dashboard.hourDetail'], { time: localClock(from, lang, zone), import: formatKwh(gridImportKwh, lang), export: formatKwh(gridExportKwh, lang) })}
      </span>
      <span class="sub">{fill(t['dashboard.hourCoverage'], { observed: formatAge(observedS, lang), elapsed: formatAge(elapsed, lang) })}</span>
    </>
  );
}

function BalanceRows({ power, name, ...ctx }: { power: Power; name: NameOf } & Ctx) {
  const { t, lang } = ctx;
  const b = power.total;
  const figures = balanceFigures(b);
  const devices = b.unknownDevices.map(name);
  const age = (q: Quantity) => <Age reportedAt={q.reportedAt} now={power.at} lang={lang} t={t} />;
  const grid = b.netImport.w;
  const storage = b.storage.w;
  return (
    <>
      <dl class="dash-kv">
        <dt>{t['dashboard.production']}</dt>
        <dd data-row="production">
          <QuantityText quantity={b.generation} t={t} lang={lang} />
          {b.generationNote !== null && <span class="sub">{t[`generationNote.${b.generationNote}`]}</span>}
          {age(b.generation)}
        </dd>
        <dt>
          {t['dashboard.house']}
          <span class="sub">{t['dashboard.houseNote']}</span>
        </dt>
        <dd data-row="house">
          {figures.house !== null ? <span class="mono">{kwText(figures.house, lang)}</span> : <QuantityText quantity={b.uncontrolled} t={t} lang={lang} devices={devices} />}
          {age(b.uncontrolled)}
        </dd>
        <dt>{t['dashboard.controlled']}</dt>
        <dd data-row="controlled">
          <QuantityText quantity={b.controlled} t={t} lang={lang} devices={devices} />
          {age(b.controlled)}
        </dd>
        <dt>{t['dashboard.storage']}</dt>
        <dd data-row="storage">
          <QuantityText quantity={b.storage} t={t} lang={lang} devices={devices} />
          {storage !== null && storage !== 0 && <span class="sub-inline"> ({t[storage > 0 ? 'dashboard.charging' : 'dashboard.discharging']})</span>}
          {age(b.storage)}
        </dd>
        <dt>{t['dashboard.grid']}</dt>
        <dd data-row="grid">
          <QuantityText quantity={b.netImport} t={t} lang={lang} />
          {grid !== null && grid !== 0 && <span class="sub-inline"> ({t[grid > 0 ? 'direction.import' : 'direction.export']})</span>}
          {age(b.netImport)}
        </dd>
        <dt>{t['dashboard.hourBalance']}</dt>
        <dd data-row="hour">
          <HourBalance at={power.at} {...ctx} />
        </dd>
      </dl>
      {b.modelExceedsLoad && <p class="note">{t['dashboard.modelExceedsLoad']}</p>}
      <p class="dash-why">{t['dashboard.balanceWhy']}</p>
      <p class="dash-why">{t['dashboard.hourWhy']}</p>
    </>
  );
}

function BalanceCard({ view, name, ...ctx }: { view: PollView<Power>; name: NameOf } & Ctx) {
  const { t } = ctx;
  return (
    <section class="card" aria-labelledby="balance-title">
      <header class="cardhead">
        <h2 id="balance-title">{t['dashboard.balanceTitle']}</h2>
        <p class="sub">{t['dashboard.balanceSub']}</p>
      </header>
      {view.kind === 'live' ? <BalanceRows power={view.data} name={name} {...ctx} /> : <Withdrawn view={view} {...ctx} />}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------
// Phase bars

function distinct(values: number[]): number[] {
  return [...new Set(values)];
}

function Legend({ phases, t, lang }: { phases: PhasePower[] } & Ctx) {
  const present = new Set(phases.flatMap((p) => p.bar.map((s) => s.kind)));
  // UI-11: the legend always names base, declared devices and foreign load, each with its own colour.
  const kinds = SEGMENT_KINDS.filter((kind) => kind === 'base' || kind === 'controlled' || kind === 'foreign' || present.has(kind));
  const currents = (values: number[]) => values.map((v) => amps(v, lang)).join(' / ');
  return (
    <p class="legend dash-legend">
      {kinds.map((kind) => (
        <span key={kind}>
          <i class="key dash-swatch" data-kind={kind} aria-hidden="true" />
          {t[`segment.${kind}`]}
        </span>
      ))}
      {phases.length > 0 && (
        <>
          <span>
            <i class="key reserve" aria-hidden="true" />
            {fill(t['bar.reserve'], { value: currents(distinct(phases.map((p) => p.reserveThresholdA))) })}
          </span>
          <span>
            <i class="key" aria-hidden="true" />
            {fill(t['bar.breaker'], { value: distinct(phases.map((p) => p.ratingA)).map((v) => formatQuantity(v, 'A', lang)).join(' / ') })}
          </span>
          <span>
            <i class="key continuous" aria-hidden="true" />
            {fill(t['bar.continuous'], { value: currents(distinct(phases.map((p) => p.continuousA))) })}
          </span>
        </>
      )}
    </p>
  );
}

/**
 * The bar's own statement: the measured current (import, or a magnitude counted as import in
 * cautious mode) beyond the dashed marker. The add-on's overReserve also counts the guard's
 * estimate with the changes already decided (a decided start lifts it before anything draws), which
 * the bar does not draw; the banner says that case in its own words.
 */
function measuredOverReserve(phase: PhasePower): boolean {
  return phase.currentA !== null && phase.direction !== 'export' && Math.abs(phase.currentA) > phase.reserveThresholdA;
}

function PhaseLoad({ power, name, ...ctx }: { power: Power; name: NameOf } & Ctx) {
  const { t, lang } = ctx;
  const phases = power.phases;
  const ratings = distinct(phases.map((p) => p.ratingA));
  const thresholds = distinct(phases.map((p) => p.reserveThresholdA));
  const sub: string[] = [];
  if (ratings.length === 1 && thresholds.length === 1) {
    sub.push(fill(t['dashboard.phasesSub'], { rating: formatQuantity(ratings[0]!, 'A', lang), threshold: amps(thresholds[0]!, lang) }));
  }
  sub.push(fill(t['dashboard.cycle'], { cycle: formatAge(power.cycleS, lang) }));
  const scaled = phases.filter((p) => p.barScaled).map((p) => p.phase);
  const unattributed = phases.filter((p) => p.bar.some((s) => s.kind === 'unattributed'));
  return (
    <>
      {!power.dataValid && power.invalidReason !== null && (
        <p class="dash-invalid">
          <StateLabel tone="crit" label={fill(t['dashboard.invalid'], { reason: t[`powerUnknown.${power.invalidReason}`] })} />
        </p>
      )}
      <h3 class="dash-subhead">{t['dashboard.phasesTitle']}</h3>
      <p class="sub">{sub.join(' · ')}</p>
      {phases.length === 0 ? (
        <p class="note">{t['dashboard.noPhases']}</p>
      ) : (
        <div class="phasebars">
          {phases.map((phase) => (
            <PhaseBar key={phase.phase} phase={phase} t={t} lang={lang}>
              {phase.direction === 'export' && <small>{t['direction.export']}</small>}
              {measuredOverReserve(phase) && (
                <small>
                  <StateLabel tone="warn" label={t['dashboard.overReserve']} />
                </small>
              )}
              <small>
                <Age reportedAt={phase.currentReportedAt} now={power.at} lang={lang} t={t} />
              </small>
            </PhaseBar>
          ))}
        </div>
      )}
      <Legend phases={phases} {...ctx} />
      {scaled.length > 0 && <p class="note">{`${scaled.join(', ')}: ${t['segment.scaled']}`}</p>}
      {unattributed.map((phase) => {
        const q = phase.balance.controlled.unknown !== null ? phase.balance.controlled : phase.balance.uncontrolled;
        const reason = unknownReason(t, q.unknown ?? 'power_not_fresh', phase.balance.unknownDevices.map(name));
        return (
          <p key={phase.phase} class="note">
            {fill(t['dashboard.unattributed'], { phase: phase.phase, reason })}
          </p>
        );
      })}
    </>
  );
}

// ---------------------------------------------------------------------------------------------
// Banner over a phase above its reserve threshold

function plannedText(change: PlannedChange, type: DeviceType | null, name: NameOf, t: Messages, lang: Lang): string {
  const device = name(change.device);
  if (isOff(change.to, type)) return fill(t['dashboard.plannedOff'], { device });
  const to = levelText(change.to, t, lang);
  return change.from === null
    ? fill(t['dashboard.plannedReduceTo'], { device, to })
    : fill(t['dashboard.plannedReduce'], { device, from: levelText(change.from, t, lang), to });
}

/**
 * The banner the mockup draws under the phase bars, placed above the queue (05-UI-SPEC): the phase,
 * the foreign load that pushed it over, the reduction dispatch or protection would make with its
 * timing from the guard's own documented reaction, and, while commands do not go out (dispatch's
 * commandPath is false), that nothing is switched. Unknown until dispatch answered: never claimed.
 */
function Banner({
  banner,
  power,
  configs,
  name,
  commandPath,
  ...ctx
}: { banner: ReserveBanner; power: Power; configs: DeviceConfig[] | null; name: NameOf; commandPath: boolean | null } & Ctx) {
  const { t, lang, zone } = ctx;
  const phase = power.phases.find((p) => p.phase === banner.phase) ?? null;
  const current = Math.abs(banner.currentA);
  // Signed: an exporting phase is never over its reserve threshold by its measured current.
  const measuredOver = banner.currentA > banner.thresholdA;
  const estimate = banner.effectiveCurrentA;
  const lines: string[] = [];

  // In shadow mode a decided reduction relieves the estimate while the meter still reads the same:
  // the banner stays and says what the decided changes would leave.
  if (measuredOver && estimate !== null && roundTo(estimate, 1) !== roundTo(current, 1)) {
    lines.push(fill(t['dashboard.bannerEstimate'], { estimate: amps(estimate, lang) }));
  }

  if (banner.foreignStepW === null) {
    const q = phase?.balance.uncontrolled;
    lines.push(fill(t['dashboard.bannerForeignUnknown'], { reason: unknownReason(t, q?.unknown ?? 'power_not_fresh', phase?.balance.unknownDevices.map(name) ?? []) }));
  } else if (roundTo(banner.foreignStepW / 1000, 2) <= 0) {
    // A step that shows as 0.00 kW is no foreign load worth naming.
    lines.push(t['dashboard.bannerNoForeign']);
  } else if (banner.foreignDetectedAt !== null) {
    lines.push(fill(t['dashboard.bannerForeignAt'], { power: formatKw(banner.foreignStepW, lang), age: formatAge(Math.max(0, secondsBetween(banner.foreignDetectedAt, power.at)), lang) }));
  } else {
    lines.push(fill(t['dashboard.bannerForeign'], { power: formatKw(banner.foreignStepW, lang) }));
  }

  if (banner.timing !== 'cannot_reduce_exporting') {
    for (const change of banner.planned) {
      const type = configs?.find((c) => c.id === change.device)?.type ?? null;
      lines.push(
        joinSentences(
          plannedText(change, type, name, t, lang),
          change.dueAt === null ? null : fill(t['dashboard.plannedAfterLockout'], { time: clockText(change.dueAt, lang, zone) }),
        ),
      );
    }
    if (banner.planned.length === 0) lines.push(t[banner.reductionRequested ? 'dashboard.nothingToReduce' : 'dashboard.notRequestedYet']);
  }

  const timing = t[`dashboard.timing.${banner.timing}`];
  if (banner.timing === 'next_boundary' || banner.timing === 'by_boundary') {
    if (banner.dueAt !== null) {
      lines.push(fill(timing, { time: clockText(banner.dueAt, lang, zone), countdown: formatCountdown(secondsBetween(power.at, banner.dueAt)) }));
    }
  } else {
    lines.push(timing);
  }

  if (phase !== null && current <= phase.ratingA && (banner.timing === 'next_boundary' || banner.timing === 'by_boundary')) {
    lines.push(fill(t['dashboard.belowBreaker'], { current: amps(current, lang), rating: formatQuantity(phase.ratingA, 'A', lang) }));
  } else if (phase !== null && current > phase.ratingA && banner.reaction !== null) {
    lines.push(fill(t['dashboard.aboveBreaker'], { rating: formatQuantity(phase.ratingA, 'A', lang), reaction: t[`tierReaction.${banner.reaction}`] }));
  }
  // Without a command path whatever the banner names is only what Switchboard would do.
  if (commandPath === false) lines.push(t['dashboard.bannerShadow']);

  const id = `reserve-banner-${banner.phase}`;
  return (
    <section class="card banner" aria-labelledby={id} data-banner-phase={banner.phase}>
      <h3 id={id}>
        {measuredOver || estimate === null
          ? fill(t['dashboard.bannerTitle'], { phase: banner.phase, current: amps(current, lang), threshold: amps(banner.thresholdA, lang) })
          : fill(t['dashboard.bannerTitleEstimate'], { phase: banner.phase, estimate: amps(estimate, lang), threshold: amps(banner.thresholdA, lang), current: amps(current, lang) })}
      </h3>
      {lines.map((line, index) => (
        <p key={index}>{line}</p>
      ))}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------
// Allocation queue

/**
 * The closed list of queue states, in precedence order: the first that applies is shown. A
 * protective hold outranks everything, as it does in the effector's target resolution (kill switch >
 * safe state > guard hold > override > dispatch); a device that does not respond comes next, then a
 * manual override, a deferred change, what dispatch would change, and only then what is happening.
 */
export const QUEUE_STATES = [
  'held',
  'unresponsive',
  'forced_on',
  'forced_off',
  'deferred',
  'would_start',
  'would_stop',
  'would_change',
  'running',
  'charging',
  'discharging',
  'idle',
  'enabled_stale',
  'enabled',
  'stopped',
  'unknown',
] as const;

export type QueueState = (typeof QUEUE_STATES)[number];

const STATE_TONE: Record<QueueState, Tone> = {
  held: 'warn',
  unresponsive: 'crit',
  forced_on: 'warn',
  forced_off: 'warn',
  deferred: 'muted',
  would_start: 'ok',
  would_stop: 'warn',
  would_change: 'warn',
  running: 'ok',
  charging: 'ok',
  discharging: 'ok',
  idle: 'muted',
  enabled_stale: 'warn',
  enabled: 'muted',
  stopped: 'muted',
  unknown: 'crit',
};

/**
 * What a setpoint device is doing, from its own fresh draw: a reported setpoint is only a ceiling,
 * so an idle car at 20 A must not read as running. A draw counts as zero below half the device's
 * minimum setpoint (a charger stays near 0 A when no car takes power; the recorded go-e idles
 * below 2 A, its minimum is 6 A), and for storage below half a setpoint step either way. A
 * presentation rule only: dispatch and protection never read it.
 */
function drawState(device: DispatchDevice, config: DeviceConfig | null, voltageV: number | null): QueueState {
  const draw = device.draw;
  if (draw === null) return 'enabled';
  if (!draw.fresh) return 'enabled_stale';
  const phases = Math.max(1, device.phases.length);
  const perPhaseA =
    draw.currentA !== null ? draw.currentA / phases : draw.powerW !== null && voltageV !== null && voltageV > 0 ? draw.powerW / voltageV / phases : null;
  if (perPhaseA === null) return 'enabled_stale';
  const range = config?.range ?? null;
  if (device.type === 'storage') {
    const idleBelow = range === null ? 0 : range.stepA / 2;
    if (perPhaseA > idleBelow) return 'charging';
    return perPhaseA < -idleBelow ? 'discharging' : 'idle';
  }
  const idleBelow = range === null ? 0 : range.minA / 2;
  return perPhaseA > idleBelow ? 'running' : 'idle';
}

/** The queue state of one device; <paramref name="voltageV"/> converts a power-only draw to amperes. */
export function queueState(device: DispatchDevice, config: DeviceConfig | null, voltageV: number | null): QueueState {
  if (device.origin === 'guard' && device.intended !== null) return 'held';
  if (device.unresponsive !== null) return 'unresponsive';
  if (device.override.mode === 'force_on') return 'forced_on';
  if (device.override.mode === 'force_off') return 'forced_off';
  if (device.deferred !== null) return 'deferred';
  const reported = device.reported;
  if (device.intended !== null && !device.matches) {
    if (isOff(device.intended, device.type)) return 'would_stop';
    if (reported !== null && isOff(reported, device.type)) return 'would_start';
    return 'would_change';
  }
  if (reported === null) return 'unknown';
  if (isOff(reported, device.type)) return 'stopped';
  if (device.type === 'on_off' || device.type === 'stepped') return 'running';
  return drawState(device, config, voltageV);
}

function stateLabel(state: QueueState, device: DispatchDevice, at: Date, t: Messages): string {
  const deferred = device.deferred;
  if (state === 'deferred' && deferred !== null && deferred.dueAt !== null && (deferred.refusedBy === 'lockout_min_on' || deferred.refusedBy === 'lockout_min_off')) {
    return fill(t['dashboard.lockout'], { time: formatCountdown(secondsBetween(at, deferred.dueAt)) });
  }
  return t[`dashboard.state.${state}`];
}

/** Why nothing better can be said: the phase has no valid power data, or dispatch has not run yet. */
function blockingReason(device: DispatchDevice, dispatch: DispatchStatus, t: Messages): string {
  const invalid = dispatch.phases.find((p) => device.phases.includes(p.phase) && !p.dataValid);
  if (invalid === undefined) return t['dashboard.noReason'];
  return fill(t['dashboard.phaseInvalid'], {
    phase: invalid.phase,
    reason: invalid.invalidReason === null ? t.meterDataInvalid : t[`dispatchInvalid.${invalid.invalidReason}`],
  });
}

/** One line saying why the device is in its state (UI-10: a stopped device keeps its blocking reason). */
function queueReason(state: QueueState, device: DispatchDevice, dispatch: DispatchStatus, { t, lang, zone }: Ctx): string {
  const sentence = (code: ReasonCode | null) => (code === null ? null : reasonSentence(t, code));
  switch (state) {
    case 'held':
    case 'would_start':
    case 'would_stop':
    case 'would_change':
      return sentence(device.reason) ?? blockingReason(device, dispatch, t);
    case 'unresponsive': {
      const retryAt = device.unresponsive?.retryAt ?? null;
      return joinSentences(reasonSentence(t, 'device_unresponsive'), retryAt === null ? null : fill(t['dashboard.retryAt'], { time: clockText(retryAt, lang, zone) }));
    }
    case 'forced_on':
    case 'forced_off': {
      const { source, changedAt } = device.override;
      return source === null || changedAt === null
        ? reasonSentence(t, 'manual_override')
        : fill(t['dashboard.override'], { source: t[`overrideSource.${source}`], time: clockText(changedAt, lang, zone) });
    }
    case 'deferred': {
      const deferred = device.deferred;
      if (deferred === null) return blockingReason(device, dispatch, t);
      return joinSentences(reasonSentence(t, deferred.refusedBy), deferred.dueAt === null ? null : fill(t['dashboard.dueAt'], { time: clockText(deferred.dueAt, lang, zone) }));
    }
    default:
      // An increase dispatch wants but cannot make yet is the blocking reason of a device not running.
      if (device.pending !== null) return joinSentences(t['dashboard.pending'], reasonSentence(t, device.pending));
      return sentence(device.reason) ?? blockingReason(device, dispatch, t);
  }
}

function oldest(times: (Date | null)[]): Date | null {
  const known = times.filter((at): at is Date => at !== null);
  return known.length === 0 ? null : new Date(Math.min(...known.map((at) => at.getTime())));
}

/** The device's power from the power picture, with its source: measured (and how old), rated, or unknown. */
function DevicePowerText({ view, hasDraw, now, t, lang }: { view: DevicePower | null; hasDraw: boolean; now: Date | null } & Ctx) {
  if (view === null) return <span class="mono">{t['common.none']}</span>;
  if (view.totalW === null) {
    return <span class="unknown">{fill(t['common.unknownBecause'], { reason: t[hasDraw ? 'dashboard.drawNotFresh' : 'dashboard.noDraw'] })}</span>;
  }
  const reportedAt = view.source === 'measured' ? oldest(view.phases.map((p) => p.reportedAt)) : null;
  return (
    <>
      <span class="mono">{formatKw(view.totalW, lang)}</span>
      <small>
        {t[`source.${view.source}`]}
        {reportedAt !== null && now !== null && (
          <>
            {' · '}
            <Age reportedAt={reportedAt} now={now} lang={lang} t={t} />
          </>
        )}
      </small>
    </>
  );
}

function QueueRow({
  device,
  dispatch,
  power,
  config,
  name,
  ...ctx
}: { device: DispatchDevice; dispatch: DispatchStatus; power: Power | null; config: DeviceConfig | null; name: NameOf } & Ctx) {
  const { t, lang } = ctx;
  const voltage = power?.phases.find((p) => p.phase === device.phases[0])?.voltageV ?? null;
  const state = queueState(device, config, voltage);
  const meta: string[] = [device.phases.join(', ')];
  if (config !== null) meta.push(fill(t.valuePerKwh, { value: formatNumber(config.valuePerKwh, lang, 2) }));
  // What dispatch would change it to; the device card shows intended against reported in full.
  if ((state === 'would_start' || state === 'would_stop' || state === 'would_change') && device.intended !== null) {
    meta.push(fill(t.targetDetail, { level: levelText(device.intended, t, lang) }));
  }
  const draw = device.draw;
  if (device.type === 'modulating' && device.reported !== null && device.reported.enabled && draw !== null && draw.fresh && draw.currentA !== null) {
    meta.push(fill(t['dashboard.drawOfSetpoint'], { draw: amps(draw.currentA, lang), setpoint: levelText(device.reported, t, lang) }));
  }
  if (state !== 'deferred') {
    if (device.lockout.minOnRemainingS > 0) meta.push(fill(t.lockoutMinOn, { time: formatAge(device.lockout.minOnRemainingS, lang) }));
    if (device.lockout.minOffRemainingS > 0) meta.push(fill(t.lockoutMinOff, { time: formatAge(device.lockout.minOffRemainingS, lang) }));
  }
  return (
    <li class="dash-row" data-device={device.id} data-state={state}>
      <div class="dash-top">
        <a class="dash-name" href={`#/devices/${encodeURIComponent(device.id)}`}>
          {name(device.id)}
        </a>
        <StateBadge tone={STATE_TONE[state]} label={stateLabel(state, device, dispatch.at, t)} />
      </div>
      <div class="dash-power">
        <DevicePowerText
          view={power?.devices.find((d) => d.id === device.id) ?? null}
          hasDraw={draw !== null || (config?.draw ?? null) !== null}
          now={power?.at ?? null}
          {...ctx}
        />
      </div>
      <p class="dash-meta">{meta.join(' · ')}</p>
      <p class="dash-reason">{queueReason(state, device, dispatch, ctx)}</p>
    </li>
  );
}

/** Dispatch's start order (DISP-03): the device given headroom first comes first; one it never ranks, last. */
function byStartRank(a: DispatchDevice, b: DispatchDevice): number {
  if (a.startRank === b.startRank) return 0;
  if (a.startRank === null) return 1;
  if (b.startRank === null) return -1;
  return a.startRank - b.startRank;
}

function QueueCard({
  view,
  power,
  configs,
  name,
  ...ctx
}: { view: PollView<DispatchStatus>; power: Power | null; configs: DeviceConfig[] | null; name: NameOf } & Ctx) {
  const { t, lang } = ctx;
  const dispatch = view.kind === 'live' ? view.data : null;
  // Every declared device keeps its row, whatever its state (UI-10): dispatch lists them all.
  const devices = dispatch === null ? [] : [...dispatch.devices].sort(byStartRank);
  return (
    <section class="card" aria-labelledby="queue-title">
      <header class="cardhead">
        <h2 id="queue-title">{t['dashboard.queueTitle']}</h2>
        <p class="sub">{t['dashboard.queueSub']}</p>
      </header>
      {/* Its age on the add-on's clock, which the power picture carries (dispatch's own time is the step's). */}
      <PollStatus view={view} evaluatedAt={dispatch?.at ?? null} now={power?.at} lang={lang} t={t} />
      {dispatch !== null && !dispatch.commandPath && <p class="sub">{t['dashboard.queueShadow']}</p>}
      {dispatch !== null &&
        (devices.length === 0 ? (
          <p class="note">{t['dashboard.noDevices']}</p>
        ) : (
          <ol class="dash-queue">
            {devices.map((device) => (
              <QueueRow
                key={device.id}
                device={device}
                dispatch={dispatch}
                power={power}
                config={configs?.find((c) => c.id === device.id) ?? null}
                name={name}
                {...ctx}
              />
            ))}
          </ol>
        ))}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------

export function Dashboard({ lang, zone = browserZone() }: { lang: Lang; zone?: string }) {
  const t = messages[lang];
  const ctx: Ctx = { t, lang, zone };
  const power = usePower();
  const dispatch = usePoll(fetchDispatch, DISPATCH_REFRESH_INTERVAL_MS, DISPATCH_REQUEST_TIMEOUT_MS);
  const devices = useDevices();
  const live = power.kind === 'live' ? power.data : null;
  const configs = devices.kind === 'live' ? devices.data.devices : null;
  const name: NameOf = (id) => deviceLabel(configs, id);
  return (
    <>
      <section class="card" aria-labelledby="dashboard-title">
        <header class="cardhead">
          <h2 id="dashboard-title">{t['dashboard.title']}</h2>
          <p class="sub">{t['dashboard.lead']}</p>
        </header>
        <PollStatus view={power} evaluatedAt={live?.evaluatedAt ?? null} now={live?.at} lang={lang} t={t} />
        {live !== null && <PhaseLoad power={live} name={name} {...ctx} />}
      </section>
      {live?.banners.map((banner) => (
        <Banner key={banner.phase} banner={banner} power={live} configs={configs} name={name} commandPath={dispatch.kind === 'live' ? dispatch.data.commandPath : null} {...ctx} />
      ))}
      <div class="dash-grid">
        <BalanceCard view={power} name={name} {...ctx} />
        <QueueCard view={dispatch} power={live} configs={configs} name={name} {...ctx} />
      </div>
    </>
  );
}
