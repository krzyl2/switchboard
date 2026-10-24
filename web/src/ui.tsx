import type { Lang } from './i18n';

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

// Local wall-clock time only here, at the presentation edge; the machine-readable instant stays in dateTime.
export function Timestamp({ at, lang }: { at: Date; lang: Lang }) {
  const text = new Intl.DateTimeFormat(lang, { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
  return <time dateTime={at.toISOString()}>{text}</time>;
}
