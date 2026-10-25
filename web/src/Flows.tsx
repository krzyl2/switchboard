import type { ComponentChildren } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import { deviceLabel, type DeviceConfig } from './api/devices';
import { fetchGuard, type GuardDevice, type GuardPhase, type GuardStatus, type PhaseGuardState } from './api/guard';
import type { Power, Quantity } from './api/power';
import type { Statistics } from './api/statistics';
import { GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS } from './config';
import { layoutFlows, type FlowLayout, type Lane, type Row, type SourceNode } from './flowLayout';
import './flows.styles.css';
import { formatAge, formatAmps, formatCountdown, formatKw, formatKwh, formatNumber, formatPercent } from './format';
import { fill, messages, type Lang, type Messages } from './i18n';
import { localClock } from './localTime';
import { Age, PollStatus, QuantityText, StateBadge, unknownReason, type Tone } from './ui';
import { useDevices } from './useDevices';
import { usePoll, type PollView } from './usePoll';
import { usePower } from './usePower';
import { useStatistics } from './useStatistics';

// Screen 02 — Flows (UI-07, UI-08, UI-11): where the power comes from and where it goes, drawn from
// one power picture (GET api/power) as one inline-SVG schematic on one scale, then the headroom of
// each phase (GET api/guard, the guard's own figures) and where PV went in the last hour (GET
// api/statistics). The three phases are drawn as separate lanes that no path joins.

const HOUR_MS = 3_600_000;
const REDUCED_MOTION = '(prefers-reduced-motion: reduce)';
// Rough character budgets of the SVG text lines at their font sizes (the drawing scales as a whole).
const SOURCE_LINE_CHARS = 28;
const DEVICE_LINE_CHARS = 48;
const NAME_CHARS = 30;

type Props = { t: Messages; lang: Lang };

const PHASE_TONE: Record<PhaseGuardState, Tone> = {
  unknown: 'muted',
  normal: 'ok',
  hold: 'warn',
  reducing: 'warn',
  shedding: 'crit',
  safe_state: 'crit',
};

/** Follows the operating system's reduced-motion setting, also when it changes while the page is open. */
function useReducedMotion(): boolean {
  const query = () => (typeof window.matchMedia === 'function' ? window.matchMedia(REDUCED_MOTION) : null);
  const [reduced, setReduced] = useState(() => query()?.matches ?? false);
  useEffect(() => {
    const media = query();
    if (media === null) return;
    const onChange = () => setReduced(media.matches);
    media.addEventListener('change', onChange);
    return () => media.removeEventListener('change', onChange);
  }, []);
  return reduced;
}

function clip(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

/** At most two lines of about <paramref name="max"/> characters, broken between words. */
function wrap(text: string, max: number): string[] {
  if (text.length <= max) return [text];
  const words = text.split(' ');
  let first = '';
  let i = 0;
  while (i < words.length && (first === '' ? 0 : first.length + 1) + words[i]!.length <= max) {
    first = first === '' ? words[i]! : `${first} ${words[i]}`;
    i++;
  }
  if (first === '') return [clip(text, max)];
  return [first, clip(words.slice(i).join(' '), max)];
}

function age(reportedAt: Date, now: Date, lang: Lang): string {
  return formatAge(Math.max(0, (now.getTime() - reportedAt.getTime()) / 1000), lang);
}

function FlowsHead({ t }: { t: Messages }) {
  return (
    <header class="cardhead">
      <h2 id="flows-title">{t['flows.title']}</h2>
      <p class="sub">{t['flows.lead']}</p>
    </header>
  );
}

export function Flows({ lang }: { lang: Lang }) {
  const t = messages[lang];
  const view = usePower();
  if (view.kind === 'live') return <LiveFlows power={view.data} view={view} t={t} lang={lang} />;
  // Without a power picture nothing is drawn: a frozen schematic next to a failure would read as calm.
  return (
    <section class="card" aria-labelledby="flows-title">
      <FlowsHead t={t} />
      <PollStatus view={view} lang={lang} t={t} />
    </section>
  );
}

/** Mounted only while a power picture exists, so the other requests start only with something to draw. */
function LiveFlows({ power, view, t, lang }: Props & { power: Power; view: PollView<Power> }) {
  const reducedMotion = useReducedMotion();
  const guard = usePoll(fetchGuard, GUARD_REFRESH_INTERVAL_MS, GUARD_REQUEST_TIMEOUT_MS);
  const devices = useDevices();
  // Only lastHour is drawn, and the add-on sums it over all its buckets whatever the range: asking for
  // the current hour of the add-on's clock keeps the other buckets out of every answer.
  const hour = Math.floor(power.at.getTime() / HOUR_MS) * HOUR_MS;
  const statistics = useStatistics({ from: new Date(hour), to: new Date(hour + HOUR_MS) });

  const configs = devices.kind === 'live' ? devices.data.devices : null;
  const storageIds = new Set((configs ?? []).filter((d) => d.type === 'storage').map((d) => d.id));
  const layout = layoutFlows(power, storageIds);
  return (
    <>
      <section class="card" aria-labelledby="flows-title">
        <FlowsHead t={t} />
        <PollStatus view={view} evaluatedAt={power.evaluatedAt} now={power.at} lang={lang} t={t} />
        {power.invalidReason !== null && (
          <p class="note" role="status">
            {fill(t['flows.invalid'], { reason: t[`powerUnknown.${power.invalidReason}`] })}
          </p>
        )}
        <Tiles power={power} t={t} lang={lang} />
        {layout.lanes.length > 0 && (
          <Diagram
            layout={layout}
            power={power}
            configs={configs}
            guardDevices={guard.kind === 'live' ? guard.data.devices : []}
            reducedMotion={reducedMotion}
            t={t}
            lang={lang}
          />
        )}
        <Legend layout={layout} t={t} lang={lang} />
        <p class="flowcaption">{t['flows.independent']}</p>
      </section>
      <div class="flowgrid">
        <HeadroomCard view={guard} t={t} lang={lang} />
        <PvCard view={statistics} power={power} configs={configs} storageIds={storageIds} t={t} lang={lang} />
      </div>
    </>
  );
}

function Tile({ label, children, note }: { label: string; children: ComponentChildren; note?: ComponentChildren }) {
  return (
    <div class="tile">
      <p class="label">{label}</p>
      <p class="bignum">{children}</p>
      {note !== undefined && <p class="sub">{note}</p>}
    </div>
  );
}

/** The magnitude of a signed figure (the label says the direction); an unknown stays unknown. */
const magnitude = (q: Quantity): Quantity => (q.w === null ? q : { ...q, w: Math.abs(q.w) });

/** The phase with the highest current against its rating (the breaker heats either way). */
function tightestPhase(power: Power) {
  let best: (typeof power.phases)[number] | null = null;
  for (const phase of power.phases) {
    if (phase.currentA === null) continue;
    if (best === null || Math.abs(phase.currentA) / phase.ratingA > Math.abs(best.currentA!) / best.ratingA) best = phase;
  }
  return best;
}

function Tiles({ power, t, lang }: Props & { power: Power }) {
  const { generation, generationNote, netImport, storage } = power.total;
  const reported = (q: Quantity) => <Age reportedAt={q.reportedAt} now={power.at} lang={lang} t={t} template="age.reported" />;
  const tightest = tightestPhase(power);
  return (
    <div class="flowstats">
      <Tile label={t['flows.tile.production']} note={generationNote !== null ? t[`generationNote.${generationNote}`] : reported(generation)}>
        <QuantityText quantity={generation} t={t} lang={lang} />
      </Tile>
      <Tile
        label={netImport.w === null ? t['flows.tile.grid'] : netImport.w < 0 ? t['flows.tile.gridExport'] : t['flows.tile.gridImport']}
        note={reported(netImport)}
      >
        <QuantityText quantity={magnitude(netImport)} t={t} lang={lang} />
      </Tile>
      {storage.unknown !== 'not_declared' && (
        <Tile
          label={storage.w === null ? t['flows.tile.storage'] : storage.w < 0 ? t['flows.tile.storageDischarging'] : t['flows.tile.storageCharging']}
          note={reported(storage)}
        >
          <QuantityText quantity={magnitude(storage)} t={t} lang={lang} />
        </Tile>
      )}
      <Tile
        label={t['flows.tile.tightest']}
        note={
          tightest === null ? undefined : (
            <>
              {fill(t['bar.ofLimit'], { percent: formatPercent(Math.abs(tightest.currentA!) / tightest.ratingA, lang) })}{' '}
              <Age reportedAt={tightest.currentReportedAt} now={power.at} lang={lang} t={t} template="age.reported" />
            </>
          )
        }
      >
        <span class="mono">{tightest === null ? t['common.none'] : `${tightest.phase} · ${formatAmps(Math.abs(tightest.currentA!), lang)}`}</span>
      </Tile>
    </div>
  );
}

function Lines({ lines, x, y, step }: { lines: string[]; x: number; y: number; step: number }) {
  return (
    <>
      {lines.map((line, i) => (
        <text key={i} class="nsub" x={x} y={y + i * step}>
          {line}
        </text>
      ))}
    </>
  );
}

function SourceBox({ source, power, t, lang }: Props & { source: SourceNode; power: Power }) {
  const { x, y, width, height, quantity } = source;
  let sub: string;
  if (quantity.w === null) sub = unknownReason(t, quantity.unknown ?? 'power_not_fresh');
  else if (source.kind === 'pv' && power.total.generationNote !== null) sub = t[`generationNote.${power.total.generationNote}`];
  else {
    const direction =
      source.direction === null
        ? null
        : source.kind === 'grid'
          ? t[`direction.${source.direction === 'in' ? 'import' : 'export'}`]
          : source.kind === 'storage'
            ? t[`flows.node.${source.direction === 'in' ? 'discharging' : 'charging'}`]
            : null;
    const reported = quantity.reportedAt === null ? null : fill(t['age.reported'], { age: age(quantity.reportedAt, power.at, lang) });
    sub = [direction, reported].filter((part) => part !== null).join(' · ');
  }
  return (
    <g class="node source" data-kind={source.kind} data-direction={source.direction ?? undefined}>
      <rect class="nbox" x={x} y={y} width={width} height={height} rx={4} />
      <text class="nttl" x={x + 12} y={y + 20}>
        {t[`flows.node.${source.kind}`]}
      </text>
      <text class="nval" x={x + width - 12} y={y + 20} text-anchor="end">
        {quantity.w === null ? t['source.unknown'] : formatKw(Math.abs(quantity.w), lang)}
      </text>
      <Lines lines={wrap(sub, SOURCE_LINE_CHARS)} x={x + 12} y={y + 38} step={12} />
    </g>
  );
}

function rowText(row: Row, lane: Lane, power: Power, configs: DeviceConfig[] | null, guardDevices: GuardDevice[], { t, lang }: Props) {
  if (row.kind === 'house') {
    const uncontrolled = lane.phase.balance.uncontrolled;
    const names = lane.phase.balance.unknownDevices.map((id) => deviceLabel(configs, id));
    const sub =
      row.w === null
        ? unknownReason(t, uncontrolled.unknown ?? 'power_not_fresh', names)
        : lane.phase.balance.modelExceedsLoad
          ? t['flows.node.modelExceedsLoad']
          : t['flows.node.houseSub'];
    return { name: fill(t['flows.node.house'], { phase: lane.phase.phase }), sub, title: null };
  }
  if (row.kind === 'foreign') {
    // A foreign row exists only for a detected foreign load (flowLayout), so it always has its instant.
    const parts = [fill(t['flows.node.foreignDetected'], { age: age(row.reportedAt!, power.at, lang) })];
    if (lane.phase.foreignBaselineW !== null) parts.push(fill(t['flows.node.foreignAbove'], { baseline: formatKw(lane.phase.foreignBaselineW, lang) }));
    return { name: t['flows.node.foreign'], sub: parts.join(' · '), title: null };
  }
  const label = deviceLabel(configs, row.device!);
  const parts: string[] = [];
  if (row.phaseCount > 1) parts.push(fill(t['flows.device.phases'], { count: String(row.phaseCount) }));
  if (row.source === 'measured') {
    parts.push(row.reportedAt === null ? t['source.measured'] : fill(t['flows.device.measured'], { age: age(row.reportedAt, power.at, lang) }));
  } else if (row.source === 'rated_model') {
    parts.push(row.state === 'flowing' ? t['flows.device.ratedOn'] : t['flows.device.reportedOff']);
  } else {
    parts.push(t['flows.device.unknown']);
  }
  const guard = guardDevices.find((d) => d.id === row.device);
  if (guard !== undefined && guard.minOffRemainingS > 0) parts.push(fill(t['flows.device.lockoutOff'], { time: formatCountdown(guard.minOffRemainingS) }));
  if (guard !== undefined && guard.minOnRemainingS > 0) parts.push(fill(t['flows.device.lockoutOn'], { time: formatCountdown(guard.minOnRemainingS) }));
  return { name: clip(label, NAME_CHARS), sub: parts.join(' · '), title: label };
}

function RowBox({ row, lane, power, configs, guardDevices, t, lang }: Props & { row: Row; lane: Lane; power: Power; configs: DeviceConfig[] | null; guardDevices: GuardDevice[] }) {
  const text = rowText(row, lane, power, configs, guardDevices, { t, lang });
  const kind = row.kind === 'device' ? 'controlled' : row.kind === 'foreign' ? 'foreign' : 'base';
  return (
    <g class="node" data-kind={kind} data-state={row.state} data-device={row.device ?? undefined}>
      {text.title !== null && <title>{text.title}</title>}
      <rect class="nbox" x={row.x} y={row.y} width={row.width} height={row.height} rx={4} />
      <text class="nttl" x={row.x + 12} y={row.y + 18}>
        {text.name}
      </text>
      <text class="nval" x={row.x + row.width - 12} y={row.y + 18} text-anchor="end">
        {row.w === null ? t['source.unknown'] : formatKw(row.w, lang)}
      </text>
      <Lines lines={wrap(text.sub, DEVICE_LINE_CHARS)} x={row.x + 12} y={row.y + 32} step={11} />
    </g>
  );
}

function Busbar({ lane, t, lang }: Props & { lane: Lane }) {
  const { x, y, width, height } = lane.busbar;
  const phase = lane.phase;
  const current = phase.currentA === null ? null : Math.abs(phase.currentA);
  // The strip spans 0 to the continuous limit, with the reserve threshold and the rating marked.
  const strip = { x: x + 10, y: y + 84, width: width - 20, height: 6 };
  const at = (amperes: number) => strip.x + (Math.min(amperes, phase.continuousA) / phase.continuousA) * strip.width;
  const tone: Tone = current === null ? 'muted' : current > phase.ratingA ? 'crit' : phase.overReserve ? 'warn' : 'ok';
  const direction = phase.direction === 'import' || phase.direction === 'export' ? t[`direction.${phase.direction}`] : null;
  return (
    <g class="busbar" data-phase={phase.phase}>
      <rect class="nbox" x={x} y={y} width={width} height={height} rx={4} />
      <text class="phl" x={x + 10} y={y + 22}>
        {phase.phase}
      </text>
      <text class="nval" x={x + 10} y={y + 40}>
        {current === null ? t['common.none'] : formatAmps(current, lang)}
        {direction !== null && <tspan class="nsub">{` ${direction}`}</tspan>}
      </text>
      <text class="nsub" x={x + 10} y={y + 56}>
        {fill(t['bar.breaker'], { value: formatAmps(phase.ratingA, lang) })}
      </text>
      <text class="nsub" x={x + 10} y={y + 70}>
        {fill(t['flows.busbar.load'], { power: lane.feedW === null ? t['source.unknown'] : formatKw(lane.feedW, lang) })}
      </text>
      <rect class="strip" x={strip.x} y={strip.y} width={strip.width} height={strip.height} />
      {current !== null && <rect class="stripfill" data-tone={tone} x={strip.x} y={strip.y} width={at(current) - strip.x} height={strip.height} />}
      <line class="striptick reserve" x1={at(phase.reserveThresholdA)} x2={at(phase.reserveThresholdA)} y1={strip.y - 3} y2={strip.y + strip.height + 3} />
      <line class="striptick" x1={at(phase.ratingA)} x2={at(phase.ratingA)} y1={strip.y - 3} y2={strip.y + strip.height + 3} />
    </g>
  );
}

function Diagram({
  layout,
  power,
  configs,
  guardDevices,
  reducedMotion,
  t,
  lang,
}: Props & { layout: FlowLayout; power: Power; configs: DeviceConfig[] | null; guardDevices: GuardDevice[]; reducedMotion: boolean }) {
  const { junction, meter } = layout;
  return (
    <div class="flowscroll">
      <svg class="flowsvg" viewBox={`0 0 ${layout.width} ${layout.height}`} role="img" aria-label={t['flows.diagram']}>
        {layout.links.map((link) => (
          <path
            key={link.id}
            // Nothing moves under reduced motion; a stopped or unknown flow never moves.
            class={link.state === 'flowing' && !reducedMotion ? 'flow anim' : 'flow'}
            d={link.d}
            stroke-width={link.width}
            data-flow={link.id}
            data-kind={link.kind}
            data-state={link.state}
            data-lane={link.lane ?? undefined}
            data-outward={link.outward ? 'true' : undefined}
          />
        ))}
        <line class="meterlink" x1={junction.x} y1={meter.y} x2={junction.x} y2={junction.y} />
        <rect class="meterbox" x={meter.x} y={meter.y} width={meter.width} height={meter.height} rx={3} />
        <text class="nsub" x={junction.x} y={meter.y + 17} text-anchor="middle">
          {t['flows.node.meter']}
        </text>
        <circle class="junction" cx={junction.x} cy={junction.y} r={7} />
        <text class="nsub" x={junction.x} y={junction.y - 14} text-anchor="middle">
          {t['flows.node.junction']}
        </text>
        {layout.sources.map((source) => (
          <SourceBox key={source.kind} source={source} power={power} t={t} lang={lang} />
        ))}
        {layout.lanes.map((lane) => (
          <g key={lane.phase.phase} class="lane" data-phase={lane.phase.phase}>
            <rect class="laneframe" x={lane.frame.x} y={lane.frame.y} width={lane.frame.width} height={lane.frame.height} rx={6} />
            <Busbar lane={lane} t={t} lang={lang} />
            {lane.rows.map((row) => (
              <RowBox key={row.device ?? row.kind} row={row} lane={lane} power={power} configs={configs} guardDevices={guardDevices} t={t} lang={lang} />
            ))}
          </g>
        ))}
      </svg>
    </div>
  );
}

function Legend({ layout, t, lang }: Props & { layout: FlowLayout }) {
  const has = (kind: SourceNode['kind']) => layout.sources.some((s) => s.kind === kind);
  const items: [string, string][] = [
    ...(has('pv') ? [['pv', t['flows.legend.pv']] as [string, string]] : []),
    ['grid', t['flows.legend.grid']],
    ...(has('storage') ? [['storage', t['flows.legend.storage']] as [string, string]] : []),
    ['feed', t['flows.legend.feed']],
    ['controlled', t['segment.controlled']],
    ['base', t['flows.legend.base']],
    ['foreign', t['segment.foreign']],
    ['unknown', t['flows.legend.unknown']],
    ['stopped', t['flows.legend.stopped']],
  ];
  return (
    <>
      <div class="flowlegend">
        {items.map(([kind, label]) => (
          <span key={kind}>
            <i data-kind={kind} aria-hidden="true" />
            {label}
          </span>
        ))}
      </div>
      {layout.capacityW > 0 && <p class="updated">{fill(t['flows.scale'], { capacity: formatKw(layout.capacityW, lang) })}</p>}
    </>
  );
}

/** A headroom in A; negative stays negative (how far the phase is over). */
function HeadroomCell({ value, sub, t, lang }: Props & { value: number | null; sub?: string }) {
  return (
    <td class={value !== null && value < 0 ? 'num neg' : 'num'}>
      {value === null ? t['common.none'] : formatAmps(value, lang)}
      {sub !== undefined && <span class="sub">{sub}</span>}
    </td>
  );
}

function HeadroomRow({ phase, at, t, lang }: Props & { phase: GuardPhase; at: Date }) {
  const direction = phase.direction === 'export' || phase.direction === 'unknown' ? t[`direction.${phase.direction}`] : null;
  return (
    <tr data-phase={phase.phase}>
      <th scope="row">{phase.phase}</th>
      <td class="num">
        {phase.currentA === null ? t['common.none'] : formatAmps(Math.abs(phase.currentA), lang)}
        {direction !== null && <span class="sub">{direction}</span>}
        <span class="sub">
          <Age reportedAt={phase.reportedAt} now={at} lang={lang} t={t} />
        </span>
      </td>
      <HeadroomCell value={phase.headroomA} sub={fill(t['flows.headroom.threshold'], { value: formatAmps(phase.reserveThresholdA, lang) })} t={t} lang={lang} />
      <HeadroomCell value={phase.breakerHeadroomA} t={t} lang={lang} />
      <td>
        <StateBadge tone={PHASE_TONE[phase.state]} label={t[`phaseState.${phase.state}`]} />
        {phase.invalidReason !== null && <span class="sub">{t[`invalidReason.${phase.invalidReason}`]}</span>}
      </td>
    </tr>
  );
}

/**
 * UI-08 from the guard's own figures: headroom to the reserve threshold and to the continuous current
 * (the breaker's limit for sustained load, the l<n>_breaker_headroom entity), never to the bare rating.
 */
function HeadroomCard({ view, t, lang }: Props & { view: PollView<GuardStatus> }) {
  const guard = view.kind === 'live' ? view.data : null;
  const breaker = guard?.breaker ?? null;
  const continuous = breaker?.continuousA ?? guard?.phases[0]?.continuousA ?? null;
  return (
    <section class="card" aria-labelledby="flows-headroom-title">
      <header class="cardhead">
        <h2 id="flows-headroom-title">{t['flows.headroom.title']}</h2>
        {breaker !== null && (
          <p class="sub">
            {fill(t.breakerSummary, {
              rating: formatAmps(breaker.ratingA, lang),
              characteristic: breaker.characteristic === 'unknown' ? t.characteristicUnknown : breaker.characteristic,
              continuous: formatAmps(breaker.continuousA, lang),
              multiple: formatNumber(breaker.continuousMultiple, lang, 2),
            })}
          </p>
        )}
      </header>
      <PollStatus view={view} lang={lang} t={t} />
      {guard !== null && continuous !== null && (
        <>
          <div class="tablewrap">
            <table>
              <thead>
                <tr>
                  <th scope="col">{t.colPhase}</th>
                  <th scope="col">{t['flows.headroom.now']}</th>
                  <th scope="col">{t['flows.headroom.toReserve']}</th>
                  <th scope="col">{fill(t['flows.headroom.toContinuous'], { value: formatAmps(continuous, lang) })}</th>
                  <th scope="col">{t.colState}</th>
                </tr>
              </thead>
              <tbody>
                {guard.phases.map((phase) => (
                  <HeadroomRow key={phase.phase} phase={phase} at={guard.at} t={t} lang={lang} />
                ))}
              </tbody>
            </table>
          </div>
          <p class="note">{t['flows.headroom.signed']}</p>
          {guard.mode.effective === 'shadow' && <p class="note">{t['flows.headroom.shadow']}</p>}
        </>
      )}
    </section>
  );
}

type PvProps = Props & { power: Power; configs: DeviceConfig[] | null; storageIds: ReadonlySet<string> };

/** Storage devices are left out of the device rows: their share is the storage row. */
function PvTable({ statistics, power, configs, storageIds, t, lang }: PvProps & { statistics: Statistics }) {
  const { lastHour } = statistics;
  const totals = lastHour.totals;
  if (lastHour.buckets === 0) return <p class="note">{t['flows.pv.noData']}</p>;
  if (totals.generationKwh <= 0) return <p class="note">{t['flows.pv.noProduction']}</p>;
  const rows: [string, string, number][] = [
    ...statistics.devices
      .filter((d) => !storageIds.has(d.id))
      .map((d): [string, string, number] => [d.id, d.name ?? deviceLabel(configs, d.id), totals.devices.find((e) => e.id === d.id)?.pvKwh ?? 0]),
    ['house', t['flows.pv.house'], totals.pv.houseKwh],
  ];
  if (power.total.storage.unknown !== 'not_declared' || totals.pv.storageKwh > 0) rows.push(['storage', t['flows.pv.storage'], totals.pv.storageKwh]);
  rows.push(['export', t['flows.pv.export'], totals.pv.exportKwh]);
  if (totals.pv.unattributedKwh > 0) rows.push(['unattributed', t['flows.pv.unattributed'], totals.pv.unattributedKwh]);
  const share = (kwh: number) => formatPercent(kwh / totals.generationKwh, lang);
  return (
    <>
      <div class="tablewrap">
        <table>
          <thead>
            <tr>
              <th scope="col">{t['flows.pv.recipient']}</th>
              <th scope="col">{t['flows.pv.energy']}</th>
              <th scope="col">{t['flows.pv.share']}</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(([key, label, kwh]) => (
              <tr key={key} data-recipient={key}>
                <th scope="row">{label}</th>
                <td class="num">{formatKwh(kwh, lang)}</td>
                <td class="num">{share(kwh)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr data-recipient="total">
              <th scope="row">{t['flows.pv.total']}</th>
              <td class="num">{formatKwh(totals.generationKwh, lang)}</td>
              <td class="num">{share(totals.generationKwh)}</td>
            </tr>
          </tfoot>
        </table>
      </div>
      <p class="note">{t['flows.pv.method']}</p>
    </>
  );
}

function PvCard({ view, power, configs, storageIds, t, lang }: PvProps & { view: PollView<Statistics> }) {
  const statistics = view.kind === 'live' ? view.data : null;
  const hour = statistics?.lastHour ?? null;
  const declared = power.total.generation.unknown !== 'not_declared';
  return (
    <section class="card" aria-labelledby="flows-pv-title">
      <header class="cardhead">
        <h2 id="flows-pv-title">{t['flows.pv.title']}</h2>
        {declared && hour !== null && hour.buckets > 0 && (
          <p class="sub">
            {fill(t['flows.pv.since'], { time: localClock(hour.from, lang) })} ·{' '}
            {fill(t['flows.pv.observed'], { minutes: formatNumber(hour.totals.observedS / 60, lang, 0) })}
          </p>
        )}
      </header>
      {!declared ? (
        <p class="note">{t['flows.pv.notDeclared']}</p>
      ) : (
        <>
          <PollStatus view={view} lang={lang} t={t} />
          {statistics !== null && <PvTable statistics={statistics} power={power} configs={configs} storageIds={storageIds} t={t} lang={lang} />}
          {statistics !== null && statistics.integratedTo !== null && (
            <p class="updated">
              <time dateTime={statistics.integratedTo.toISOString()}>{fill(t['flows.pv.counted'], { age: age(statistics.integratedTo, statistics.at, lang) })}</time>
            </p>
          )}
        </>
      )}
    </section>
  );
}
