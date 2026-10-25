import type { Lang } from './i18n';

// Presentation formatting only: the add-on sends values already normalized to A, W, V, kWh and %.

const MINUTE_S = 60;
const HOUR_S = 60 * MINUTE_S;
// Below these, the smaller unit is still easy to read ("119 s", "119 min").
const SECONDS_UP_TO_S = 120;
const MINUTES_UP_TO_S = 120 * MINUTE_S;

/** An age: "4 s", "18 min", "19 h", in whole units (truncated, as a clock would show it). */
export function formatAge(seconds: number, lang: Lang): string {
  const whole = (value: number) => formatNumber(Math.trunc(value), lang, 0);
  const magnitude = Math.abs(seconds);
  if (magnitude < SECONDS_UP_TO_S) return `${whole(seconds)} s`;
  if (magnitude < MINUTES_UP_TO_S) return `${whole(seconds / MINUTE_S)} min`;
  return `${whole(seconds / HOUR_S)} h`;
}

/** A countdown as a clock shows it, "4:12" (minutes:seconds, rounded up so it reaches 0:00 at expiry). */
export function formatCountdown(seconds: number): string {
  const total = Math.max(0, Math.ceil(seconds));
  return `${Math.floor(total / MINUTE_S)}:${String(total % MINUTE_S).padStart(2, '0')}`;
}

/** A detected refresh interval keeps one decimal: a 2.5 s charger is not a 2 s one. */
export function formatInterval(seconds: number, lang: Lang): string {
  return seconds < SECONDS_UP_TO_S ? `${formatNumber(seconds, lang, 1)} s` : formatAge(seconds, lang);
}

export function formatNumber(value: number, lang: Lang, maximumFractionDigits: number): string {
  // Round first (half away from zero, as Intl does) so a value that rounds to zero prints "0", not "-0".
  const factor = 10 ** maximumFractionDigits;
  const rounded = (Math.sign(value) * Math.round(Math.abs(value) * factor)) / factor;
  return new Intl.NumberFormat(lang, { maximumFractionDigits }).format(rounded === 0 ? 0 : rounded);
}

const FRACTION_DIGITS: Record<string, number> = { A: 2, W: 0, V: 1, kWh: 3, '%': 1 };

/** A measured value with its unit, e.g. "9.03 A", "-2,070 W" (pl: "9,03 A"). */
export function formatQuantity(value: number, unit: string, lang: Lang): string {
  return `${formatNumber(value, lang, FRACTION_DIGITS[unit] ?? 2)} ${unit}`;
}

/**
 * A number with exactly <paramref name="digits"/> decimals ("1.50"), rounded half away from zero
 * first, so a value that rounds to zero prints "0.00", never "-0.00". The rounding rule the
 * dashboard's sums are checked against.
 */
export function formatFixed(value: number, lang: Lang, digits: number): string {
  const rounded = roundTo(value, digits);
  return new Intl.NumberFormat(lang, { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(rounded === 0 ? 0 : rounded);
}

/** Half away from zero to <paramref name="digits"/> decimals (what formatFixed shows). */
export function roundTo(value: number, digits: number): number {
  const factor = 10 ** digits;
  return (Math.sign(value) * Math.round(Math.abs(value) * factor)) / factor;
}

/** A power in kW with two decimals, as the mockup shows it: 1500 W → "1.50 kW" (pl "1,50 kW"). */
export function formatKw(watts: number, lang: Lang): string {
  return `${formatFixed(watts / 1000, lang, 2)} kW`;
}

/** An energy with two decimals: "2.10 kWh". */
export function formatKwh(kwh: number, lang: Lang): string {
  return `${formatFixed(kwh, lang, 2)} kWh`;
}

/** A current with one decimal, as the phase bars show it: "12.6 A". */
export function formatAmps(amperes: number, lang: Lang): string {
  return `${formatFixed(amperes, lang, 1)} A`;
}

/** A share as a percentage without decimals: 0.504 → "50%". */
export function formatPercent(fraction: number, lang: Lang): string {
  return new Intl.NumberFormat(lang, { style: 'percent', maximumFractionDigits: 0 }).format(fraction);
}
