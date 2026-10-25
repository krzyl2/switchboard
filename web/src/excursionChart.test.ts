import { describe, expect, it } from 'vitest';
import { parseExcursions, type Excursion } from './api/excursions';
import sample from './api/excursions.sample.json';
import { CHART_BOX, MARKER_RADIUS, excursionChart, tickStep } from './excursionChart';

const recorded = parseExcursions(sample).phases.find((p) => p.phase === 'L1')!.worst!;

function excursion(change: Partial<Excursion> = {}): Excursion {
  return {
    active: false,
    start: new Date('2026-10-25T09:00:30Z'),
    end: new Date('2026-10-25T09:02:00Z'),
    peakA: 31,
    peakAt: new Date('2026-10-25T09:01:00Z'),
    reserveThresholdA: 14.13,
    ratingA: 25,
    continuousA: 28.25,
    decimated: false,
    samples: [
      { at: new Date('2026-10-25T09:00:00Z'), a: 10 },
      { at: new Date('2026-10-25T09:00:30Z'), a: 20 },
      { at: new Date('2026-10-25T09:01:00Z'), a: 31 },
      { at: new Date('2026-10-25T09:02:00Z'), a: 12 },
    ],
    decisions: [],
    ...change,
  };
}

// The chart is the "real recorded excursion" of docs/05: a reader must be able to tell how far the
// current went past the thresholds and when Switchboard reacted. A scale that drifts would draw a
// 31 A peak under a 28.25 A line.
describe('excursion chart geometry', () => {
  it('draws higher currents higher, on one linear scale for the curve and the thresholds', () => {
    const g = excursionChart(excursion());
    const line = (kind: string) => g.limits.find((l) => l.kind === kind)!.y;

    // SVG y grows downwards: the breaker (25 A) is above the reserve (14.13 A), the continuous
    // limit (28.25 A) above the breaker.
    expect(line('continuous')).toBeLessThan(line('breaker'));
    expect(line('breaker')).toBeLessThan(line('reserve'));
    // Linear: equal steps in amperes are equal steps on screen.
    const perAmpere = (line('reserve') - line('breaker')) / (25 - 14.13);
    expect((line('breaker') - line('continuous')) / (28.25 - 25)).toBeCloseTo(perAmpere, 1);
    // The 31 A peak is drawn above the continuous limit, inside the plot.
    expect(g.path).toContain(`V${Math.round((CHART_BOX.bottom - ((31 - g.yMin) / (g.yMax - g.yMin)) * (CHART_BOX.bottom - CHART_BOX.top)) * 10) / 10}`);
    expect(g.yMax).toBeGreaterThan(31);
    expect(g.yMin).toBe(0);
  });

  // A meter reading holds until the next one: a linear ramp between readings would invent currents
  // that were never measured and shift the crossing of a threshold in time.
  it('draws the samples as steps that hold each reading until the next one', () => {
    const g = excursionChart(excursion());
    expect(g.path.startsWith(`M${CHART_BOX.left} `)).toBe(true);
    expect(g.path.match(/H/g)).toHaveLength(4);
    expect(g.path.match(/V/g)).toHaveLength(3);
    expect(g.path.endsWith(`H${CHART_BOX.right}`)).toBe(true);
  });

  it('spans the samples in time, with the first, middle and last instant on the axis', () => {
    const g = excursionChart(excursion());
    expect(g.from.toISOString()).toBe('2026-10-25T09:00:00.000Z');
    expect(g.to.toISOString()).toBe('2026-10-25T09:02:00.000Z');
    expect(g.xTicks.map((t) => t.at.toISOString())).toEqual(['2026-10-25T09:00:00.000Z', '2026-10-25T09:01:00.000Z', '2026-10-25T09:02:00.000Z']);
    expect(g.xTicks.map((t) => t.x)).toEqual([CHART_BOX.left, (CHART_BOX.left + CHART_BOX.right) / 2, CHART_BOX.right]);
  });

  // The shed moment is annotated on the curve: the marker sits on the reading in force at that
  // instant, not on a later one.
  it('puts each decision on the reading in force at its instant, numbered in time order', () => {
    const g = excursionChart(
      excursion({
        decisions: [
          { id: 'b', at: new Date('2026-10-25T09:01:30Z'), action: 'switch_off', reason: 'guard_tier_exceeded', device: 'heater_l1', commanded: false },
          { id: 'a', at: new Date('2026-10-25T09:00:40Z'), action: 'foreign_load_detected', reason: 'reserve_threshold', device: null, commanded: false },
        ],
      }),
    );
    expect(g.markers.map((m) => [m.number, m.decision.id, m.current])).toEqual([
      [1, 'a', 20],
      [2, 'b', 31],
    ]);
    expect(g.markers[1]!.x).toBeCloseTo(CHART_BOX.left + 0.75 * (CHART_BOX.right - CHART_BOX.left), 1);
  });

  // A reserve request and the reduction it causes share one instant: both markers must stay
  // visible, the later one stacked above the earlier, while both keep their point on the curve.
  it('stacks markers of one instant so none hides another', () => {
    const decisionsAt = (at: Date) =>
      (['request_reduction', 'set_setpoint', 'stop_increases'] as const).map((action, i) => ({
        id: String(i),
        at,
        action,
        reason: 'reserve_threshold' as const,
        device: action === 'set_setpoint' ? 'ev_charger' : null,
        commanded: false,
      }));
    // Mid-plot (20 A): the second marker goes just above the first.
    const middle = excursionChart(excursion({ decisions: decisionsAt(new Date('2026-10-25T09:00:40Z')) })).markers;
    expect(middle[0]!.markY).toBe(middle[0]!.y);
    expect(middle[1]!.y).toBe(middle[0]!.y);
    expect(middle[0]!.markY - middle[1]!.markY).toBeCloseTo(2 * MARKER_RADIUS + 1, 6);
    // Near the top (the 31 A peak) there is no room above: the free slots below are used instead.
    for (const markers of [middle, excursionChart(excursion({ decisions: decisionsAt(new Date('2026-10-25T09:01:30Z')) })).markers]) {
      for (const [one, other] of [[markers[0]!, markers[1]!], [markers[0]!, markers[2]!], [markers[1]!, markers[2]!]] as const) {
        expect(Math.abs(one.markY - other.markY)).toBeGreaterThanOrEqual(2 * MARKER_RADIUS);
      }
      for (const m of markers) {
        expect(m.markY).toBeGreaterThanOrEqual(CHART_BOX.top + MARKER_RADIUS);
        expect(m.markY).toBeLessThanOrEqual(CHART_BOX.bottom - MARKER_RADIUS);
      }
    }
  });

  it('keeps a decision before the first sample on the chart edge with the first reading', () => {
    const g = excursionChart(
      excursion({ decisions: [{ id: 'x', at: new Date('2026-10-25T08:59:00Z'), action: 'request_reduction', reason: 'reserve_threshold', device: null, commanded: false }] }),
    );
    expect(g.markers[0]).toMatchObject({ x: CHART_BOX.left, current: 10 });
    expect(g.markers[0]!.markY).toBe(g.markers[0]!.y);
  });

  // Labels of nearby lines must stay readable: when the limits sit close together on the scale, a
  // label moves below the previous one instead of covering it.
  it('never lets two threshold labels overlap', () => {
    const g = excursionChart(excursion({ peakA: 90, samples: [{ at: new Date('2026-10-25T09:00:00Z'), a: 90 }] }));
    const labels = g.limits.map((l) => l.labelY).sort((a, b) => a - b);
    for (let i = 1; i < labels.length; i++) expect(labels[i]! - labels[i - 1]!).toBeGreaterThanOrEqual(9 - 1e-9);
  });

  it('gives a single sample a readable minute of time axis', () => {
    const at = new Date('2026-10-25T09:00:00Z');
    const g = excursionChart(excursion({ start: at, end: null, active: true, peakAt: at, samples: [{ at, a: 15 }] }));
    expect(g.to.getTime() - g.from.getTime()).toBe(60_000);
  });

  // The breaker heats on export too (DECISIONS 2026-10-24): an export overload is drawn below zero
  // with the limits mirrored there, never folded onto the import side.
  it('draws an export overload below zero with the breaker and continuous limits mirrored', () => {
    const g = excursionChart(
      excursion({
        peakA: 30,
        samples: [
          { at: new Date('2026-10-25T09:00:00Z'), a: -20 },
          { at: new Date('2026-10-25T09:01:00Z'), a: -30 },
        ],
      }),
    );
    expect(g.yMin).toBeLessThan(-30);
    expect(g.mirrored.map((m) => m.kind)).toEqual(['breaker', 'continuous']);
    const zero = g.yTicks.find((t) => t.value === 0)!;
    for (const m of g.mirrored) expect(m.y).toBeGreaterThan(zero.y);
  });

  it('labels the current axis with round steps that include zero', () => {
    const g = excursionChart(excursion());
    expect(g.yTicks.map((t) => t.value)).toEqual([0, 10, 20, 30]);
    expect(tickStep(34.1)).toBe(10);
    expect(tickStep(4)).toBe(1);
    expect(tickStep(80)).toBe(20);
  });

  // The real recorded sample from the add-on's serializer (synthetic day, L1 at 09:20).
  it('lays out the recorded L1 excursion with its three decisions', () => {
    const g = excursionChart(recorded);
    expect(g.markers.map((m) => m.decision.action)).toEqual(['foreign_load_detected', 'request_reduction', 'set_setpoint']);
    expect(g.from.toISOString()).toBe(recorded.samples[0]!.at.toISOString());
    for (const m of g.markers) {
      expect(m.x).toBeGreaterThanOrEqual(CHART_BOX.left);
      expect(m.x).toBeLessThanOrEqual(CHART_BOX.right);
      expect(m.y).toBeGreaterThanOrEqual(CHART_BOX.top);
      expect(m.y).toBeLessThanOrEqual(CHART_BOX.bottom);
    }
  });
});
