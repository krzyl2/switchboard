import type { ComponentChildren } from 'preact';
import type { PhasePower, PowerUnknownReason, Quantity } from './api/power';
import { formatAge, formatAmps, formatKw, formatPercent } from './format';
import { fill, type Lang, type Messages } from './i18n';
import type { PollView } from './usePoll';

/** `muted` is for "nothing to report" (an unmapped slot): neither good nor bad. */
export type Tone = 'ok' | 'warn' | 'crit' | 'muted';

/** A coloured dot always paired with a text label: colour alone never carries a state. */
export function StateLabel({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span class="state" data-tone={tone}>
      <span class="dot" aria-hidden="true" />
      {label}
    </span>
  );
}

/** The mockup's pill: a dot and a text label in a rounded badge (a queue state, a verdict). */
export function StateBadge({ tone, label }: { tone: Tone; label: string }) {
  return (
    <span class="badge" data-tone={tone}>
      <span class="dot" aria-hidden="true" />
      {label}
    </span>
  );
}

// Local wall-clock time only here, at the presentation edge; the machine-readable instant stays in dateTime.
export function Timestamp({ at, lang }: { at: Date; lang: Lang }) {
  const text = new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
  return <time dateTime={at.toISOString()}>{text}</time>;
}

/**
 * How old a value is, measured on the add-on's clock (<paramref name="now"/> is the answer's `at`,
 * data time in development replay), so a frozen value is told apart from a calm one. Nothing is
 * shown for a value without a report time (a model value).
 */
export function Age({ reportedAt, now, lang, t, template = 'age.ago' }: { reportedAt: Date | null; now: Date; lang: Lang; t: Messages; template?: 'age.ago' | 'age.reported' }) {
  if (reportedAt === null) return null;
  const seconds = Math.max(0, (now.getTime() - reportedAt.getTime()) / 1000);
  return (
    <time class="age" dateTime={reportedAt.toISOString()}>
      {fill(t[template], { age: formatAge(seconds, lang) })}
    </time>
  );
}

/**
 * The freshness line of a card: when the add-on last evaluated (its own clock), or, after a failed
 * poll, "unavailable" with the time of the last successful update (the browser's clock). A failed
 * poll withdraws the values, so this is what stays on screen.
 */
export function PollStatus<T>({ view, evaluatedAt, now, lang, t }: { view: PollView<T>; evaluatedAt?: Date | null; now?: Date; lang: Lang; t: Messages }) {
  if (view.kind === 'loading') return <p class="updated">{t['common.loading']}</p>;
  if (view.kind === 'unavailable') {
    return (
      <p class="updated unavailable" role="alert">
        <StateLabel tone="crit" label={view.lastSuccessAt === null ? t['poll.unavailableNever'] : fill(t['poll.unavailable'], { time: clock(view.lastSuccessAt, lang) })} />
      </p>
    );
  }
  if (evaluatedAt === undefined || evaluatedAt === null || now === undefined) return null;
  const seconds = Math.max(0, (now.getTime() - evaluatedAt.getTime()) / 1000);
  return (
    <p class="updated">
      <time dateTime={evaluatedAt.toISOString()}>{fill(t['poll.evaluated'], { age: formatAge(seconds, lang) })}</time>
    </p>
  );
}

function clock(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
}

/** Why a figure is unknown, in words; a device reason names the devices. */
export function unknownReason(t: Messages, reason: PowerUnknownReason, devices: readonly string[] = []): string {
  return reason === 'device_power_unknown'
    ? fill(t['powerUnknown.device_power_unknown'], { devices: devices.length === 0 ? t['common.none'] : devices.join(', ') })
    : t[`powerUnknown.${reason}`];
}

/**
 * A power figure: "1.50 kW", or "not declared" (counted as 0 in the balance, never shown as 0 W),
 * or "unknown — <reason>". An unknown figure is never rendered as a number.
 */
export function QuantityText({ quantity, t, lang, devices = [] }: { quantity: Quantity; t: Messages; lang: Lang; devices?: readonly string[] }) {
  if (quantity.w !== null) return <span class="mono">{formatKw(quantity.w, lang)}</span>;
  if (quantity.unknown === 'not_declared') return <span class="sub-inline">{t['common.notDeclared']}</span>;
  return <span class="unknown">{fill(t['common.unknownBecause'], { reason: unknownReason(t, quantity.unknown ?? 'power_not_fresh', devices) })}</span>;
}

/** The scale of a phase bar: a little beyond the continuous current, or the current when it is higher. */
function barScale(phase: PhasePower): number {
  const magnitude = Math.abs(phase.currentA ?? 0);
  return Math.max(phase.continuousA * 1.1, magnitude, phase.ratingA);
}

/**
 * One phase as the mockup draws it (UI-08, UI-11): the measured current split into base load,
 * declared devices and a foreign step (or export, or unattributed while a device draw is unknown),
 * a dashed marker at the reserve threshold, a solid one at the breaker rating and a dotted one at
 * the continuous limit, with the current and its share of the rating beside it. Controlled and
 * foreign never share a colour (styles.css tokens --load-controlled and --load-foreign).
 */
export function PhaseBar({ phase, t, lang, children }: { phase: PhasePower; t: Messages; lang: Lang; children?: ComponentChildren }) {
  const scale = barScale(phase);
  const percent = (value: number) => `${Math.min(100, (Math.max(0, value) / scale) * 100)}%`;
  let left = 0;
  const current = phase.currentA === null ? null : Math.abs(phase.currentA);
  const share = current === null ? null : formatPercent(current / phase.ratingA, lang);
  return (
    <div class="phasebar" data-phase={phase.phase}>
      <div class="pname">{phase.phase}</div>
      <div
        class="bar"
        role="img"
        aria-label={current === null ? phase.phase : fill(t['bar.label'], { phase: phase.phase, current: formatAmps(current, lang), percent: share! })}
      >
        {phase.bar.map((segment) => {
          const style = { left: percent(left), width: percent(segment.a) };
          left += segment.a;
          return <div key={segment.kind} class="seg" data-kind={segment.kind} style={style} title={t[`segment.${segment.kind}`]} />;
        })}
        <div class="tick reserve" style={{ left: percent(phase.reserveThresholdA) }} title={fill(t['bar.reserve'], { value: formatAmps(phase.reserveThresholdA, lang) })} />
        <div class="tick breaker" style={{ left: percent(phase.ratingA) }} title={fill(t['bar.breaker'], { value: formatAmps(phase.ratingA, lang) })} />
        <div class="tick continuous" style={{ left: percent(phase.continuousA) }} title={fill(t['bar.continuous'], { value: formatAmps(phase.continuousA, lang) })} />
      </div>
      <div class="pval">
        <span class="mono">{current === null ? t['common.none'] : formatAmps(current, lang)}</span>
        {share !== null && <small>{fill(t['bar.ofLimit'], { percent: share })}</small>}
        {children}
      </div>
    </div>
  );
}
