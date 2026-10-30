import type { ComponentChildren } from 'preact';
import { useState } from 'preact/hooks';
import { EV_MODES, type Ev, type EvAssessment, type EvMode, type EvOfferedGoal, type EvRead, type EvRefusal } from './api/ev';
import type { EvCause } from './api/plan';
import type { SignalState } from './api/signals';
import { SourcesText } from './Economics';
import { formatAge, formatFixed, formatKwh, formatNumber } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { browserZone } from './localTime';
import { dayTime } from './planText';
import { StateBadge, StateLabel, type Tone } from './ui';
import type { EvControl } from './useEv';
import './plan.css';
import './ev.css';

// The EV module on screen (EV-10): the card of the day plan ("Car charging": what the module asks the planner for and why, the car's inputs,
// the next trip, the calendar events it ignored, the manual control of S6) and the row of the charger's device card. It reads api/ev and says
// what the add-on reports; its only writes are POST api/ev/mode and POST and DELETE api/ev/charge-now, the module's own state, never an entity.

interface Ctx {
  t: Messages;
  lang: Lang;
}

/** How a reason reads at a glance: what is wanted of the grid is neutral, what leaves the car on the v2 rules or without data warns. */
const REASON_TONE: Record<EvCause, Tone> = {
  ev_trip_deadline: 'ok',
  ev_surplus: 'ok',
  ev_cheap_zone: 'ok',
  ev_floor_hold: 'ok',
  ev_weekly_full: 'ok',
  ev_manual: 'ok',
  ev_pv_only: 'ok',
  ev_off: 'muted',
  ev_not_plugged: 'muted',
  ev_forecast_missing: 'warn',
  ev_soc_stale: 'warn',
  ev_calendar_unavailable: 'warn',
  ev_range_unusable: 'warn',
  ev_without_planner_dispatch: 'warn',
};

/** A sentence for a key, or the code itself when this page has none (parseEv admits only the closed lists, so this is a guard for a list that grew). */
const word = (t: Messages, key: string, code: string): string => (t as Record<string, string | undefined>)[key] ?? code;

const READ_TONE: Record<SignalState, Tone> = { fresh: 'ok', stale: 'warn', rejected: 'crit', unavailable: 'warn', unmapped: 'muted' };

/** A reason or note code in a sentence (the closed list of the module's own; the plan's `cause` uses the same sentences). */
const cause = (t: Messages, code: EvCause) => word(t, `plan.cause.${code}`, code);

function Row({ name, row, children }: { name: string; row: string; children: ComponentChildren }) {
  return (
    <>
      <dt>{name}</dt>
      <dd data-row={row}>{children}</dd>
    </>
  );
}

/** The state of charge in words: the estimate, or why there is none. */
function socText(t: Messages, lang: Lang, assessment: EvAssessment | null, ev: Ev): string {
  const percent = assessment === null ? (ev.inputs?.socPercent ?? null) : assessment.socPercent;
  if (percent !== null) return fill(t['ev.soc.value'], { percent: formatNumber(percent, lang, 0) });
  const gap = assessment?.socGap ?? null;
  return gap === null ? t['ev.soc.none'] : `${t['ev.soc.none']}: ${word(t, `ev.socGap.${gap}`, gap)}`;
}

function PluggedText({ ev, t }: { ev: Ev; t: Messages }) {
  const config = ev.config!;
  if (config.pluggedSignal === null) return <>{t['ev.plugged.notMapped']}</>;
  const plugged = ev.inputs?.isPlugged ?? null;
  const state = ev.inputs?.pluggedState ?? null;
  return (
    <>
      {plugged === null ? t['ev.plugged.unknown'] : plugged ? t['ev.plugged.yes'] : t['ev.plugged.no']}
      {state !== null && (
        <>
          {' '}
          (<span class="mono">{state}</span>)
        </>
      )}
    </>
  );
}

function GoalItem({ goal, ev, t, lang }: { goal: EvOfferedGoal; ev: Ev } & Ctx) {
  const zone = browserZone();
  return (
    <li class="plan-goal" data-ev-goal={goal.kind}>
      <span class="line">
        <strong>{fill(word(t, `ev.goal.kind.${goal.kind}`, goal.kind), { target: formatNumber(goal.targetPercent, lang, 0) })}</strong>{' '}
        {goal.deadline === null ? t['ev.goal.noDeadline'] : fill(t['ev.goal.due'], { time: dayTime(goal.deadline, ev.at, lang, zone) })}
      </span>
      <span class="line plan-pills">
        <span class="badge" data-tone={goal.hard ? 'warn' : 'muted'}>
          {goal.hard ? t['plan.goal.hard'] : t['plan.goal.soft']}
        </span>
        {goal.spread && (
          <span class="badge" data-tone="muted" data-ev-goal-spread="true">
            {t['ev.goal.spread']}
          </span>
        )}
      </span>
      <span class="line sub">{fill(t['ev.goal.need'], { kwh: formatKwh(goal.remainingKwh, lang) })}</span>
      {goal.sources !== null && (
        <span class="line sub" data-ev-goal="sources">
          {t['plan.goal.sources'].split('{sources}')[0]}
          <SourcesText sources={goal.sources} t={t} />
        </span>
      )}
    </li>
  );
}

function InputLine({ name, read, t, lang }: { name: string; read: EvRead } & Ctx) {
  if (!read.configured) {
    return (
      <li data-ev-input={name}>
        <strong>{t[`ev.input.${name as 'soc'}`]}</strong>: <span class="sub-inline">{t['ev.input.notMapped']}</span>
      </li>
    );
  }
  return (
    <li data-ev-input={name} data-state={read.state}>
      <strong>{t[`ev.input.${name as 'soc'}`]}</strong> <StateLabel tone={READ_TONE[read.state]} label={t[`signalState.${read.state}`]} />
      {read.detail !== 'none' && <span class="sub-inline"> · {t[`signalDetail.${read.detail}`]}</span>}
      {read.ageS !== null && <span class="sub-inline"> · {fill(t['ev.input.age'], { age: formatAge(read.ageS, lang) })}</span>}
    </li>
  );
}

function Inputs({ ev, t, lang }: { ev: Ev } & Ctx) {
  const inputs = ev.inputs;
  if (inputs === null) return null;
  const reads: [string, EvRead][] = [
    ['soc', inputs.soc],
    ['range', inputs.range],
    ['plugged', inputs.plugged],
    ['calendar', inputs.calendar],
  ];
  // A problem opens the list by itself: the quiet case needs no space.
  const problem = reads.some(([, r]) => r.configured && r.state !== 'fresh');
  return (
    <details class="plan-slots" data-ev="inputs" open={problem}>
      <summary>{t['ev.inputs.title']}</summary>
      <ul class="plainlist">
        {reads.map(([name, read]) => (
          <InputLine key={name} name={name} read={read} t={t} lang={lang} />
        ))}
      </ul>
      {ev.config!.rangeSignal !== null && <p class="sub">{t['ev.range.note']}</p>}
      {ev.addOnZone !== null && ev.config!.calendarSignal !== null && (
        <p class="sub" data-ev="zone">
          {fill(t['ev.zone'], { zone: ev.addOnZone })}
        </p>
      )}
    </details>
  );
}

/** The calendar events the module ignored, and the warning that one of them may hide the next trip (the entity shows one event). */
function Unparsed({ ev, t, lang }: { ev: Ev } & Ctx) {
  const trips = ev.trips;
  if (trips === null || (trips.unparsed.length === 0 && trips.warnings.length === 0)) return null;
  const zone = browserZone();
  return (
    <div data-ev="unparsed">
      {trips.warnings.map((w, i) => (
        <p key={`w${i}`} class="note" role="status" data-ev-warning={w.kind}>
          {fill(word(t, `ev.warn.${w.kind}`, w.kind), { title: w.title, time: dayTime(w.start, ev.at, lang, zone) })}
        </p>
      ))}
      {trips.unparsed.length > 0 && (
        <>
          <p class="plan-runs-title">{t['ev.unparsed.title']}</p>
          <ul class="plainlist">
            {trips.unparsed.map((u, i) => (
              <li key={`u${i}`} data-ev-unparsed={u.reason}>
                {fill(t['ev.unparsed.item'], { title: u.title, time: dayTime(u.start, ev.at, lang, zone), reason: word(t, `ev.unparsed.${u.reason}`, u.reason) })}
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function Issues({ ev, t }: { ev: Ev; t: Messages }) {
  if (ev.issues.length === 0) return null;
  return (
    <div data-ev="issues">
      <p class="plan-runs-title">{t['ev.issues.title']}</p>
      <ul class="plainlist">
        {ev.issues.map((issue, i) => (
          <li key={`${issue.path}-${i}`} data-issue={issue.code}>
            {`issue.${issue.code}` in t ? t[`issue.${issue.code}` as keyof Messages] : issue.message} <span class="mono sub-inline">{issue.path}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** The manual control of S6: the mode and "charge now". Nothing is assumed to have worked until the add-on answers; a refusal is worded. */
function Controls({ ev, control, t, lang }: { ev: Ev; control: EvControl } & Ctx) {
  const manual = ev.manual!;
  const config = ev.config!;
  const defaults = ev.parameters;
  const [mode, setMode] = useState<EvMode | null>(null);
  const [target, setTarget] = useState('');
  const [hours, setHours] = useState('');
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: 'ok' | 'warn'; text: string } | null>(null);
  const shownMode: EvMode = mode ?? manual.mode;
  const dispatch = config.plannerDispatch;
  const canChargeNow = dispatch === 'full';
  const zone = browserZone();

  const refused = (refusal: EvRefusal) => setMessage({ tone: 'warn', text: word(t, `ev.refused.${refusal}`, refusal) });

  const changeMode = async (next: EvMode) => {
    setMode(next);
    setSaving(true);
    setMessage(null);
    const refusal = await control.setMode(next);
    setSaving(false);
    // The add-on's answer is the truth: after a refusal the select goes back to what it holds.
    setMode(null);
    if (refusal === null) setMessage({ tone: 'ok', text: t['ev.control.saved'] });
    else refused(refusal);
  };

  const start = async (event: Event) => {
    event.preventDefault();
    const percent = target.trim() === '' ? undefined : Number(target.replace(',', '.'));
    if (percent !== undefined && (!Number.isFinite(percent) || percent <= 0 || percent > 100)) return setMessage({ tone: 'warn', text: t['ev.charge.noTarget'] });
    const duration = hours.trim() === '' ? undefined : Number(hours.replace(',', '.'));
    if (duration !== undefined && (!Number.isFinite(duration) || duration <= 0)) return setMessage({ tone: 'warn', text: t['ev.charge.noDuration'] });
    setSaving(true);
    setMessage(null);
    const refusal = await control.chargeNow({ targetPct: percent, durationMinutes: duration === undefined ? undefined : Math.round(duration * 60) });
    setSaving(false);
    if (refusal === null) setMessage({ tone: 'ok', text: t['ev.charge.started'] });
    else refused(refusal);
  };

  const stop = async () => {
    setSaving(true);
    setMessage(null);
    const refusal = await control.cancelChargeNow();
    setSaving(false);
    if (refusal === null) setMessage({ tone: 'ok', text: t['ev.charge.stopped'] });
    else refused(refusal);
  };

  const now = manual.chargeNow;
  return (
    <div data-ev="controls">
      <h3 class="plan-h3">{t['ev.control.title']}</h3>
      <div class="plan-wish" data-form="ev-mode">
        <label>
          <span>{t['ev.control.mode']}</span>
          <select value={shownMode} disabled={saving} onChange={(event) => void changeMode((event.currentTarget as HTMLSelectElement).value as EvMode)}>
            {EV_MODES.map((m) => (
              <option key={m} value={m}>
                {word(t, `ev.mode.${m}`, m)}
              </option>
            ))}
          </select>
        </label>
        <span class="sub">{t['ev.control.modeNote']}</span>
      </div>
      <form class="plan-wish" onSubmit={(event) => void start(event)} aria-label={t['ev.charge.title']} data-form="ev-charge-now">
        <strong>{t['ev.charge.title']}</strong>
        {now.active ? (
          <span class="sub" data-ev="charge-active">
            {fill(t['ev.charge.active'], { target: now.targetPct === null ? '' : formatNumber(now.targetPct, lang, 0), time: now.until === null ? '' : dayTime(now.until, ev.at, lang, zone) })}
          </span>
        ) : (
          <span class="sub" data-ev="charge-inactive">
            {t['ev.charge.inactive']}
          </span>
        )}
        {!canChargeNow && (
          <span class="sub" role="note" data-ev="charge-needs-full">
            {dispatch === null ? t['ev.charge.noPlanner'] : fill(t['ev.charge.needsFull'], { mode: dispatch })}
          </span>
        )}
        <label>
          <span>{t['ev.charge.target']}</span>
          <input
            type="text"
            inputMode="decimal"
            value={target}
            placeholder={defaults === null ? undefined : formatNumber(defaults.chargeNowTargetPercent, lang, 0)}
            disabled={saving || !canChargeNow}
            onInput={(event) => setTarget((event.currentTarget as HTMLInputElement).value)}
          />
        </label>
        <label>
          <span>{t['ev.charge.duration']}</span>
          <input
            type="text"
            inputMode="decimal"
            value={hours}
            placeholder={defaults === null ? undefined : formatNumber(defaults.chargeNowDurationS / 3600, lang, 1)}
            disabled={saving || !canChargeNow}
            onInput={(event) => setHours((event.currentTarget as HTMLInputElement).value)}
          />
        </label>
        <span class="plan-actions">
          <button type="submit" disabled={saving || !canChargeNow}>
            {t['ev.charge.submit']}
          </button>
          {now.active && (
            <button type="button" disabled={saving} onClick={() => void stop()}>
              {t['ev.charge.cancel']}
            </button>
          )}
        </span>
        <span class="sub">{t['ev.charge.note']}</span>
      </form>
      {message !== null && (
        <p class="sub" role="status" data-tone={message.tone} data-ev="message">
          {saving ? t['ev.control.saving'] : message.text}
        </p>
      )}
    </div>
  );
}

/** The card of the day plan. Nothing without an ev: section (or without an answer): the module is optional and off by default. */
export function EvCard({ control, lang, t }: { control: EvControl } & Ctx) {
  const view = control.view;
  if (view.kind !== 'live' || !view.data.enabled || view.data.config === null) return null;
  const ev = view.data;
  const config = ev.config!;
  const a = ev.assessment;
  const zone = browserZone();
  const failed = a?.failed === true;
  const expired = a !== null && !a.failed && a.expired;
  const reason = a?.reason ?? null;
  const trip = a?.nextTrip ?? ev.trips?.nextTrip ?? null;
  const mode: EvMode | null = ev.manual?.mode ?? a?.mode ?? null;
  return (
    <section class="card" aria-labelledby="ev-title" data-card="ev" data-ev-reason={reason ?? 'failed'}>
      <header class="cardhead">
        <h2 id="ev-title">{t['ev.title']}</h2>
        {failed && <StateBadge tone="crit" label={t['ev.failed']} />}
        <p class="sub">{t['ev.lead']}</p>
      </header>
      {a === null && <p class="note" data-ev="not-computed">{t['ev.notComputed']}</p>}
      {failed && (
        <p class="note" role="alert" data-ev="failed">
          {t['ev.failed']}
        </p>
      )}
      {expired && (
        <p class="note" role="status" data-ev="expired">
          {t['ev.expired']}
        </p>
      )}
      {reason !== null && (
        <p data-ev="reason">
          <StateBadge tone={REASON_TONE[reason] ?? 'muted'} label={t['ev.reason']} /> {cause(t, reason)}
        </p>
      )}
      {a !== null && a.notes.length > 0 && (
        <ul class="plainlist" data-ev="notes">
          {a.notes.map((note) => (
            <li key={note} data-ev-note={note}>
              {cause(t, note)}
            </li>
          ))}
        </ul>
      )}
      <dl class="dash-kv plan-kv">
        <Row name={t['ev.row.device']} row="ev-device">
          <code class="entity mono">{config.device}</code>
        </Row>
        <Row name={t['ev.row.plugged']} row="ev-plugged">
          <PluggedText ev={ev} t={t} />
        </Row>
        <Row name={t['ev.row.soc']} row="ev-soc">
          {socText(t, lang, a, ev)}
        </Row>
        {config.rangeSignal !== null && (
          <Row name={t['ev.row.range']} row="ev-range">
            {ev.inputs?.rangeKm == null ? t['ev.range.none'] : fill(t['ev.range.value'], { km: formatNumber(ev.inputs.rangeKm, lang, 0) })}
          </Row>
        )}
        {a?.kwhPerKm != null && (
          <Row name={t['ev.row.consumption']} row="ev-consumption">
            <span class="mono">{fill(t['ev.consumption.value'], { value: formatFixed(a.kwhPerKm, lang, 3) })}</span>
          </Row>
        )}
        <Row name={t['ev.row.trip']} row="ev-trip">
          {trip === null ? t['ev.trip.none'] : fill(t['ev.trip.value'], { title: trip.title, km: formatNumber(trip.km, lang, 0), time: dayTime(trip.start, ev.at, lang, zone) })}
        </Row>
        {mode !== null && (
          <Row name={t['ev.row.mode']} row="ev-mode">
            {word(t, `ev.mode.${mode}`, mode)}
          </Row>
        )}
        {a !== null && !a.failed && (
          <Row name={t['ev.row.lastFull']} row="ev-last-full">
            {a.lastFullAt === null ? t['ev.lastFull.none'] : fill(t['ev.lastFull.value'], { time: dayTime(a.lastFullAt, ev.at, lang, zone) })}
          </Row>
        )}
      </dl>
      {a !== null && !a.failed && (
        <div data-ev="goals">
          <p class="plan-runs-title">{t['ev.goals.title']}</p>
          {a.goals.length === 0 ? (
            <p class="sub" data-ev="goals-none">
              {t['ev.goals.none']}
            </p>
          ) : (
            <ul class="plainlist">
              {a.goals.map((goal) => (
                <GoalItem key={`${goal.kind}-${goal.deadline?.getTime() ?? 'none'}`} goal={goal} ev={ev} t={t} lang={lang} />
              ))}
            </ul>
          )}
          {a.computedAt !== null && <p class="sub">{fill(t['ev.computed'], { time: dayTime(a.computedAt, ev.at, lang, zone) })}</p>}
        </div>
      )}
      <Unparsed ev={ev} t={t} lang={lang} />
      <Inputs ev={ev} t={t} lang={lang} />
      <Issues ev={ev} t={t} />
      {ev.manual !== null && <Controls ev={ev} control={control} t={t} lang={lang} />}
    </section>
  );
}

/** The charger's row on its device card: where the car stands and what the module wants, with the way to the card above. Nothing for another device. */
export function EvDeviceLines({ ev, device, t, lang }: { ev: Ev | null; device: string } & Ctx) {
  if (ev === null || !ev.enabled || ev.config === null || ev.config.device !== device) return null;
  const a = ev.assessment;
  const mode: EvMode | null = ev.manual?.mode ?? a?.mode ?? null;
  const reason = a?.reason ?? null;
  return (
    <>
      <span class="line" data-ev-device="summary">
        {mode === null ? socText(t, lang, a, ev) : fill(t['ev.device.summary'], { soc: socText(t, lang, a, ev), mode: word(t, `ev.mode.${mode}`, mode) })}
      </span>
      {a?.failed === true && <span class="line sub">{t['ev.failed']}</span>}
      {reason !== null && (
        <span class="line sub" data-ev-device="reason" data-ev-reason={reason}>
          {cause(t, reason)}
        </span>
      )}
      <span class="line sub">
        <a href="#/plan">{t['ev.device.link']}</a>
      </span>
    </>
  );
}

/** Whether the page has a charger row to show for this device. */
export function isEvDevice(ev: Ev | null, device: string): boolean {
  return ev !== null && ev.enabled && ev.config !== null && ev.config.device === device;
}
