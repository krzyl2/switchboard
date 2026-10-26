import { SIGNAL_DETAILS, SIGNAL_STATES } from './signals';
import type { SignalDetail, SignalState } from './signals';
import { bool, count, fail, field, instant, list, nullable, number, object, oneOf, text } from './strict';

// Contract of GET api/economics (src/Switchboard.Host/Web/EconomicsEndpoint.cs): the tariff in force
// now, the settlement as configured, per device the value now and its sources, and the savings
// (14-ECONOMICS "Savings"). Prices are in the currency label of the configuration, per kWh; this file
// never names a currency. EconomicsEndpointTests compares the closed lists below with the backend's
// enums and the keys of economics.sample.json with what the endpoint writes.

/** How a price at an instant came about (PriceBasis). */
export const PRICE_BASES = ['flat', 'zone', 'live', 'fallback'] as const;
/** The shape of a price spec: a number, a zone schedule, or a signal with a fallback. */
export const PRICE_KINDS = ['flat', 'zoned', 'dynamic'] as const;
/** How a device's value at an instant came about (ValueBasis). */
export const VALUE_BASES = ['static', 'followed', 'fallback'] as const;
/** Which price a device's value follows (ValueFollows). */
export const VALUE_FOLLOWS = ['static', 'import_price', 'export_price'] as const;
/** How a period is settled (SettlementModelId). */
export const SETTLEMENT_MODELS = ['hourly_netting', 'net_billing'] as const;
/** The energy class that allowed a device in the last dispatch step: surplus, the active zone, or neither. */
export const ENERGY_CLASSES = ['pv', 'zone', 'none'] as const;
/** What savings are measured against; the add-on's own words, which the UI localises. */
export const SAVINGS_BASES = ['against the controlled loads being off'] as const;

export type PriceBasis = (typeof PRICE_BASES)[number];
export type PriceKind = (typeof PRICE_KINDS)[number];
export type ValueBasis = (typeof VALUE_BASES)[number];
export type ValueFollows = (typeof VALUE_FOLLOWS)[number];
export type SettlementModel = (typeof SETTLEMENT_MODELS)[number];
export type EnergyClass = (typeof ENERGY_CLASSES)[number];
export type SavingsBasis = (typeof SAVINGS_BASES)[number];

/** A price signal a dynamic price follows, as the signal layer reports it (never an entity id). */
export interface PriceSignal {
  name: string;
  state: SignalState;
  detail: SignalDetail;
  /** Per kWh before the spec's add; null while the signal is not usable. */
  perKwh: number | null;
  ageS: number | null;
}

export interface TariffNow {
  kind: PriceKind;
  /** The zone id now; null for a flat price. */
  zone: string | null;
  importPrice: number | null;
  priceBasis: PriceBasis | null;
  /** The first minute whose zone differs; null for a flat price or when it does not change within a year. */
  nextChangeAt: Date | null;
  nextZone: string | null;
  signal: PriceSignal | null;
  fallbackInForce: boolean;
}

export interface ExportPrice {
  kind: PriceKind;
  zone: string | null;
  perKwh: number | null;
  priceBasis: PriceBasis | null;
  signal: PriceSignal | null;
  fallbackInForce: boolean;
}

export interface Settlement {
  model: SettlementModel;
  exportPrice: ExportPrice | null;
}

export interface EconomicsIssue {
  code: string;
  path: string;
  message: string;
}

export interface EconomicsDevice {
  id: string;
  /** The static value as written; also the fallback. */
  valuePerKwh: number;
  valueFollows: ValueFollows;
  valueFactor: number;
  /** The effective value now. */
  vEff: number;
  valueBasis: ValueBasis;
  /** The sources after narrowing, in the configuration grammar (pv_only, any, pv_and_zone:a,b); null when none are declared. */
  sources: string | null;
  sourcesDeclared: string | null;
  /** The class that allowed the device in the last dispatch step; null until a step ran. */
  energyClass: EnergyClass | null;
}

export interface SavingsDevice {
  id: string;
  energyKwh: number;
  valueGained: number;
  extraCost: number;
  savings: number;
}

export interface SavingsTotal {
  from: Date;
  to: Date;
  periods: number;
  fallbackPeriods: number;
  unpricedPeriods: number;
  incompletePeriods: number;
  valueGained: number;
  extraCost: number;
  /** Negative is shown, not hidden: it means the sources or the value are mis-set. */
  savings: number;
  /** Some counted period had a price at its fallback. */
  fallback: boolean;
  devices: SavingsDevice[];
}

export interface SavingsPeriod {
  start: Date;
  end: Date;
  valueGained: number;
  extraCost: number;
  savings: number;
  fallback: boolean;
  devices: SavingsDevice[];
}

export interface Savings {
  basis: SavingsBasis;
  /** The label as written in the configuration. */
  currency: string | null;
  model: SettlementModel;
  /** The start of the first counted period; null while none is counted. */
  since: Date | null;
  today: SavingsTotal;
  range: SavingsTotal | null;
  retained: SavingsTotal;
  /** The period in progress: apart from every total. */
  running: SavingsPeriod | null;
}

export interface Economics {
  at: Date;
  /** False without an economics: section (or an invalid document); the cards then say nothing. */
  configured: boolean;
  currency: string | null;
  tariff: TariffNow | null;
  settlement: Settlement | null;
  holidaysListStale: boolean;
  issues: EconomicsIssue[];
  devices: EconomicsDevice[] | null;
  savings: Savings | null;
}

const ECONOMICS_URL = 'api/economics';

const SOURCES = /^(pv_only|any|pv_and_zone:[^,\s:]+(,[^,\s:]+)*)$/;

function sources(raw: unknown, path: string): string {
  const value = text(raw, path);
  if (!SOURCES.test(value)) fail(path, 'pv_only, any or pv_and_zone:<id>[,<id>]');
  return value;
}

function priceSignal(raw: unknown, path: string): PriceSignal {
  const r = object(raw, path);
  return {
    name: text(field(r, 'name', path), `${path}.name`),
    state: oneOf(SIGNAL_STATES, field(r, 'state', path), `${path}.state`),
    detail: oneOf(SIGNAL_DETAILS, field(r, 'detail', path), `${path}.detail`),
    perKwh: nullable(field(r, 'perKwh', path), `${path}.perKwh`, number),
    ageS: nullable(field(r, 'ageS', path), `${path}.ageS`, number),
  };
}

function tariff(raw: unknown, path: string): TariffNow {
  const r = object(raw, path);
  return {
    kind: oneOf(PRICE_KINDS, field(r, 'kind', path), `${path}.kind`),
    zone: nullable(field(r, 'zone', path), `${path}.zone`, text),
    importPrice: nullable(field(r, 'importPrice', path), `${path}.importPrice`, number),
    priceBasis: nullable(field(r, 'priceBasis', path), `${path}.priceBasis`, (v, p) => oneOf(PRICE_BASES, v, p)),
    nextChangeAt: nullable(field(r, 'nextChangeAt', path), `${path}.nextChangeAt`, instant),
    nextZone: nullable(field(r, 'nextZone', path), `${path}.nextZone`, text),
    signal: nullable(field(r, 'signal', path), `${path}.signal`, priceSignal),
    fallbackInForce: bool(field(r, 'fallbackInForce', path), `${path}.fallbackInForce`),
  };
}

function exportPrice(raw: unknown, path: string): ExportPrice {
  const r = object(raw, path);
  return {
    kind: oneOf(PRICE_KINDS, field(r, 'kind', path), `${path}.kind`),
    zone: nullable(field(r, 'zone', path), `${path}.zone`, text),
    perKwh: nullable(field(r, 'perKwh', path), `${path}.perKwh`, number),
    priceBasis: nullable(field(r, 'priceBasis', path), `${path}.priceBasis`, (v, p) => oneOf(PRICE_BASES, v, p)),
    signal: nullable(field(r, 'signal', path), `${path}.signal`, priceSignal),
    fallbackInForce: bool(field(r, 'fallbackInForce', path), `${path}.fallbackInForce`),
  };
}

function settlement(raw: unknown, path: string): Settlement {
  const r = object(raw, path);
  return {
    model: oneOf(SETTLEMENT_MODELS, field(r, 'model', path), `${path}.model`),
    exportPrice: nullable(field(r, 'exportPrice', path), `${path}.exportPrice`, exportPrice),
  };
}

function issue(raw: unknown, path: string): EconomicsIssue {
  const r = object(raw, path);
  return {
    code: text(field(r, 'code', path), `${path}.code`),
    path: text(field(r, 'path', path), `${path}.path`),
    message: text(field(r, 'message', path), `${path}.message`),
  };
}

function device(raw: unknown, path: string): EconomicsDevice {
  const r = object(raw, path);
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    valuePerKwh: number(field(r, 'valuePerKwh', path), `${path}.valuePerKwh`),
    valueFollows: oneOf(VALUE_FOLLOWS, field(r, 'valueFollows', path), `${path}.valueFollows`),
    valueFactor: number(field(r, 'valueFactor', path), `${path}.valueFactor`),
    vEff: number(field(r, 'vEff', path), `${path}.vEff`),
    valueBasis: oneOf(VALUE_BASES, field(r, 'valueBasis', path), `${path}.valueBasis`),
    sources: nullable(field(r, 'sources', path), `${path}.sources`, sources),
    sourcesDeclared: nullable(field(r, 'sourcesDeclared', path), `${path}.sourcesDeclared`, sources),
    energyClass: nullable(field(r, 'class', path), `${path}.class`, (v, p) => oneOf(ENERGY_CLASSES, v, p)),
  };
}

function savingsDevice(raw: unknown, path: string): SavingsDevice {
  const r = object(raw, path);
  return {
    id: text(field(r, 'id', path), `${path}.id`),
    energyKwh: number(field(r, 'energyKwh', path), `${path}.energyKwh`),
    valueGained: number(field(r, 'valueGained', path), `${path}.valueGained`),
    extraCost: number(field(r, 'extraCost', path), `${path}.extraCost`),
    savings: number(field(r, 'savings', path), `${path}.savings`),
  };
}

function savingsTotal(raw: unknown, path: string): SavingsTotal {
  const r = object(raw, path);
  return {
    from: instant(field(r, 'from', path), `${path}.from`),
    to: instant(field(r, 'to', path), `${path}.to`),
    periods: count(field(r, 'periods', path), `${path}.periods`),
    fallbackPeriods: count(field(r, 'fallbackPeriods', path), `${path}.fallbackPeriods`),
    unpricedPeriods: count(field(r, 'unpricedPeriods', path), `${path}.unpricedPeriods`),
    incompletePeriods: count(field(r, 'incompletePeriods', path), `${path}.incompletePeriods`),
    valueGained: number(field(r, 'valueGained', path), `${path}.valueGained`),
    extraCost: number(field(r, 'extraCost', path), `${path}.extraCost`),
    savings: number(field(r, 'savings', path), `${path}.savings`),
    fallback: bool(field(r, 'fallback', path), `${path}.fallback`),
    devices: list(field(r, 'devices', path), `${path}.devices`, savingsDevice),
  };
}

function savingsPeriod(raw: unknown, path: string): SavingsPeriod {
  const r = object(raw, path);
  return {
    start: instant(field(r, 'start', path), `${path}.start`),
    end: instant(field(r, 'end', path), `${path}.end`),
    valueGained: number(field(r, 'valueGained', path), `${path}.valueGained`),
    extraCost: number(field(r, 'extraCost', path), `${path}.extraCost`),
    savings: number(field(r, 'savings', path), `${path}.savings`),
    fallback: bool(field(r, 'fallback', path), `${path}.fallback`),
    devices: list(field(r, 'devices', path), `${path}.devices`, savingsDevice),
  };
}

function savings(raw: unknown, path: string): Savings {
  const r = object(raw, path);
  return {
    basis: oneOf(SAVINGS_BASES, field(r, 'basis', path), `${path}.basis`),
    currency: nullable(field(r, 'currency', path), `${path}.currency`, text),
    model: oneOf(SETTLEMENT_MODELS, field(r, 'model', path), `${path}.model`),
    since: nullable(field(r, 'since', path), `${path}.since`, instant),
    today: savingsTotal(field(r, 'today', path), `${path}.today`),
    range: nullable(field(r, 'range', path), `${path}.range`, savingsTotal),
    retained: savingsTotal(field(r, 'retained', path), `${path}.retained`),
    running: nullable(field(r, 'running', path), `${path}.running`, savingsPeriod),
  };
}

export function parseEconomics(raw: unknown): Economics {
  const path = 'economics';
  const r = object(raw, path);
  return {
    at: instant(field(r, 'at', path), `${path}.at`),
    configured: bool(field(r, 'configured', path), `${path}.configured`),
    currency: nullable(field(r, 'currency', path), `${path}.currency`, text),
    tariff: nullable(field(r, 'tariff', path), `${path}.tariff`, tariff),
    settlement: nullable(field(r, 'settlement', path), `${path}.settlement`, settlement),
    holidaysListStale: bool(field(r, 'holidaysListStale', path), `${path}.holidaysListStale`),
    issues: list(field(r, 'issues', path), `${path}.issues`, issue),
    devices: nullable(field(r, 'devices', path), `${path}.devices`, (v, p) => list(v, p, device)),
    savings: nullable(field(r, 'savings', path), `${path}.savings`, savings),
  };
}

export async function fetchEconomics(signal: AbortSignal): Promise<Economics> {
  const response = await fetch(ECONOMICS_URL, { signal, cache: 'no-store', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new Error(`Economics request failed with HTTP ${response.status}.`);
  return parseEconomics(await response.json());
}
