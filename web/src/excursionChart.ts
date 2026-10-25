import type { Excursion, ExcursionDecision } from './api/excursions';

// Geometry of the Limits screen's excursion chart (docs/mockups screen 05, "Przebieg z dzisiejszego
// poranka"): current over time on one phase, the reserve threshold, the breaker rating and the
// continuous limit drawn, and the decisions taken marked on the curve. Pure, so the scale rules are
// tested without rendering. Coordinates are in the SVG's viewBox units.

export interface ChartBox {
  width: number;
  height: number;
  /** The plot area inside the viewBox: room for the axis labels left and below. */
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/** The mockup's 320 × 170 viewBox; the SVG scales with its container, so it never widens the page. */
export const CHART_BOX: ChartBox = { width: 320, height: 170, left: 30, right: 314, top: 12, bottom: 140 };

/** Threshold labels closer than this (viewBox units, about one text line) would overlap. */
const LABEL_GAP = 9;

/** Markers are circles of this radius; two closer than its diameter would hide one another. */
export const MARKER_RADIUS = 5;

/** A reference current drawn across the chart; `labelY` is where its label sits without overlapping another. */
export interface LimitLine {
  kind: 'reserve' | 'breaker' | 'continuous';
  value: number;
  y: number;
  labelY: number;
}

export interface ChartMarker {
  decision: ExcursionDecision;
  /** 1-based, in time order: the number drawn on the marker and in the list below the chart. */
  number: number;
  x: number;
  /** The point on the curve at the decision. */
  y: number;
  /** Where the marker is drawn: on the curve, or stacked above an earlier marker it would cover. */
  markY: number;
  /** The current the meter last reported at the decision (the curve holds a reading until the next one). */
  current: number;
}

export interface ExcursionGeometry {
  box: ChartBox;
  from: Date;
  to: Date;
  yMin: number;
  yMax: number;
  /** The samples as a step line: a reading holds until the next one, as the meter reported it. */
  path: string;
  limits: LimitLine[];
  /** The breaker and continuous lines on the export side, drawn only when the samples go there. */
  mirrored: { kind: 'breaker' | 'continuous'; y: number }[];
  yTicks: { value: number; y: number }[];
  xTicks: { at: Date; x: number }[];
  markers: ChartMarker[];
}

/** The shortest span drawn: a single sample still gets a readable time axis. */
const MIN_SPAN_MS = 60_000;
/** Headroom above the highest value drawn, so the top line and its label stay inside the plot. */
const TOP_MARGIN = 1.1;

const round1 = (value: number) => Math.round(value * 10) / 10;

/** A step of 1, 2 or 5 × 10^n that gives at most five intervals over the range. */
export function tickStep(range: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(range > 0 ? range : 1));
  for (const factor of [0.1, 0.2, 0.5, 1, 2, 5, 10]) {
    const step = factor * magnitude;
    if (range / step <= 5) return step;
  }
  return 10 * magnitude;
}

/** The current reported at an instant: the last sample at or before it, else the first one. */
function currentAt(samples: Excursion['samples'], at: Date): number {
  let value = samples[0]?.a ?? 0;
  for (const sample of samples) {
    if (sample.at.getTime() > at.getTime()) break;
    value = sample.a;
  }
  return value;
}

/** Labels from top to bottom, each pushed below the previous one when they would overlap. */
function placeLabels(lines: Omit<LimitLine, 'labelY'>[]): LimitLine[] {
  const sorted = [...lines].sort((a, b) => a.y - b.y);
  let previous = -Infinity;
  const placed = sorted.map((line) => {
    // Above its own line by default; below the previous label when that one is too close.
    const labelY = Math.max(line.y - 3, previous + LABEL_GAP);
    previous = labelY;
    return { ...line, labelY: round1(labelY) };
  });
  return lines.map((line) => placed.find((p) => p.kind === line.kind)!);
}

export function excursionChart(excursion: Excursion, box: ChartBox = CHART_BOX): ExcursionGeometry {
  const { samples } = excursion;
  const times = [
    ...samples.map((s) => s.at.getTime()),
    excursion.start.getTime(),
    excursion.peakAt.getTime(),
    ...(excursion.end === null ? [] : [excursion.end.getTime()]),
  ];
  const fromMs = Math.min(...times);
  const toMs = Math.max(Math.max(...times), fromMs + MIN_SPAN_MS);
  const values = samples.map((s) => s.a);

  // Import is positive. The thresholds always fit; the export side appears only when the samples go
  // there, and then reaches the mirrored limits once it is below the reserve threshold.
  const highest = Math.max(excursion.peakA, excursion.continuousA, excursion.ratingA, ...values);
  const lowest = Math.min(0, ...values);
  const yMax = highest * TOP_MARGIN;
  const yMin = lowest < -excursion.reserveThresholdA ? -Math.max(-lowest, excursion.continuousA) * TOP_MARGIN : lowest;

  const x = (atMs: number) => round1(box.left + ((Math.min(Math.max(atMs, fromMs), toMs) - fromMs) / (toMs - fromMs)) * (box.right - box.left));
  const y = (a: number) => round1(box.bottom - ((a - yMin) / (yMax - yMin)) * (box.bottom - box.top));

  let path = '';
  samples.forEach((sample, i) => {
    path += i === 0 ? `M${x(sample.at.getTime())} ${y(sample.a)}` : ` H${x(sample.at.getTime())} V${y(sample.a)}`;
  });
  if (samples.length > 0) path += ` H${x(toMs)}`;

  const limits = placeLabels([
    { kind: 'breaker', value: excursion.ratingA, y: y(excursion.ratingA) },
    { kind: 'continuous', value: excursion.continuousA, y: y(excursion.continuousA) },
    { kind: 'reserve', value: excursion.reserveThresholdA, y: y(excursion.reserveThresholdA) },
  ]);
  const mirrored = (
    [
      { kind: 'breaker', value: -excursion.ratingA },
      { kind: 'continuous', value: -excursion.continuousA },
    ] as const
  )
    .filter((line) => line.value >= yMin)
    .map((line) => ({ kind: line.kind, y: y(line.value) }));

  const step = tickStep(yMax - yMin);
  const yTicks: ExcursionGeometry['yTicks'] = [];
  for (let value = Math.ceil(yMin / step) * step; value <= yMax; value += step) {
    const tick = Math.round(value / step) * step;
    yTicks.push({ value: tick === 0 ? 0 : tick, y: y(tick) });
  }

  const middle = fromMs + (toMs - fromMs) / 2;
  const xTicks = [fromMs, middle, toMs].map((ms) => ({ at: new Date(ms), x: x(ms) }));

  // Decisions of one instant (a reserve request and the reduction it causes) would draw on top of
  // each other: a marker that would cover an earlier one moves to the nearest free slot above (or below) it.
  const markers: ChartMarker[] = [];
  for (const [i, decision] of [...excursion.decisions].sort((a, b) => a.at.getTime() - b.at.getTime()).entries()) {
    const current = currentAt(samples, decision.at);
    const mx = x(decision.at.getTime());
    const my = y(current);
    const free = (candidate: number) =>
      candidate >= box.top + MARKER_RADIUS &&
      candidate <= box.bottom - MARKER_RADIUS &&
      !markers.some((m) => Math.abs(m.x - mx) < 2 * MARKER_RADIUS && Math.abs(m.markY - candidate) < 2 * MARKER_RADIUS);
    // On the curve if free, else the nearest free slot above, then below (near the top of the plot).
    const slots = Array.from({ length: 2 * markers.length + 1 }, (_, k) => round1(my + (k % 2 === 1 ? -1 : 1) * Math.ceil(k / 2) * (2 * MARKER_RADIUS + 1)));
    const markY = slots.find(free) ?? my;
    markers.push({ decision, number: i + 1, x: mx, y: my, markY, current });
  }

  return { box, from: new Date(fromMs), to: new Date(toMs), yMin, yMax, path, limits, mirrored, yTicks, xTicks, markers };
}

/**
 * The decisions that reduce a device during an excursion: what the chart annotates as the shed (or,
 * in shadow mode, would-shed) moment. The add-on attaches these device actions only with a tier, a
 * low budget or the reserve threshold as their reason (ExcursionRecorder), so each one is a reduction.
 */
export const REDUCING_ACTIONS: ReadonlySet<ExcursionDecision['action']> = new Set(['switch_off', 'reduce_to_minimum', 'set_setpoint', 'deferred']);
