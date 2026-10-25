import { useEffect, useRef, useState } from 'preact/hooks';
import type { CommandFailure, Control } from './api/control';
import type { ModeCondition } from './api/decisions';
import type { Mode } from './api/mode';
import { formatNumber } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { levelText } from './reasons';
import { PollStatus, StateBadge, StateLabel, type Tone } from './ui';
import type { ControlState } from './useControl';
import './control.css';

// MODE-02 on screen (Status page, setup step 5): the add-on option (the ceiling, read-only), the
// "control allowed" switch, the mode in force and every condition that keeps it in shadow, each as a
// sentence. Allowing control takes a confirmation; switching it off does not, because off is the
// direction that sends nothing. The observation time is advice only. After an untrusted state.json
// the switch is locked until the user confirms the state. Everything shown is what the add-on
// answered; nothing here decides whether commands go out.

/**
 * What the add-on sends under a mode: commands (active), only safe-state switch-offs and minimums
 * (control allowed, but the configuration is invalid: the add-on keeps protecting the devices of the
 * last valid file), or nothing at all.
 */
export type ModeKind = 'active' | 'offs_only' | 'shadow';

export function modeKind(mode: Mode): ModeKind {
  if (mode.effective === 'active') return 'active';
  return mode.missing.length === 1 && mode.missing[0] === 'configuration_invalid' ? 'offs_only' : 'shadow';
}

const DAY_MS = 86_400_000;

/** Whole days since the first start, on the browser's clock (advice only, so a DST hour does not matter). */
function daysText(since: Date, t: Messages, lang: Lang): string {
  const days = Math.max(0, Math.floor((Date.now() - since.getTime()) / DAY_MS));
  if (days === 0) return t['control.days.zero'];
  if (days === 1) return t['control.days.one'];
  return fill(t['control.days.other'], { count: formatNumber(days, lang, 0) });
}

// Local time only here, at the presentation edge.
function dateText(at: Date, lang: Lang): string {
  return new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit', year: 'numeric' }).format(at);
}

function When({ at, lang }: { at: Date; lang: Lang }) {
  const text = new Intl.DateTimeFormat(lang, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);
  return <time dateTime={at.toISOString()}>{text}</time>;
}

/** A text with a {time} placeholder, the instant rendered as a <time> element wherever the language puts it. */
function TimedText({ template, values = {}, at, lang }: { template: string; values?: Record<string, string>; at: Date; lang: Lang }) {
  const [before, after = ''] = template.split('{time}');
  return (
    <>
      {fill(before!, values)}
      <When at={at} lang={lang} />
      {fill(after, values)}
    </>
  );
}

function effectiveFacts(t: Messages, mode: Mode): { tone: Tone; label: string; note: string } {
  switch (modeKind(mode)) {
    case 'active':
      return { tone: 'ok', label: t.modeActive, note: t.modeActiveNote };
    case 'offs_only':
      return { tone: 'crit', label: t['control.effective.offsOnly'], note: t['control.effective.offsOnlyNote'] };
    case 'shadow':
      return { tone: 'muted', label: t.modeShadow, note: t.modeShadowNote };
  }
}

function failureText(t: Messages, failure: CommandFailure): string {
  return failure.notSent !== null ? t[`control.notSent.${failure.notSent}`] : t[`control.outcome.${failure.outcome as 'failed' | 'timed_out'}`];
}

function noticeText(t: Messages, notice: NonNullable<ControlState['notice']>): string {
  if (notice.kind === 'failed') return t['control.failed'];
  if (notice.code === 'state_unconfirmed' || notice.code === 'not_locked') return t[`control.refused.${notice.code}`];
  return fill(t['control.refused.other'], { code: notice.code });
}

type Props = { state: ControlState; t: Messages; lang: Lang };

/**
 * The control block. On the Status page it is a card of its own with the command path; inside setup
 * step 5 (a card already) it is a section without the command path.
 */
export function ControlBlock({ state, t, lang, place }: Props & { place: 'status' | 'setup' }) {
  const { view } = state;
  const control = view.kind === 'live' ? view.data : null;
  const Heading = place === 'status' ? 'h2' : 'h3';
  return (
    <section
      class={place === 'status' ? 'card ctl' : 'ctl ctl-inline'}
      aria-labelledby="ctl-title"
      data-effective={control === null ? undefined : modeKind(control.mode)}
      data-missing={control === null ? undefined : control.mode.missing.join(' ')}
    >
      <header class="cardhead">
        <Heading id="ctl-title">{t['control.title']}</Heading>
        <p class="sub">{t['control.lead']}</p>
      </header>
      <PollStatus view={view} lang={lang} t={t} />
      {control !== null && (
        <>
          {control.locked && <LockCard state={state} t={t} />}
          <Facts control={control} state={state} t={t} lang={lang} />
          <Missing mode={control.mode} t={t} />
          {control.leftReduced.length > 0 && <LeftReduced control={control} t={t} lang={lang} />}
          {place === 'status' && <Commands control={control} t={t} lang={lang} />}
        </>
      )}
    </section>
  );
}

function Facts({ control, state, t, lang }: { control: Control } & Props) {
  const effective = effectiveFacts(t, control.mode);
  const observation = control.observation;
  return (
    <dl class="status ctl-facts">
      <dt>{t['control.row.effective']}</dt>
      <dd data-fact="effective">
        <StateBadge tone={effective.tone} label={effective.label} />
        <span class="sub">{effective.note}</span>
      </dd>
      <dt>{t['control.row.option']}</dt>
      <dd data-fact="option">
        <span class="mono">mode: {control.mode.ceiling}</span>
        <span class="sub">{t['control.option.hint']}</span>
      </dd>
      <dt id="ctl-switch-label">{t['control.row.switch']}</dt>
      <dd data-fact="switch">
        <ControlSwitch control={control} state={state} t={t} lang={lang} />
      </dd>
      {observation !== null && (
        <>
          <dt>{t['control.row.observing']}</dt>
          <dd data-fact="observing">
            {fill(t['control.observing.value'], { days: daysText(observation.since, t, lang), date: dateText(observation.since, lang) })}
            <span class="sub">
              {observation.configurationErrors > 0 ? (
                <StateLabel
                  tone="warn"
                  label={[
                    fill(t['control.observing.errors'], { count: formatNumber(observation.configurationErrors, lang, 0) }),
                    ...(observation.lastConfigurationErrorAt !== null ? [fill(t['control.observing.lastError'], { date: dateText(observation.lastConfigurationErrorAt, lang) })] : []),
                  ].join(', ')}
                />
              ) : (
                fill(t['control.observing.errors'], { count: formatNumber(0, lang, 0) })
              )}
            </span>
            <span class="sub">{t['control.observing.advice']}</span>
          </dd>
        </>
      )}
    </dl>
  );
}

/** The switch. Allowing asks first; switching off is sent at once (it sends nothing to any device). */
function ControlSwitch({ control, state, t, lang }: { control: Control } & Props) {
  const [asking, setAsking] = useState(false);
  const allowButton = useRef<HTMLButtonElement>(null);
  const on = control.controlAllowed;
  const confirming = asking && !on && !control.locked;

  useEffect(() => {
    if (confirming) allowButton.current?.focus();
  }, [confirming]);

  // Default with a time: set off by the add-on itself (the lock after an untrusted state.json), not "never changed".
  const source = control.source !== 'default' ? t[`control.source.${control.source}`] : control.changedAt === null ? t['control.source.default'] : null;
  return (
    <>
      <div class="ctl-switchline">
        <button
          type="button"
          role="switch"
          class="ctl-switch"
          aria-checked={on ? 'true' : 'false'}
          aria-labelledby="ctl-switch-label"
          aria-describedby={control.locked ? 'ctl-switch-locked' : undefined}
          disabled={control.locked || state.busy !== 'idle'}
          onClick={() => (on ? void state.setAllowed(false) : setAsking(true))}
        >
          <span class="ctl-knob" aria-hidden="true" />
          <span class="ctl-switchtext">{on ? t['control.switch.on'] : t['control.switch.off']}</span>
        </button>
        <span class="sub-inline">
          {source}
          {control.changedAt !== null && (
            <>
              {source !== null && ', '}
              <TimedText template={t['control.since']} at={control.changedAt} lang={lang} />
            </>
          )}
        </span>
      </div>
      {control.locked && (
        <span class="sub" id="ctl-switch-locked">
          {t['control.switch.locked']}
        </span>
      )}
      {on && <span class="sub">{t['control.switch.offHint']}</span>}
      {confirming && (
        <div class="ctl-confirm" role="group" aria-labelledby="ctl-confirm-title">
          <p id="ctl-confirm-title" class="ctl-confirm-title">
            {t['control.confirm.title']}
          </p>
          <p>{t['control.confirm.body']}</p>
          {control.mode.ceiling === 'shadow' && <p>{t['control.confirm.optionShadow']}</p>}
          <p>
            <strong>{t['control.confirm.notProtective']}</strong>
          </p>
          <div class="ctl-buttons">
            <button
              type="button"
              class="log-button ctl-primary"
              ref={allowButton}
              onClick={() => {
                setAsking(false);
                void state.setAllowed(true);
              }}
            >
              {t['control.confirm.allow']}
            </button>
            <button type="button" class="log-button" onClick={() => setAsking(false)}>
              {t['control.confirm.cancel']}
            </button>
          </div>
        </div>
      )}
      {state.busy === 'changing' && (
        <span class="sub" role="status">
          {t['control.switch.saving']}
        </span>
      )}
      {state.notice !== null && state.busy === 'idle' && (
        <span class="ctl-notice" data-notice={state.notice.kind === 'refused' ? state.notice.code : 'failed'}>
          <StateLabel tone="warn" label={noticeText(t, state.notice)} />
        </span>
      )}
    </>
  );
}

const MISSING_KEY: Record<ModeCondition, `control.missing.${ModeCondition}`> = {
  option_shadow: 'control.missing.option_shadow',
  control_off: 'control.missing.control_off',
  configuration_invalid: 'control.missing.configuration_invalid',
  state_unconfirmed: 'control.missing.state_unconfirmed',
};

/** Every condition that keeps the mode in shadow, in the add-on's order, each in words. */
function Missing({ mode, t }: { mode: Mode; t: Messages }) {
  if (mode.missing.length === 0) return null;
  const offsOnly = modeKind(mode) === 'offs_only';
  return (
    <div class="ctl-missing">
      <p>{t['control.missing.title']}</p>
      <ul class="plainlist">
        {mode.missing.map((condition) => (
          <li key={condition} data-condition={condition}>
            {t[MISSING_KEY[condition]]}
            {condition === 'configuration_invalid' && (
              <>
                {' '}
                <a href="#/signals">{t.navSignals}</a>
                {offsOnly && <span class="sub">{t['control.missing.offsOnly']}</span>}
              </>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

/** After an untrusted state.json: locked off, kill switch on, until the user confirms (recorded). */
function LockCard({ state, t }: { state: ControlState; t: Messages }) {
  return (
    <div class="ctl-lock" role="alert" aria-labelledby="ctl-lock-title">
      <p id="ctl-lock-title">
        <StateLabel tone="crit" label={t['control.lock.title']} />
      </p>
      <p>{t['control.lock.state_file_corrupt']}</p>
      <p class="sub">{t['control.lock.what']}</p>
      <button type="button" class="log-button ctl-primary" disabled={state.busy !== 'idle'} onClick={() => void state.confirm()}>
        {state.busy === 'confirming' ? t['control.lock.confirming'] : t['control.lock.confirm']}
      </button>
    </div>
  );
}

/** Devices Switchboard switched off or lowered and would bring back (restore_only baselines). */
function LeftReduced({ control, t, lang }: { control: Control; t: Messages; lang: Lang }) {
  const active = control.mode.effective === 'active';
  return (
    <div class="ctl-reduced" data-left-reduced={control.leftReduced.map((d) => d.device).join(' ')}>
      <h3>{t['control.leftReduced.title']}</h3>
      <p class="sub">{active ? t['control.leftReduced.active'] : t['control.leftReduced.notActive']}</p>
      <ul class="plainlist">
        {control.leftReduced.map((device) => (
          <li key={device.device}>
            <a href={`#/devices/${encodeURIComponent(device.device)}`}>{device.device}</a>{' '}
            <span class="sub-inline">
              <TimedText template={t['control.leftReduced.item']} values={{ level: levelText(device.level, t, lang) }} at={device.since} lang={lang} />
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The command path: the link, the queue, the last accepted command, the last failure, devices that did not follow. */
function Commands({ control, t, lang }: { control: Control; t: Messages; lang: Lang }) {
  const { commands } = control;
  const failure = commands.lastFailure;
  return (
    <div class="ctl-commands">
      <h3>{t['control.commands.title']}</h3>
      <p data-link={commands.linkReady ? 'ready' : 'down'}>
        <StateLabel tone={commands.linkReady ? 'ok' : 'warn'} label={commands.linkReady ? t['control.commands.linkReady'] : t['control.commands.linkDown']} />
        <span class="sub">
          {fill(t['control.commands.pending'], { count: formatNumber(commands.pending, lang, 0) })}
          {' · '}
          {commands.lastAcknowledgedAt === null ? (
            t['control.commands.noAck']
          ) : (
            <TimedText template={t['control.commands.lastAck']} at={commands.lastAcknowledgedAt} lang={lang} />
          )}
        </span>
      </p>
      {failure !== null && (
        <p data-failure={failure.outcome}>
          <StateLabel tone="warn" label={t['control.commands.lastFailure']} />
          <span class="sub">
            <a href={`#/devices/${encodeURIComponent(failure.device)}`}>{failure.device}</a>, <When at={failure.at} lang={lang} />: {failureText(t, failure)}
            {failure.detail !== null && <span class="mono"> ({failure.detail})</span>}
          </span>
        </p>
      )}
      {commands.unconfirmed.length > 0 && (
        <div class="ctl-unconfirmed" data-unconfirmed={commands.unconfirmed.map((u) => u.device).join(' ')}>
          <p>
            <StateLabel tone="warn" label={t['control.unconfirmed.title']} />
          </p>
          <p class="sub">{t['control.unconfirmed.text']}</p>
          <ul class="plainlist">
            {commands.unconfirmed.map((u) => (
              <li key={u.device}>
                <a href={`#/devices/${encodeURIComponent(u.device)}`}>{u.device}</a>{' '}
                <span class="sub-inline">
                  <TimedText template={t['control.since']} at={u.since} lang={lang} />
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
