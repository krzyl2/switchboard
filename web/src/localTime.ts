// Local time exists only here, at the presentation edge: the add-on is timezone-free and sends UTC
// instants (its signal clock's `at`, data time in development replay). A local day is computed in
// an explicit IANA zone, by default the browser's, so it is right on 23- and 25-hour days and
// testable whatever zone the test machine runs in.

/** The browser's IANA zone ("Europe/Warsaw"). */
export function browserZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

interface WallClock {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
  second: number;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function wallClock(at: Date, zone: string): WallClock {
  let formatter = formatters.get(zone);
  if (formatter === undefined) {
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone: zone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
    formatters.set(zone, formatter);
  }
  const parts = Object.fromEntries(formatter.formatToParts(at).map((p) => [p.type, p.value]));
  return {
    year: Number(parts.year),
    month: Number(parts.month),
    day: Number(parts.day),
    hour: Number(parts.hour),
    minute: Number(parts.minute),
    second: Number(parts.second),
  };
}

/** The zone's offset from UTC at an instant, in milliseconds (Warsaw in summer: +2 h). */
export function offsetAt(at: Date, zone: string): number {
  const w = wallClock(at, zone);
  const asUtc = Date.UTC(w.year, w.month - 1, w.day, w.hour, w.minute, w.second);
  return asUtc - Math.floor(at.getTime() / 1000) * 1000;
}

/**
 * The instant a local wall time happens. Twice round the offset, so a time just after a DST change
 * lands right; a wall time that does not exist (skipped forward) resolves to just after the change.
 */
function instantOf(year: number, month: number, day: number, hour: number, zone: string): Date {
  const wall = Date.UTC(year, month - 1, day, hour);
  let guess = wall - offsetAt(new Date(wall), zone);
  guess = wall - offsetAt(new Date(guess), zone);
  return new Date(guess);
}

/** Local midnight of the day containing <paramref name="at"/>, and the next local midnight: [from, to). */
export function localDay(at: Date, zone: string = browserZone()): { from: Date; to: Date } {
  const w = wallClock(at, zone);
  const from = instantOf(w.year, w.month, w.day, 0, zone);
  const next = new Date(Date.UTC(w.year, w.month - 1, w.day + 1));
  const to = instantOf(next.getUTCFullYear(), next.getUTCMonth() + 1, next.getUTCDate(), 0, zone);
  return { from, to };
}

/** The start of the local hour containing <paramref name="at"/> ("grid this hour since 14:00"). */
export function localHourStart(at: Date, zone: string = browserZone()): Date {
  const w = wallClock(at, zone);
  return new Date(at.getTime() - ((w.minute * 60 + w.second) * 1000) - (at.getTime() % 1000));
}

/** How many buckets of <paramref name="bucketMinutes"/> a range holds (a whole local day: 276, 288 or 300 at 5 min). */
export function bucketCount(range: { from: Date; to: Date }, bucketMinutes: number): number {
  return Math.round((range.to.getTime() - range.from.getTime()) / (bucketMinutes * 60_000));
}

/** "14:05" in the zone, for a label such as "since 14:05". */
export function localClock(at: Date, lang: string, zone: string = browserZone()): string {
  return new Intl.DateTimeFormat(lang, { timeZone: zone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(at);
}
