import type { PhasePower, Power, PowerSource, Quantity } from './api/power';
import type { PhaseName } from './api/signals';

// Geometry of the flow view (screen 02, UI-07), kept apart from the page so its rules are testable
// without a browser: sources → main meter → board junction → one busbar lane per phase → the devices
// on that phase. Every lane has its own frame, its own feed from the junction and its own links; no
// path joins two lanes, because no current moves from one phase to another. All values come from one
// GET api/power answer; nothing here converts units.

/** The widest path: every phase at its breaker rating at once. One scale for the whole drawing. */
export const MAX_STROKE_PX = 24;
/** A path that carries any power stays visible. */
export const MIN_STROKE_PX = 1.5;
/** A link that carries nothing (a stopped device): a thin dashed line, never removed. */
export const STOPPED_STROKE_PX = 1;
/** Below this a power reads "0.00 kW" (formatKw's two decimals), so its path is drawn as stopped. */
export const SHOWN_AS_ZERO_W = 5;

export const FLOW_WIDTH = 840;
const MARGIN = 8;
const SOURCE = { x: 10, width: 170, height: 64, gap: 12 };
const JUNCTION = { x: 284, r: 7 };
const METER = { width: 96, height: 26, below: 34 };
const LANE = { x: 342, width: 490, pad: 10, gap: 22 };
const BUSBAR = { x: 354, width: 116, height: 96 };
const ROW = { x: 546, width: 278, height: 48, pitch: 58 };

export type LinkKind = 'pv' | 'grid' | 'storage' | 'feed' | 'controlled' | 'base' | 'foreign' | 'unattributed';
/** flowing: a power at or above SHOWN_AS_ZERO_W; stopped: nothing flows; unknown: the power is not known. */
export type FlowState = 'flowing' | 'stopped' | 'unknown';

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FlowLink {
  id: string;
  kind: LinkKind;
  /** The phase lane the link belongs to; null for the source trunks. */
  lane: PhaseName | null;
  /** Drawn in the direction the power flows, so the dash animation runs that way. */
  d: string;
  /** The flow leaves the board (grid export, storage charging): the path runs from the junction out. */
  outward: boolean;
  width: number;
  state: FlowState;
}

export interface SourceNode extends Box {
  kind: 'pv' | 'grid' | 'storage';
  quantity: Quantity;
  /** Into the board, out of it, or neither (nothing flows, or the power is unknown). */
  direction: 'in' | 'out' | null;
}

export interface Row extends Box {
  kind: 'device' | 'foreign' | 'house';
  /** The device id; null for the house and the foreign load. */
  device: string | null;
  /** The power on this phase in W; null when unknown. The house can be negative (model exceeds load). */
  w: number | null;
  state: FlowState;
  /** Where a device's power comes from; null for the house and the foreign load. */
  source: PowerSource | null;
  reportedAt: Date | null;
  /** How many phases the device draws from (a three-phase device has a row on each lane). */
  phaseCount: number;
}

export interface Lane {
  phase: PhasePower;
  frame: Box;
  busbar: Box;
  /** What the board feeds the phase: its load less storage (storage is drawn at the sources); null when unknown. */
  feedW: number | null;
  rows: Row[];
}

export interface FlowLayout {
  width: number;
  height: number;
  /** The power the widest path stands for: Σ phases of rating × voltage. */
  capacityW: number;
  junction: { x: number; y: number };
  meter: Box;
  sources: SourceNode[];
  lanes: Lane[];
  links: FlowLink[];
}

/** Width proportional to power on one scale; anything flowing stays at least MIN_STROKE_PX wide. */
export function strokeWidth(watts: number, capacityW: number): number {
  return Math.max(MIN_STROKE_PX, (MAX_STROKE_PX * watts) / capacityW);
}

function stateOf(w: number | null): FlowState {
  if (w === null) return 'unknown';
  return w >= SHOWN_AS_ZERO_W ? 'flowing' : 'stopped';
}

function widthOf(w: number | null, capacityW: number): number {
  const state = stateOf(w);
  if (state === 'unknown') return MIN_STROKE_PX;
  return state === 'stopped' ? STOPPED_STROKE_PX : strokeWidth(w!, capacityW);
}

const r1 = (v: number) => Math.round(v * 10) / 10;

/** A smooth path from one point to another, horizontal at both ends. */
function curve(x1: number, y1: number, x2: number, y2: number): string {
  const mx = r1((x1 + x2) / 2);
  return `M${r1(x1)},${r1(y1)} C${mx},${r1(y1)} ${mx},${r1(y2)} ${r1(x2)},${r1(y2)}`;
}

const centreY = (box: Box) => box.y + box.height / 2;

/** Load less storage, when both are known (storage not declared counts as none). */
function feedOf(phase: PhasePower): number | null {
  const { load, storage } = phase.balance;
  if (load.w === null) return null;
  if (storage.w !== null) return load.w - storage.w;
  return storage.unknown === 'not_declared' ? load.w : null;
}

function laneRows(power: Power, phase: PhasePower, storageIds: ReadonlySet<string>): Omit<Row, keyof Box>[] {
  const rows: Omit<Row, keyof Box>[] = [];
  for (const device of power.devices) {
    if (storageIds.has(device.id)) continue;
    for (const share of device.phases) {
      if (share.phase !== phase.phase) continue;
      rows.push({
        kind: 'device',
        device: device.id,
        w: share.powerW,
        state: stateOf(share.powerW),
        source: share.source,
        reportedAt: share.reportedAt,
        phaseCount: device.phases.length,
      });
    }
  }
  // A foreign load the add-on detected on this phase is split out of the house's own load. Only a
  // detected one: the bare step wanders around the baseline every sample, and a box keyed to it would
  // come and go, moving every row below it.
  const uncontrolled = phase.balance.uncontrolled;
  const foreign =
    phase.foreignDetectedAt !== null && uncontrolled.w !== null && phase.foreignStepW !== null && phase.foreignStepW >= SHOWN_AS_ZERO_W
      ? phase.foreignStepW
      : null;
  if (foreign !== null) {
    rows.push({ kind: 'foreign', device: null, w: foreign, state: 'flowing', source: null, reportedAt: phase.foreignDetectedAt, phaseCount: 1 });
  }
  const house = uncontrolled.w === null ? null : uncontrolled.w - (foreign ?? 0);
  rows.push({ kind: 'house', device: null, w: house, state: stateOf(house), source: null, reportedAt: uncontrolled.reportedAt, phaseCount: 1 });
  return rows;
}

function sourceFlow(kind: SourceNode['kind'], power: Power): { quantity: Quantity; inward: number | null } | null {
  const { generation, netImport, storage } = power.total;
  switch (kind) {
    case 'pv':
      return generation.unknown === 'not_declared' ? null : { quantity: generation, inward: generation.w };
    case 'grid':
      return { quantity: netImport, inward: netImport.w };
    case 'storage':
      // Storage is a consumer in the balance (charging positive); into the board is its discharge.
      return storage.unknown === 'not_declared' ? null : { quantity: storage, inward: storage.w === null ? null : -storage.w };
  }
}

/**
 * Lays out the whole schematic from one power picture. Storage devices (<paramref name="storageIds"/>)
 * are drawn as a source, not on their phase, so each lane carries its load less storage.
 */
export function layoutFlows(power: Power, storageIds: ReadonlySet<string> = new Set()): FlowLayout {
  const capacityW = power.phases.reduce((sum, p) => sum + p.ratingA * p.voltageV, 0);
  const flows = (['pv', 'grid', 'storage'] as const).flatMap((kind) => {
    const flow = sourceFlow(kind, power);
    return flow === null ? [] : [{ kind, ...flow }];
  });

  let top = MARGIN;
  const lanes: Lane[] = power.phases.map((phase) => {
    const rows = laneRows(power, phase, storageIds);
    const block = rows.length * ROW.pitch - (ROW.pitch - ROW.height);
    const content = Math.max(BUSBAR.height, block);
    const frame = { x: LANE.x, y: top, width: LANE.width, height: content + 2 * LANE.pad };
    const inner = top + LANE.pad;
    top += frame.height + LANE.gap;
    return {
      phase,
      frame,
      busbar: { x: BUSBAR.x, y: inner + (content - BUSBAR.height) / 2, width: BUSBAR.width, height: BUSBAR.height },
      feedW: feedOf(phase),
      rows: rows.map((row, i) => ({ ...row, x: ROW.x, y: inner + (content - block) / 2 + i * ROW.pitch, width: ROW.width, height: ROW.height })),
    };
  });

  const lanesBottom = lanes.length === 0 ? MARGIN : top - LANE.gap;
  const height = Math.max(lanesBottom + MARGIN, 2 * MARGIN + flows.length * (SOURCE.height + SOURCE.gap));
  const junction = { x: JUNCTION.x, y: height / 2 };
  const sources: SourceNode[] = flows.map((flow, i) => {
    const state = stateOf(flow.inward === null ? null : Math.abs(flow.inward));
    return {
      kind: flow.kind,
      quantity: flow.quantity,
      direction: state !== 'flowing' ? null : flow.inward! > 0 ? 'in' : 'out',
      x: SOURCE.x,
      y: (height * (i + 1)) / (flows.length + 1) - SOURCE.height / 2,
      width: SOURCE.width,
      height: SOURCE.height,
    };
  });

  const links: FlowLink[] = sources.map((source, i) => {
    const inward = flows[i]!.inward;
    const magnitude = inward === null ? null : Math.abs(inward);
    const outward = source.direction === 'out';
    const from = { x: source.x + source.width, y: centreY(source) };
    const to = { x: junction.x - JUNCTION.r, y: junction.y };
    return {
      id: source.kind,
      kind: source.kind,
      lane: null,
      d: outward ? curve(to.x, to.y, from.x, from.y) : curve(from.x, from.y, to.x, to.y),
      outward,
      width: widthOf(magnitude, capacityW),
      state: stateOf(magnitude),
    };
  });

  for (const lane of lanes) {
    const phase = lane.phase.phase;
    const busY = centreY(lane.busbar);
    links.push({
      id: `${phase}:feed`,
      kind: 'feed',
      lane: phase,
      d: curve(junction.x + JUNCTION.r, junction.y, lane.busbar.x, busY),
      outward: false,
      width: widthOf(lane.feedW, capacityW),
      state: stateOf(lane.feedW),
    });
    for (const row of lane.rows) {
      const kind: LinkKind =
        row.kind === 'device' ? 'controlled' : row.kind === 'foreign' ? 'foreign' : row.w === null ? 'unattributed' : 'base';
      links.push({
        id: `${phase}:${row.device ?? row.kind}`,
        kind,
        lane: phase,
        d: curve(lane.busbar.x + lane.busbar.width, busY, row.x, centreY(row)),
        outward: false,
        width: widthOf(row.w, capacityW),
        state: row.state,
      });
    }
  }

  return {
    width: FLOW_WIDTH,
    height,
    capacityW,
    junction,
    meter: { x: junction.x - METER.width / 2, y: junction.y + METER.below, width: METER.width, height: METER.height },
    sources,
    lanes,
    links,
  };
}
