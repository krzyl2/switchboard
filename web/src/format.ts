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
