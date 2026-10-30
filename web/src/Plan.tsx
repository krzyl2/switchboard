import type { Plan, PlanDevice, PlanSlot, PlanStatus } from './api/plan';
import { formatAge, formatKwh, formatNumber } from './format';
import { GoalLines } from './GoalView';
import { EvCard } from './EvView';
import { fill, messages, type Lang, type Messages } from './i18n';
import { browserZone, localClock } from './localTime';
import { costText, dayTime, priceTones, RunText, runsOf, type PriceTone } from './planText';
import { PollStatus, StateBadge, type Tone } from './ui';
import { useEv, type EvControl } from './useEv';
import { usePlan } from './usePlan';
import './plan.css';

// Screen 06 — Day plan (PLAN-19, docs/mockups "Plan doby"): what the planner intends from now to the horizon, per device, the cost against
// two references, the inputs it stood on and what it warns about. Read-only: the plan is an estimate from forecasts, it is shown as such, and
// with planner.dispatch off it is only a preview. The only thing that can be changed here is on the device card (the manual goal).

interface Ctx {
  t: Messages;
  lang: Lang;
  zone: string;
}

const STATUS_TONE: Record<PlanStatus, Tone> = { fresh: 'ok', stale: 'warn', absent: 'muted', suspended: 'warn', error: 'crit' };

function ageSince(at: Date, now: Date, lang: Lang): string {
  return formatAge(Math.max(0, (now.getTime() - at.getTime()) / 1000), lang);
}

/** The plan's own state, in words, and what it means for whether the plan can be used. */
function StatusNote({ plan, t, lang }: { plan: Plan } & Omit<Ctx, 'zone'>) {
  const age = plan.computedAt === null ? '' : ageSince(plan.computedAt, plan.at, lang);
  const key =
    plan.status === 'suspended' ? (plan.computedAt === null ? 'plan.statusNote.suspendedNone' : 'plan.statusNote.suspended') : (`plan.statusNote.${plan.status}` as const);
  return <p class="sub" data-plan="status-note">{fill(t[key], { age })}</p>;
}

function DispatchNote({ plan, t }: { plan: Plan; t: Messages }) {
  if (!plan.enabled || plan.dispatchMode === null) return <p class="note" data-plan="not-enabled">{t['plan.notEnabled']}</p>;
  return (
    <p class="note" data-plan="dispatch" data-dispatch={plan.dispatchMode}>
      {t[`plan.dispatch.${plan.dispatchMode}`]}
    </p>
  );
}

function solveReason(t: Messages, reason: string): string {
  const key = `plan.solve.reason.${reason}`;
  return key in t ? t[key as keyof Messages] : reason;
}

function Header({ plan, ...ctx }: { plan: Plan } & Ctx) {
  const { t, lang, zone } = ctx;
  const solve = plan.lastSolve;
  return (
    <section class="card" aria-labelledby="plan-title" data-card="plan-head">
      <header class="cardhead">
        <h2 id="plan-title">{t['plan.title']}</h2>
        <StateBadge tone={STATUS_TONE[plan.status]} label={`${t['plan.status.label']}: ${t[`plan.status.${plan.status}`]}`} />
        <p class="sub">{t['plan.lead']}</p>
      </header>
      <DispatchNote plan={plan} t={t} />
      {plan.enabled && <StatusNote plan={plan} t={t} lang={lang} />}
      {plan.enabled && (
        <dl class="dash-kv plan-kv">
          {plan.computedAt !== null && (
            <>
              <dt>{t['plan.row.computed']}</dt>
              <dd data-row="plan-computed">
                <span class="mono">{dayTime(plan.computedAt, plan.at, lang, zone)}</span>
              </dd>
            </>
          )}
          {plan.validUntil !== null && (
            <>
              <dt>{t['plan.row.validUntil']}</dt>
              <dd data-row="plan-valid">
                <span class="mono">{dayTime(plan.validUntil, plan.at, lang, zone)}</span>
              </dd>
            </>
          )}
          {plan.horizonEnd !== null && (
            <>
              <dt>{t['plan.row.horizon']}</dt>
              <dd data-row="plan-horizon">
                <span class="mono">{dayTime(plan.horizonEnd, plan.at, lang, zone)}</span>
              </dd>
            </>
          )}
          {plan.slotMinutes !== null && (
            <>
              <dt>{t['plan.row.slot']}</dt>
              <dd data-row="plan-slot">
                <span class="mono">{fill(t['plan.slotMinutes'], { minutes: formatNumber(plan.slotMinutes, lang, 0) })}</span>
              </dd>
            </>
          )}
          {solve !== null && (
            <>
              <dt>{t['plan.row.lastSolve']}</dt>
              <dd data-row="plan-solve" data-solve={solve.result}>
                {fill(t['plan.solve.summary'], { result: t[`plan.solve.result.${solve.result}`], ms: formatNumber(solve.durationMs, lang, 0), reason: solveReason(t, solve.reason) })}
                {solve.violations.length > 0 && (
                  <span class="sub">{fill(t['plan.solve.violations'], { codes: solve.violations.map((code) => t[`plan.violation.${code}`]).join('; ') })}</span>
                )}
              </dd>
            </>
          )}
        </dl>
      )}
      <p class="dash-why" data-plan="estimate">
        {t['plan.estimate']}
      </p>
    </section>
  );
}

function Inputs({ plan, ...ctx }: { plan: Plan } & Ctx) {
  const { t, lang, zone } = ctx;
  const { pvForecast, loadForecast, prices, goalsActive, goalsInactive } = plan.inputs;
  if (!plan.enabled) return null;
  return (
    <section class="card" aria-labelledby="plan-inputs-title" data-card="plan-inputs">
      <header class="cardhead">
        <h2 id="plan-inputs-title">{t['plan.inputs.title']}</h2>
      </header>
      <dl class="dash-kv plan-kv">
        {pvForecast !== null && (
          <>
            <dt>{t['plan.inputs.pv']}</dt>
            <dd data-row="inputs-pv" data-basis={pvForecast.basis}>
              {fill(t[`plan.inputs.pv.${pvForecast.basis}`], { derate: formatNumber(pvForecast.derate, lang, 2) })}
              <span class="sub">
                {pvForecast.ageS === null
                  ? fill(t['plan.inputs.pvStateNever'], { state: pvForecast.state })
                  : fill(t['plan.inputs.pvState'], { state: pvForecast.state, age: formatAge(pvForecast.ageS, lang) })}
                {pvForecast.coversUntil !== null && <> · {fill(t['plan.inputs.pvCovers'], { time: dayTime(pvForecast.coversUntil, plan.at, lang, zone) })}</>}
              </span>
            </dd>
          </>
        )}
        {loadForecast !== null && (
          <>
            <dt>{t['plan.inputs.load']}</dt>
            <dd data-row="inputs-load" data-basis={loadForecast.basis}>
              {t[`plan.inputs.load.${loadForecast.basis}`]}
              <span class="sub">{fill(t['plan.inputs.loadDays'], { days: String(loadForecast.daysFilled) })}</span>
            </dd>
          </>
        )}
        {prices !== null && (
          <>
            <dt>{t['plan.inputs.prices']}</dt>
            <dd data-row="inputs-prices" data-basis={prices.basis}>
              {t[`plan.inputs.prices.${prices.basis}`]}
              {prices.coversUntil !== null && <span class="sub">{fill(t['plan.inputs.pricesCovers'], { time: dayTime(prices.coversUntil, plan.at, lang, zone) })}</span>}
            </dd>
          </>
        )}
        <dt>{t['plan.inputs.goals']}</dt>
        <dd data-row="inputs-goals">{fill(t['plan.inputs.goalsCount'], { active: String(goalsActive), inactive: String(goalsInactive.length) })}</dd>
      </dl>
    </section>
  );
}

function Notes({ plan, t }: { plan: Plan; t: Messages }) {
  if (plan.notes.length === 0 && plan.issues.length === 0) return null;
  return (
    <section class="card" aria-labelledby="plan-notes-title" data-card="plan-notes">
      <header class="cardhead">
        <h2 id="plan-notes-title">{t['plan.notes.title']}</h2>
      </header>
      <ul class="plainlist">
        {plan.notes.map((note) => (
          <li key={note} data-note={note}>
            {t[`plan.note.${note}`]}
          </li>
        ))}
      </ul>
      {plan.issues.length > 0 && (
        <>
          <p class="note">{t['plan.issues.title']}</p>
          <ul class="plainlist">
            {plan.issues.map((issue, i) => (
              <li key={`${issue.path}-${i}`}>
                <span class="mono">{issue.path}</span>: {issue.message}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

// The picture: time left to right in proportion to real duration (a 23- or 25-hour day is drawn as it is), the tariff zones as a band
// coloured by price rank, the PV forecast as a step line, one row per device with bars by energy class, and a marker for now.
const LABEL_MIN_W = 84;
const PROSE_CHAR_W = 5; // 9 px axis text, a little generous so a longer translation never runs under the zone band
const MONO_CHAR_W = 5.6;
const HOUR_W = 16;
const BAND_Y = 4;
const BAND_H = 12;
const PV_Y = 22;
const PV_H = 56;
const ROW_Y = 88;
const ROW_H = 18;
const BAR_H = 12;
const AXIS_H = 18;

const HOUR_MS = 3_600_000;

function wallHour(at: Date, zone: string): number {
  return Number(new Intl.DateTimeFormat('en-US', { timeZone: zone, hour: '2-digit', hourCycle: 'h23' }).format(at));
}

function Chart({ plan, devices, ...ctx }: { plan: Plan; devices: PlanDevice[] } & Ctx) {
  const { t, lang, zone } = ctx;
  const slots = plan.slots;
  if (slots.length === 0) return <p class="note" data-plan="no-slots">{t['plan.chart.noSlots']}</p>;
  const t0 = slots[0]!.start.getTime();
  const t1 = slots[slots.length - 1]!.end.getTime();
  const rows = devices.filter((d) => runsOf(d.entries).length > 0);
  const labelW = Math.max(
    LABEL_MIN_W,
    Math.ceil(Math.max(t['plan.chart.zones'].length, t['plan.chart.pv'].length) * PROSE_CHAR_W) + 8,
    Math.ceil(Math.max(0, ...rows.map((d) => d.device.length)) * MONO_CHAR_W) + 8,
  );
  const x = (ms: number) => labelW + ((ms - t0) / HOUR_MS) * HOUR_W;
  const width = x(t1) + 8;
  const tones = priceTones(slots);
  const kw = (s: PlanSlot) => s.pvKwh / Math.max(1e-9, (s.end.getTime() - s.start.getTime()) / HOUR_MS);
  const peak = Math.max(0.5, ...slots.map(kw));
  const height = ROW_Y + Math.max(1, rows.length) * ROW_H + AXIS_H;
  const y = (value: number) => PV_Y + PV_H - (value / peak) * PV_H;
  const pv = slots.flatMap((s) => [`${x(s.start.getTime())},${y(kw(s))}`, `${x(s.end.getTime())},${y(kw(s))}`]).join(' ');
  const ticks = slots.filter((s) => localClock(s.start, lang, zone).endsWith(':00') && wallHour(s.start, zone) % 6 === 0);
  const nowX = x(Math.min(Math.max(plan.at.getTime(), t0), t1));
  return (
    <div class="plan-scroll">
      <svg class="plan-chart" viewBox={`0 0 ${width} ${height}`} width={width} height={height} role="img" aria-label={t['plan.chart.label']} data-plan="chart">
        <text class="plan-axis" x="0" y={BAND_Y + BAND_H - 2}>
          {t['plan.chart.zones']}
        </text>
        {slots.map((s, i) => (
          <rect key={s.start.getTime()} class="plan-zone" data-tone={tones[i]} x={x(s.start.getTime())} y={BAND_Y} width={x(s.end.getTime()) - x(s.start.getTime())} height={BAND_H}>
            <title>{`${dayTime(s.start, plan.at, lang, zone)}${s.zone === null ? '' : ` ${s.zone}`}`}</title>
          </rect>
        ))}
        <text class="plan-axis" x="0" y={PV_Y + 8}>
          {t['plan.chart.pv']}
        </text>
        <text class="plan-axis" x="0" y={PV_Y + 20}>
          {`${formatNumber(peak, lang, 1)} kW`}
        </text>
        <polyline class="plan-pv" points={pv} fill="none" />
        {rows.map((d, i) => (
          <g key={d.device} data-device={d.device}>
            <text class="plan-axis plan-device-label" x="0" y={ROW_Y + i * ROW_H + BAR_H - 2}>
              {d.device}
            </text>
            {runsOf(d.entries).map((run) => (
              <rect
                key={run.from.getTime()}
                class="plan-bar"
                data-class={run.urgent ? 'urgent' : run.class === 'pv' ? 'pv' : 'zone'}
                x={x(run.from.getTime())}
                y={ROW_Y + i * ROW_H}
                width={Math.max(2, x(run.to.getTime()) - x(run.from.getTime()))}
                height={BAR_H}
                rx="2"
              >
                <title>{`${dayTime(run.from, plan.at, lang, zone)}–${dayTime(run.to, plan.at, lang, zone)} ${run.class} ${formatKwh(run.energyKwh, lang)}`}</title>
              </rect>
            ))}
          </g>
        ))}
        <line class="plan-now" x1={nowX} x2={nowX} y1="0" y2={ROW_Y + Math.max(1, rows.length) * ROW_H} />
        <text class="plan-axis" x={nowX + 3} y={height - 4}>
          {t['plan.chart.now']}
        </text>
        {ticks.map((s) => (
          <text key={`tick-${s.start.getTime()}`} class="plan-axis" x={x(s.start.getTime())} y={height - 4}>
            {dayTime(s.start, plan.at, lang, zone)}
          </text>
        ))}
      </svg>
    </div>
  );
}

function Legend({ t }: { t: Messages }) {
  return (
    <ul class="plan-legend" aria-label={t['plan.chart.zones']}>
      {(['cheap', 'middle', 'dear'] as const).map((tone) => (
        <li key={tone}>
          <span class="plan-swatch plan-zone" data-tone={tone} aria-hidden="true" /> {t[`plan.price.${tone}`]}
        </li>
      ))}
      {(['pv', 'zone', 'urgent'] as const).map((kind) => (
        <li key={kind}>
          <span class="plan-swatch plan-bar" data-class={kind} aria-hidden="true" /> {t[`plan.legend.${kind}`]}
        </li>
      ))}
    </ul>
  );
}

function DeviceBlock({ device, plan, ...ctx }: { device: PlanDevice; plan: Plan } & Ctx) {
  const { t, lang, zone } = ctx;
  const runs = runsOf(device.entries);
  const fresh = plan.status === 'fresh';
  const goals = device.goals;
  return (
    <div class="plan-device" data-device={device.device}>
      <h3>
        <code class="entity mono">{device.device}</code>
      </h3>
      {goals.map((goal, i) => (
        <GoalLines key={`${goal.source}-${goal.kind}-${goal.deadline?.getTime() ?? i}`} goal={goal} plan={plan} t={t} lang={lang} />
      ))}
      {!fresh && plan.enabled && <p class="sub">{t['plan.device.notFresh']}</p>}
      {plan.enabled && (
        <>
          <p class="plan-runs-title">{t['plan.device.runs']}</p>
          {runs.length === 0 ? (
            <p class="sub" data-device-runs="none">
              {t['plan.device.noRuns']}
            </p>
          ) : (
            <ul class="plainlist" data-device-runs="list">
              {runs.map((run) => (
                <li key={run.from.getTime()}>
                  <RunText run={run} now={plan.at} t={t} lang={lang} zone={zone} />
                </li>
              ))}
            </ul>
          )}
          {device.predictedCompletion !== null && (
            <p class="sub">{fill(t['plan.device.completion'], { time: dayTime(device.predictedCompletion, plan.at, lang, zone) })}</p>
          )}
          {device.shortfallKwh !== null && device.shortfallKwh > 1e-9 && <p class="sub">{fill(t['plan.device.shortfall'], { kwh: formatKwh(device.shortfallKwh, lang) })}</p>}
        </>
      )}
    </div>
  );
}

function Cost({ plan, ...ctx }: { plan: Plan } & Ctx) {
  const { t, lang } = ctx;
  const cost = plan.cost;
  return (
    <div data-card="plan-cost">
      <h3 class="plan-h3">{t['plan.cost.title']}</h3>
      {cost === null ? (
        <p class="sub">{t['plan.cost.none']}</p>
      ) : (
        <>
          <dl class="dash-kv plan-kv">
            <dt>{t['plan.cost.planned']}</dt>
            <dd data-row="cost-planned">
              <span class="mono">{costText(t, lang, cost.planned, cost.currency)}</span>
            </dd>
            <dt>{t['plan.cost.v2']}</dt>
            <dd data-row="cost-v2">
              <span class="mono">{costText(t, lang, cost.referenceV2, cost.currency)}</span>
            </dd>
            <dt>{t['plan.cost.asap']}</dt>
            <dd data-row="cost-asap">
              <span class="mono">{costText(t, lang, cost.referenceAsap, cost.currency)}</span>
            </dd>
            <dt>{t['plan.cost.diff']}</dt>
            <dd data-row="cost-diff" data-sign={cost.planned - cost.referenceV2 > 0.005 ? 'worse' : 'better'}>
              <span class="mono">{costText(t, lang, cost.planned - cost.referenceV2, cost.currency)}</span>
            </dd>
          </dl>
          <p class="dash-why">{t['plan.cost.note']}</p>
        </>
      )}
    </div>
  );
}

function SlotsTable({ plan, devices, ...ctx }: { plan: Plan; devices: PlanDevice[] } & Ctx) {
  const { t, lang, zone } = ctx;
  if (plan.slots.length === 0) return null;
  const tones = priceTones(plan.slots);
  const columns = devices.filter((d) => runsOf(d.entries).length > 0);
  return (
    <details class="plan-slots" data-card="plan-slots">
      <summary>{fill(t['plan.slots.summary'], { count: String(plan.slots.length) })}</summary>
      <div class="tablewrap">
        <table class="plan-table">
          <thead>
            <tr>
              <th scope="col">{t['plan.slots.col.time']}</th>
              <th scope="col">{t['plan.slots.col.zone']}</th>
              <th scope="col">{t['plan.slots.col.import']}</th>
              <th scope="col">{t['plan.slots.col.pv']}</th>
              <th scope="col">{t['plan.slots.col.house']}</th>
              <th scope="col">{t['plan.slots.col.surplus']}</th>
              {columns.map((d) => (
                <th key={d.device} scope="col" class="mono">
                  {d.device}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {plan.slots.map((s, i) => (
              <tr key={s.start.getTime()}>
                <th scope="row" class="mono">
                  {dayTime(s.start, plan.at, lang, zone)}
                </th>
                <td>
                  {s.zone === null ? <span class="sub-inline">{t['plan.slots.noZone']}</span> : <span class="mono">{s.zone}</span>}
                  <span class="plan-swatch plan-zone" data-tone={tones[i] as PriceTone} aria-hidden="true" />
                </td>
                <td class="num">{s.importPrice === null ? t['common.none'] : formatNumber(s.importPrice, lang, 4)}</td>
                <td class="num">{formatNumber(s.pvKwh, lang, 2)}</td>
                <td class="num">{formatNumber(s.baseKwh, lang, 2)}</td>
                <td class="num">{formatNumber(s.surplusKwh, lang, 2)}</td>
                {columns.map((d) => {
                  const e = d.entries.find((entry) => entry.start.getTime() === s.start.getTime());
                  return (
                    <td key={d.device} class="num" data-want={e?.want ?? 'none'}>
                      {e !== undefined && (e.want === 'run' || e.want === 'urgent') ? formatNumber(e.energyKwh, lang, 2) : t['common.none']}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </details>
  );
}

function Body({ plan, ev, ...ctx }: { plan: Plan; ev: EvControl } & Ctx) {
  const { t, lang, zone } = ctx;
  const planned = plan.devices.filter((d) => d.goals.length > 0);
  return (
    <>
      <Header plan={plan} {...ctx} />
      <EvCard control={ev} t={t} lang={lang} />
      <Inputs plan={plan} {...ctx} />
      <Notes plan={plan} t={t} />
      {plan.enabled && (
        <section class="card" aria-labelledby="plan-chart-title" data-card="plan-chart">
          <header class="cardhead">
            <h2 id="plan-chart-title">{plan.horizonEnd === null ? t['plan.title'] : fill(t['plan.chart.title'], { end: dayTime(plan.horizonEnd, plan.at, lang, zone) })}</h2>
          </header>
          <Chart plan={plan} devices={planned} {...ctx} />
          <Legend t={t} />
          <SlotsTable plan={plan} devices={planned} {...ctx} />
          <div class="plan-columns">
            <div>
              <h3 class="plan-h3">{t['plan.devices.title']}</h3>
              {planned.length === 0 ? <p class="sub">{t['plan.devices.none']}</p> : planned.map((d) => <DeviceBlock key={d.device} device={d} plan={plan} {...ctx} />)}
            </div>
            <Cost plan={plan} {...ctx} />
          </div>
        </section>
      )}
    </>
  );
}

export function PlanPage({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const { view } = usePlan();
  const ev = useEv();
  const zone = browserZone();
  return (
    <>
      {view.kind === 'live' ? (
        <Body plan={view.data} ev={ev} t={t} lang={lang} zone={zone} />
      ) : (
        <section class="card" aria-labelledby="plan-title">
          <header class="cardhead">
            <h2 id="plan-title">{t['plan.title']}</h2>
            <p class="sub">{t['plan.lead']}</p>
          </header>
        </section>
      )}
      <PollStatus view={view} evaluatedAt={view.kind === 'live' ? view.data.at : null} now={view.kind === 'live' ? view.data.at : undefined} lang={lang} t={t} />
    </>
  );
}
