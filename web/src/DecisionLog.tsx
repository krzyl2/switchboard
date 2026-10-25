import { Fragment } from 'preact';
import { useEffect, useRef, useState } from 'preact/hooks';
import { DECISION_KINDS, type Decision, type DecisionKind } from './api/decisions';
import { fetchSignals, type Signal, type SignalState, type Signals } from './api/signals';
import type { Statistics, Totals } from './api/statistics';
import { SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS } from './config';
import { formatAge, formatFixed, formatKwh, formatPercent } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { localClock, localDay } from './localTime';
import { describeDecision } from './reasons';
import { PollStatus, StateLabel, Timestamp, type Tone } from './ui';
import { useDecisionLog } from './useDecisionLog';
import { usePoll, type PollView } from './usePoll';
import { useStatistics } from './useStatistics';
import './log.css';

// Screen 07 — Decision log (MODE-03, MODE-04, SIG-05), route #/log. Structure and vocabulary follow
// docs/mockups "Dziennik decyzji": the decisions with time, device, change and reason sentence
// (filters and paging over the add-on's memory window), then the signal health panel and the daily
// summary, where "Breaker trips" stays on screen permanently and claims only what can be measured.

const NO_VALUE = '—';

// The same tones as the Signals page: anything but fresh means the value is not used.
const SIGNAL_TONE: Record<SignalState, Tone> = { fresh: 'ok', stale: 'crit', unavailable: 'crit', rejected: 'crit', unmapped: 'muted' };

interface Props {
  t: Messages;
  lang: Lang;
}

type Device = Statistics['devices'][number];

function dayLabel(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit' }).format(at);
}

function Subject({ decision, name, t }: { decision: Decision; name: (id: string) => string; t: Messages }) {
  if (decision.scope === 'device' && decision.device !== null) {
    const label = name(decision.device);
    return (
      <>
        <span>{label}</span>
        {label !== decision.device && <span class="sub mono">{decision.device}</span>}
        {decision.stage !== null && (
          <span class="sub">
            {t.stage} {decision.stage}
          </span>
        )}
        {decision.phase !== null && <span class="sub mono">{decision.phase}</span>}
      </>
    );
  }
  if (decision.scope === 'phase' && decision.phase !== null) return <span class="mono">{decision.phase}</span>;
  if (decision.scope === 'group' && decision.group !== null) {
    return (
      <>
        <span class="mono">{decision.group}</span>
        {decision.phase !== null && <span class="sub mono">{decision.phase}</span>}
      </>
    );
  }
  return <span>{t.subjectSystem}</span>;
}

function DecisionRow({ decision, today, name, t, lang }: { decision: Decision; today: { from: Date; to: Date }; name: (id: string) => string } & Props) {
  const text = describeDecision(decision, t, lang);
  const otherDay = decision.at < today.from || decision.at >= today.to;
  return (
    <tr data-decision={decision.id} data-kind={decision.kind}>
      <td class="num">
        <Timestamp at={decision.at} lang={lang} />
        {otherDay && <span class="sub">{dayLabel(decision.at, lang)}</span>}
      </td>
      <td>
        <Subject decision={decision} name={name} t={t} />
      </td>
      <td>
        <span>{text.change}</span>
        {text.command !== null && <span class="sub">{text.command}</span>}
        <span class="sub">{t[`decisionKind.${decision.kind}`]}</span>
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

/**
 * MODE-03 / MODE-04: the decisions newest first, filtered by device and kind, paged with the
 * add-on's cursor over its memory window. A new filter starts again at the newest page.
 */
function DecisionsCard({ devices, now, name, t, lang }: { devices: Device[]; now: Date; name: (id: string) => string } & Props) {
  const [device, setDevice] = useState<string | undefined>(undefined);
  const [kind, setKind] = useState<DecisionKind | undefined>(undefined);
  // The cursors of the older pages opened, newest first; empty on the newest page.
  const [cursors, setCursors] = useState<string[]>([]);
  const view = useDecisionLog({ device, kind, before: cursors.at(-1) });
  const today = localDay(now);
  const options = [...devices];
  if (device !== undefined && !options.some((d) => d.id === device)) options.push({ id: device, name: null, valuePerKwh: 0 });
  const page = view.kind === 'live' ? view.data : null;
  return (
    <section class="card" aria-labelledby="log-title">
      <header class="cardhead">
        <h2 id="log-title">{t['log.title']}</h2>
        <p class="sub">{t['log.lead']}</p>
      </header>
      <div class="log-filters">
        <label>
          <span class="sub-inline">{t['log.filter.device']}</span>
          <select
            value={device ?? ''}
            onChange={(event) => {
              const value = (event.currentTarget as HTMLSelectElement).value;
              setDevice(value === '' ? undefined : value);
              setCursors([]);
            }}
          >
            <option value="">{t['log.filter.allDevices']}</option>
            {options.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name ?? d.id}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span class="sub-inline">{t['log.filter.kind']}</span>
          <select
            value={kind ?? ''}
            onChange={(event) => {
              const value = (event.currentTarget as HTMLSelectElement).value;
              setKind(value === '' ? undefined : (value as DecisionKind));
              setCursors([]);
            }}
          >
            <option value="">{t['log.filter.allKinds']}</option>
            {DECISION_KINDS.map((k) => (
              <option key={k} value={k}>
                {t[`decisionKind.${k}`]}
              </option>
            ))}
          </select>
        </label>
      </div>
      <PollStatus view={view} lang={lang} t={t} />
      {page !== null && !page.logWritable && (
        <p class="issues" role="status">
          <StateLabel tone="warn" label={t.decisionLogUnwritable} />
        </p>
      )}
      {page !== null && page.items.length === 0 && <p>{cursors.length === 0 && device === undefined && kind === undefined ? t.noDecisions : t['log.none']}</p>}
      {page !== null && page.items.length > 0 && (
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
              {page.items.map((decision) => (
                <DecisionRow key={decision.id} decision={decision} today={today} name={name} t={t} lang={lang} />
              ))}
            </tbody>
          </table>
        </div>
      )}
      <div class="log-paging">
        <button type="button" class="log-button" disabled={cursors.length === 0} onClick={() => setCursors([])}>
          {t['log.page.newest']}
        </button>
        <button type="button" class="log-button" disabled={cursors.length === 0} onClick={() => setCursors(cursors.slice(0, -1))}>
          {t['log.page.newer']}
        </button>
        <button type="button" class="log-button" disabled={page === null || page.next === null} onClick={() => page?.next && setCursors([...cursors, page.next])}>
          {t['log.page.older']}
        </button>
        <span class="sub-inline">{fill(t['log.page.position'], { page: String(cursors.length + 1) })}</span>
      </div>
      {page !== null && page.next === null && cursors.length > 0 && <p class="note">{t['log.page.end']}</p>}
      {view.kind === 'live' && (
        <p class="updated">
          {t.lastUpdated}: <Timestamp at={view.updatedAt} lang={lang} />
        </p>
      )}
    </section>
  );
}

function SignalRow({ signal, t, lang }: { signal: Signal } & Props) {
  return (
    <tr data-signal={signal.name ?? signal.role}>
      <td>
        <span>{signal.name ?? t[`role.${signal.role}`]}</span>
        <span class="sub mono">{signal.entity ?? NO_VALUE}</span>
      </td>
      <td class="num">
        {signal.ageS === null ? NO_VALUE : formatAge(signal.ageS, lang)}
        {signal.maxAgeS !== null && (
          <span class="sub">
            {t.maxAge} {formatAge(signal.maxAgeS, lang)}
          </span>
        )}
      </td>
      <td>
        <StateLabel tone={SIGNAL_TONE[signal.state]} label={t[`signalState.${signal.state}`]} />
        {signal.detail !== 'none' && <span class="sub">{t[`signalDetail.${signal.detail}`]}</span>}
      </td>
    </tr>
  );
}

/** SIG-05 on this screen: every signal with its entity, its age and its state. */
function SignalHealthCard({ view, t, lang }: { view: PollView<Signals> } & Props) {
  const signals = view.kind === 'live' ? view.data : null;
  return (
    <section class="card" aria-labelledby="health-title">
      <header class="cardhead">
        <h2 id="health-title">{t['log.signals.title']}</h2>
        <p class="sub">{t['log.signals.lead']}</p>
      </header>
      <PollStatus view={view} evaluatedAt={signals?.snapshotAt ?? null} now={signals?.generatedAt} lang={lang} t={t} />
      {signals !== null && signals.signals.length === 0 && <p>{t.noSignals}</p>}
      {signals !== null && signals.signals.length > 0 && (
        <div class="tablewrap">
          <table class="log-compact">
            <thead>
              <tr>
                <th scope="col">{t.colSignal}</th>
                <th scope="col">{t.colAge}</th>
                <th scope="col">{t.colState}</th>
              </tr>
            </thead>
            <tbody>
              {signals.signals.map((signal, i) => (
                <SignalRow key={signal.name ?? `${signal.role}-${i}`} signal={signal} t={t} lang={lang} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/**
 * How long the local day has run at the add-on's `at`: from local midnight, never the sum of the
 * buckets that exist, so downtime shows as unobserved time. Real elapsed time, so a 25-hour day
 * counts 24 hours at 23:00.
 */
export function elapsedOfDay(at: Date, day: { from: Date; to: Date }): number {
  return Math.max(0, (Math.min(at.getTime(), day.to.getTime()) - day.from.getTime()) / 1000);
}

/** PV self-consumption: the share of the production that was not exported; null without production. */
export function selfConsumption(totals: Totals): number | null {
  if (totals.generationKwh <= 0) return null;
  return Math.min(1, Math.max(0, (totals.generationKwh - totals.pv.exportKwh) / totals.generationKwh));
}

function BreakerTrips({ statistics, totals, elapsedS, t, lang }: { statistics: Statistics | null; totals: Totals | null; elapsedS: number } & Props) {
  if (statistics === null || totals === null) return <dd data-metric="breaker-trips">{NO_VALUE}</dd>;
  if (!statistics.voltageMeasured) {
    return (
      <dd data-metric="breaker-trips">
        <span class="unknown">{t['log.summary.tripsNotMeasured']}</span>
      </dd>
    );
  }
  const { lifetime } = statistics;
  return (
    <dd data-metric="breaker-trips">
      <span class="mono log-metric">{totals.voltageLosses}</span>
      <span class="sub">{fill(t['log.summary.tripsHow'], { observed: formatAge(totals.voltageObservedS, lang), elapsed: formatAge(elapsedS, lang) })}</span>
      <span class="sub">{t['log.summary.tripsBlind']}</span>
      {lifetime.since !== null && (
        <span class="sub">
          {fill(t['log.summary.tripsLifetime'], { date: dayLabel(lifetime.since, lang), trips: String(lifetime.voltageLosses), outages: String(lifetime.supplyOutages) })}
        </span>
      )}
    </dd>
  );
}

/**
 * The local day's summary: energy per device, self-consumption, sheds, the value delivered, the
 * savings (not available without a tariff) and, permanently, the breaker trips the add-on can see.
 */
function DailySummaryCard({ view, t, lang }: { view: PollView<Statistics> } & Props) {
  const statistics = view.kind === 'live' ? view.data : null;
  // Only the sums of the local day that contains the add-on's `at` count: the first answer (no
  // range asked yet) and an answer from before midnight are not today's.
  const day = statistics === null ? null : localDay(statistics.at);
  const asked = statistics?.range ?? null;
  const range = asked !== null && day !== null && asked.from.getTime() === day.from.getTime() ? asked : null;
  const totals = range?.totals ?? null;
  const elapsedS = statistics !== null && day !== null ? elapsedOfDay(statistics.at, day) : 0;
  const file = statistics?.stateFile ?? null;
  const share = totals === null ? null : selfConsumption(totals);
  const value = totals === null ? 0 : totals.devices.reduce((sum, d) => sum + d.value, 0);
  return (
    <section class="card" aria-labelledby="summary-title">
      <header class="cardhead">
        <h2 id="summary-title">{t['log.summary.title']}</h2>
        {statistics !== null && range !== null && totals !== null && (
          <p class="sub">
            {fill(t['log.summary.lead'], {
              date: dayLabel(range.from, lang),
              from: localClock(range.from, lang),
              observed: formatAge(totals.observedS, lang),
              elapsed: formatAge(elapsedS, lang),
            })}
          </p>
        )}
      </header>
      <PollStatus view={view} evaluatedAt={statistics?.integratedTo ?? null} now={statistics?.at} lang={lang} t={t} />
      {statistics !== null && range === null && <p class="updated">{t['log.summary.waiting']}</p>}
      {file !== null && file.status === 'reset_corrupt' && (
        <p class="sub" role="status">
          <StateLabel tone="warn" label={t['log.file.reset']} />
          {file.warning !== null && <span class="sub">{file.warning}</span>}
        </p>
      )}
      {file !== null && file.writeError !== null && (
        <p class="sub" role="status">
          <StateLabel tone="warn" label={fill(t['log.file.writeError'], { error: file.writeError })} />
        </p>
      )}
      <dl class="status log-summary">
        <dt>{t['log.summary.breakerTrips']}</dt>
        <BreakerTrips statistics={statistics} totals={totals} elapsedS={elapsedS} t={t} lang={lang} />
        {statistics !== null && totals !== null && (
          <>
            <dt>{t['log.summary.outages']}</dt>
            <dd data-metric="supply-outages">
              {statistics.voltageMeasured ? (
                <>
                  <span class="mono">{totals.supplyOutages}</span>
                  <span class="sub">{t['log.summary.outagesHow']}</span>
                </>
              ) : (
                <span class="unknown">{t['log.summary.tripsNotMeasured']}</span>
              )}
            </dd>
            <dt>{t['log.summary.selfConsumption']}</dt>
            <dd data-metric="self-consumption">
              {share === null ? (
                <span class="sub-inline">{t['log.summary.noProduction']}</span>
              ) : (
                <span class="mono">
                  {fill(t['log.summary.selfConsumptionValue'], {
                    percent: formatPercent(share, lang),
                    used: formatKwh(totals.generationKwh - totals.pv.exportKwh, lang),
                    produced: formatKwh(totals.generationKwh, lang),
                  })}
                </span>
              )}
            </dd>
            {statistics.devices.map((device) => {
              const energy = totals.devices.find((d) => d.id === device.id);
              const measured = energy?.measuredKwh ?? 0;
              const rated = energy?.modelKwh ?? 0;
              return (
                <Fragment key={device.id}>
                  <dt>{fill(t['log.summary.energy'], { device: device.name ?? device.id })}</dt>
                  <dd data-metric={`energy-${device.id}`}>
                    <span class="mono">{formatKwh(measured + rated, lang)}</span>
                    {measured > 0 && <span class="sub">{fill(t['log.summary.energyMeasured'], { value: formatKwh(measured, lang) })}</span>}
                    {rated > 0 && <span class="sub">{fill(t['log.summary.energyRated'], { value: formatKwh(rated, lang) })}</span>}
                    {energy !== undefined && energy.unknownS > 0 && (
                      <span class="sub unknown">{fill(t['log.summary.energyUnknown'], { time: formatAge(energy.unknownS, lang) })}</span>
                    )}
                  </dd>
                </Fragment>
              );
            })}
            <dt>{t['log.summary.sheds']}</dt>
            <dd data-metric="sheds">
              <span class="mono">{totals.sheds}</span>
              <span class="sub">{fill(t['log.summary.wouldSheds'], { count: String(totals.wouldSheds) })}</span>
            </dd>
            <dt>{t['log.summary.value']}</dt>
            <dd data-metric="value">
              <span class="mono">{formatFixed(value, lang, 2)}</span>
              <span class="sub">{t['log.summary.valueHow']}</span>
            </dd>
          </>
        )}
        <dt>{t['log.summary.savings']}</dt>
        <dd data-metric="savings">
          <span class="sub-inline">{t['log.summary.savingsNotAvailable']}</span>
        </dd>
      </dl>
    </section>
  );
}

export function DecisionLog({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const signals = usePoll(fetchSignals, SIGNALS_REFRESH_INTERVAL_MS, SIGNALS_REQUEST_TIMEOUT_MS);
  // The local day is known only from the add-on's own clock (data time in development replay): the
  // first answer gives it, and from then on only that day's buckets are asked for, again at midnight.
  const [day, setDay] = useState<{ from: Date; to: Date } | undefined>(undefined);
  const statistics = useStatistics(day);
  const at = statistics.kind === 'live' ? statistics.data.at : null;
  useEffect(() => {
    if (at === null) return;
    const current = localDay(at);
    if (day === undefined || current.from.getTime() !== day.from.getTime()) setDay(current);
  }, [at?.getTime()]);

  // Names and the device list are configuration, not measurements: the last ones known are kept
  // while the statistics reload, so the table never flips between names and ids.
  const devices = useRef<Device[]>([]);
  if (statistics.kind === 'live') devices.current = statistics.data.devices;
  const names = new Map(devices.current.map((d) => [d.id, d.name ?? d.id]));
  const name = (id: string) => names.get(id) ?? id;
  // "Today" on the add-on's clock, so a replayed day is dated as the data's day.
  const now = signals.kind === 'live' ? signals.data.generatedAt : (at ?? new Date());

  return (
    <>
      <DecisionsCard devices={devices.current} now={now} name={name} t={t} lang={lang} />
      <div class="log-grid">
        <SignalHealthCard view={signals} t={t} lang={lang} />
        <DailySummaryCard view={statistics} t={t} lang={lang} />
      </div>
    </>
  );
}
