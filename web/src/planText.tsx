import { Fragment } from 'preact';
import type { PlanEntry, PlanSlot } from './api/plan';
import { formatFixed, formatKw, formatKwh, roundTo } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { localClock } from './localTime';

// Small pure helpers the day-plan screen and the device card's goal row share: runs made of consecutive entries, the words for an
// energy class, a clock time that names its day when it is not today, and the tariff zones ranked by price. Local time only here,
// at the presentation edge, in an explicit zone.

/** A stretch of consecutive slots in which a device is planned to run on one energy class. */
export interface Run {
  from: Date;
  to: Date;
  /** `pv` or `zone:<id>`. */
  class: string;
  urgent: boolean;
  energyKwh: number;
  maxPowerW: number;
}

/** Joins entries that follow one another with the same class and urgency into one run; waiting, done and not planned entries end a run. */
export function runsOf(entries: readonly PlanEntry[]): Run[] {
  const runs: Run[] = [];
  for (const e of entries) {
    if ((e.want !== 'run' && e.want !== 'urgent') || e.class === null) continue;
    const urgent = e.want === 'urgent';
    const last = runs[runs.length - 1];
    if (last !== undefined && last.to.getTime() === e.start.getTime() && last.class === e.class && last.urgent === urgent) {
      last.to = e.end;
      last.energyKwh += e.energyKwh;
      last.maxPowerW = Math.max(last.maxPowerW, e.maxPowerW);
    } else {
      runs.push({ from: e.start, to: e.end, class: e.class, urgent, energyKwh: e.energyKwh, maxPowerW: e.maxPowerW });
    }
  }
  return runs;
}

/** "02:00", or "Tue 02:00" when the instant is not on the same local day as <paramref name="now"/>. */
export function dayTime(at: Date, now: Date, lang: Lang, zone: string): string {
  const day = (d: Date) => new Intl.DateTimeFormat('en-CA', { timeZone: zone }).format(d);
  if (day(at) === day(now)) return localClock(at, lang, zone);
  return `${new Intl.DateTimeFormat(lang, { timeZone: zone, weekday: 'short' }).format(at)} ${localClock(at, lang, zone)}`;
}

/** An energy class in words; a zone id in monospace. */
export function ClassText({ cls, t }: { cls: string; t: Messages }) {
  if (cls === 'pv') return <>{t['plan.class.pv']}</>;
  const zone = cls.slice('zone:'.length);
  const [before, after] = t['plan.class.zone'].split('{zone}');
  return (
    <Fragment>
      {before}
      <span class="mono">{zone}</span>
      {after}
    </Fragment>
  );
}

/** One planned run as a sentence: "22:00–06:00, from the grid, zone offpeak, 6.20 kWh, up to 3.70 kW". */
export function RunText({ run, now, t, lang, zone }: { run: Run; now: Date; t: Messages; lang: Lang; zone: string }) {
  const sentence = fill(t['plan.device.run'], {
    from: dayTime(run.from, now, lang, zone),
    to: dayTime(run.to, now, lang, zone),
    class: '{class}',
    energy: formatKwh(run.energyKwh, lang),
    power: formatKw(run.maxPowerW, lang),
  });
  const [before, after] = sentence.split('{class}');
  return (
    <Fragment>
      {before}
      <ClassText cls={run.class} t={t} />
      {after}
    </Fragment>
  );
}

export type PriceTone = 'cheap' | 'middle' | 'dear' | 'flat' | 'unknown';

/**
 * Each slot's import price ranked among the prices of the plan: the cheapest is cheap, the dearest dear, the rest middle; one price
 * for the whole plan is flat. Rank, not the zone's name, so a market price curve and a G13 schedule read the same way.
 */
export function priceTones(slots: readonly PlanSlot[]): PriceTone[] {
  const prices = [...new Set(slots.map((s) => s.importPrice).filter((p): p is number => p !== null).map((p) => roundTo(p, 6)))].sort((a, b) => a - b);
  return slots.map((s) => {
    if (s.importPrice === null) return 'unknown';
    if (prices.length <= 1) return 'flat';
    const price = roundTo(s.importPrice, 6);
    return price === prices[0] ? 'cheap' : price === prices[prices.length - 1] ? 'dear' : 'middle';
  });
}

/** An amount with the currency label as written in the configuration, or the bare number when no economics section names one. */
export function costText(t: Messages, lang: Lang, value: number, currency: string | null): string {
  const amount = formatFixed(value, lang, 2);
  return currency === null ? fill(t['plan.cost.amountPlain'], { value: amount }) : fill(t['plan.cost.amount'], { value: amount, currency });
}
