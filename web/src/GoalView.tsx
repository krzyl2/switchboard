import { useState } from 'preact/hooks';
import type { GoalRefusal } from './api/goals';
import type { GoalKind, Plan, PlanDevice, PlanGoal } from './api/plan';
import { formatAge, formatFixed, formatKwh, formatNumber } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { browserZone } from './localTime';
import { SourcesText } from './Economics';
import { dayTime, RunText, runsOf } from './planText';
import { StateBadge, type Tone } from './ui';
import type { PlanControl } from './usePlan';
import type { PollView } from './usePoll';
import './plan.css';

// The goal of a device as the plan reports it (UI-12, PLAN-19): what it asks for, how far it is, when it is due, whether the plan
// says it is on track, and the "want now" action. It reads api/plan; the only thing it writes is the add-on's own manual goal.

interface Ctx {
  t: Messages;
  lang: Lang;
}

const STATUS_TONE: Record<PlanGoal['status'], Tone> = {
  on_track: 'ok',
  met: 'ok',
  at_risk: 'warn',
  infeasible: 'crit',
  expired: 'muted',
  missed: 'crit',
  inactive: 'muted',
  superseded: 'muted',
};

/** A kWh figure in words ("6.20 kWh"). */
const kwh = (value: number, lang: Lang) => formatKwh(value, lang);

/** One goal: what it asks for and when, its progress, its status and what the plan says about it. */
export function GoalLines({ goal, plan, ...ctx }: { goal: PlanGoal; plan: Plan } & Ctx) {
  const { t, lang } = ctx;
  const zone = browserZone();
  const inactive = goal.status === 'inactive';
  const judged = goal.plannedKwh !== null;
  const target = goal.kind === 'soc' ? goal.targetSoc : goal.targetKwh;
  return (
    <div class="plan-goal" data-goal-status={goal.status} data-goal-source={goal.source}>
      {!inactive && target !== null && (
        <span class="line" data-goal="what">
          <strong>{fill(t[`plan.goal.kind.${goal.kind}`], { target: formatNumber(target, lang, goal.kind === 'soc' ? 0 : 2) })}</strong>{' '}
          {goal.deadline === null ? t['plan.goal.noDeadline'] : fill(t['plan.goal.deadline'], { time: dayTime(goal.deadline, plan.at, lang, zone) })}
          {goal.marginS > 0 && <span class="sub-inline"> ({fill(t['plan.goal.margin'], { margin: formatAge(goal.marginS, lang) })})</span>}
        </span>
      )}
      <span class="line plan-pills" data-goal="pills">
        <StateBadge tone={STATUS_TONE[goal.status]} label={t[`plan.goal.status.${goal.status}`]} />
        {!inactive && (
          <span class="badge" data-tone={goal.hard ? 'warn' : 'muted'} data-goal-hard={goal.hard ? 'true' : 'false'}>
            {goal.hard ? t['plan.goal.hard'] : t['plan.goal.soft']}
          </span>
        )}
        <span class="badge" data-tone="muted">
          {t[`plan.goal.source.${goal.source}`]}
        </span>
        {goal.urgent === true && (
          <span class="badge" data-tone="crit" data-goal="urgent">
            {t['plan.goal.urgent']}
          </span>
        )}
      </span>
      {inactive && goal.inactiveReason !== null && (
        <span class="line" data-goal="inactive">
          {fill(t[`plan.goal.inactive.${goal.inactiveReason}`], { input: goal.inactiveInput === null ? '' : t[`plan.goal.input.${goal.inactiveInput}`] })}
        </span>
      )}
      {!inactive && (
        <span class="line sub" data-goal="progress">
          {goal.kind === 'soc'
            ? goal.currentSoc === null
              ? t['plan.goal.progress.socUnknown']
              : fill(t['plan.goal.progress.soc'], { current: formatNumber(goal.currentSoc, lang, 0) })
            : fill(t['plan.goal.progress.energy'], { delivered: formatFixed(goal.deliveredKwh ?? 0, lang, 2), target: formatFixed(goal.targetKwh ?? 0, lang, 2) })}
          {goal.remainingKwh !== null && <>; {fill(t['plan.goal.remaining'], { kwh: kwh(goal.remainingKwh, lang) })}</>}
        </span>
      )}
      {judged && (
        <span class="line sub" data-goal="plan">
          {fill(t['plan.goal.planned'], { kwh: kwh(goal.plannedKwh!, lang) })}
          {goal.shortfallKwh !== null && goal.shortfallKwh > 1e-9 && <> · {fill(t['plan.goal.shortfall'], { kwh: kwh(goal.shortfallKwh, lang) })}</>}
          {goal.latestGridStart !== null && <> · {fill(t['plan.goal.latestStart'], { time: dayTime(goal.latestGridStart, plan.at, lang, zone) })}</>}
        </span>
      )}
      {goal.cause !== null && (
        <span class="line sub" data-goal="cause" data-cause={goal.cause}>
          {t[`plan.cause.${goal.cause}`]}
        </span>
      )}
      {!inactive && goal.sources !== null && (
        <span class="line sub" data-goal="sources">
          {t['plan.goal.sources'].split('{sources}')[0]}
          <SourcesText sources={goal.sources} t={t} />
        </span>
      )}
    </div>
  );
}

/** Which goals of the device are "manual", and whether a SoC goal can be asked for (the device's own goal reads a SoC). */
function wishCapabilities(device: PlanDevice | undefined): { manual: boolean; soc: boolean } {
  const goals = device?.goals ?? [];
  return { manual: goals.some((g) => g.source === 'manual'), soc: goals.some((g) => g.kind === 'soc') };
}

/** "2026-10-28T06:30" in the browser's zone: the value format of a datetime-local input. */
export function localInputValue(at: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${p(at.getMonth() + 1)}-${p(at.getDate())}T${p(at.getHours())}:${p(at.getMinutes())}`;
}

/**
 * "Want now": asks for energy (or a state of charge) by a deadline. Stored in the add-on's own state, dropped at the deadline, never an entity
 * write. The add-on's refusals are shown in words; nothing is assumed to have worked until it answers.
 */
function WantNow({ device, plan, control, ...ctx }: { device: string; plan: PlanDevice | undefined; control: PlanControl } & Ctx) {
  const { t } = ctx;
  const { manual, soc } = wishCapabilities(plan);
  const [kind, setKind] = useState<GoalKind>('energy');
  const [target, setTarget] = useState('');
  const [deadline, setDeadline] = useState('');
  const [hard, setHard] = useState(true);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: 'ok' | 'warn'; text: string } | null>(null);
  const effectiveKind: GoalKind = kind === 'soc' && !soc ? 'energy' : kind;

  const submit = async (event: Event) => {
    event.preventDefault();
    const value = Number(target.replace(',', '.'));
    const at = new Date(deadline);
    if (!Number.isFinite(value) || value <= 0) return setMessage({ tone: 'warn', text: t['plan.wish.noTarget'] });
    if (deadline === '' || Number.isNaN(at.getTime()) || at.getTime() <= Date.now()) return setMessage({ tone: 'warn', text: t['plan.wish.noDeadline'] });
    setSaving(true);
    setMessage(null);
    const refusal: GoalRefusal | null = await control.want({ device, kind: effectiveKind, target: value, deadline: at, hard });
    setSaving(false);
    setMessage(refusal === null ? { tone: 'ok', text: t['plan.wish.saved'] } : { tone: 'warn', text: t[`plan.wish.refused.${refusal}`] });
  };

  const cancel = async () => {
    setSaving(true);
    setMessage(null);
    const refusal = await control.cancel(device);
    setSaving(false);
    setMessage(refusal === null ? { tone: 'ok', text: t['plan.wish.cancelled'] } : { tone: 'warn', text: t[`plan.wish.refused.${refusal}`] });
  };

  return (
    <form class="plan-wish" onSubmit={(event) => void submit(event)} aria-label={t['plan.wish.title']} data-form="want-now">
      <strong>{t['plan.wish.title']}</strong>
      <label>
        <span>{t['plan.wish.kind']}</span>
        <select value={effectiveKind} disabled={saving} onChange={(event) => setKind((event.currentTarget as HTMLSelectElement).value as GoalKind)}>
          <option value="energy">{t['plan.wish.kind.energy']}</option>
          {soc && <option value="soc">{t['plan.wish.kind.soc']}</option>}
        </select>
      </label>
      <label>
        <span>{t['plan.wish.target']}</span>
        <input type="text" inputMode="decimal" value={target} disabled={saving} onInput={(event) => setTarget((event.currentTarget as HTMLInputElement).value)} />
      </label>
      <label>
        <span>{t['plan.wish.deadline']}</span>
        <input type="datetime-local" value={deadline} disabled={saving} onInput={(event) => setDeadline((event.currentTarget as HTMLInputElement).value)} />
      </label>
      <label class="plan-check">
        <input type="checkbox" checked={hard} disabled={saving} onChange={(event) => setHard((event.currentTarget as HTMLInputElement).checked)} />
        <span>{t['plan.wish.hard']}</span>
      </label>
      <span class="plan-actions">
        <button type="submit" disabled={saving}>
          {saving ? t['plan.wish.saving'] : t['plan.wish.submit']}
        </button>
        {manual && (
          <button type="button" disabled={saving} onClick={() => void cancel()}>
            {t['plan.wish.cancel']}
          </button>
        )}
      </span>
      {message !== null && (
        <span class="sub" role="status" data-tone={message.tone}>
          {message.text}
        </span>
      )}
      <span class="sub">{t['plan.wish.note']}</span>
    </form>
  );
}

/**
 * The goal row of the device card (UI-12): every goal of the device with its status, the next planned runs, what the merge set aside, and
 * the "want now" action. Without a goal it says so; without an answer it says the goal is not available instead of showing none.
 */
export function GoalRow({ device, control, ...ctx }: { device: string; control: PlanControl } & Ctx) {
  const { t, lang } = ctx;
  const view: PollView<Plan> = control.view;
  if (view.kind === 'loading') return <span class="sub-inline" data-goal-row="loading">{t['plan.goal.loading']}</span>;
  if (view.kind === 'unavailable') {
    return (
      <span class="sub-inline" data-goal-row="unavailable">
        {t['plan.goal.unavailable']}
      </span>
    );
  }
  const plan = view.data;
  const entry = plan.devices.find((d) => d.device === device);
  const goals = entry?.goals ?? [];
  const setAside = plan.superseded.filter((g) => g.device === device);
  const zone = browserZone();
  const runs = runsOf(entry?.entries ?? []).filter((r) => r.to.getTime() > plan.at.getTime());
  const fresh = plan.status === 'fresh';
  return (
    <>
      {goals.length === 0 ? (
        <span class="line sub-inline" data-goal-row="none">
          {t['plan.goal.none']}
        </span>
      ) : (
        goals.map((goal, i) => <GoalLines key={`${goal.source}-${goal.kind}-${goal.deadline?.getTime() ?? i}`} goal={goal} plan={plan} t={t} lang={lang} />)
      )}
      {setAside.map((goal, i) => (
        <span key={`aside-${i}`} class="line sub" data-goal-row="superseded">
          {fill(t['plan.goal.superseded'], {
            source: t[`plan.goal.source.${goal.source}`],
            target: goal.kind === 'soc' ? `${formatNumber(goal.targetSoc ?? 0, lang, 0)} %` : formatKwh(goal.targetKwh ?? 0, lang),
          })}
        </span>
      ))}
      {goals.length > 0 && plan.enabled && !fresh && (
        <span class="line sub" data-goal-row="not-fresh">
          {t['plan.goal.notFresh']}
        </span>
      )}
      {goals.length > 0 && plan.enabled && fresh && (
        <span class="line sub" data-goal-row="next">
          {t['plan.goal.next']}:{' '}
          {runs.length === 0 ? (
            t['plan.goal.nextNone']
          ) : (
            <>
              {runs.slice(0, 3).map((run) => (
                <span key={run.from.getTime()} class="line">
                  <RunText run={run} now={plan.at} t={t} lang={lang} zone={zone} />
                </span>
              ))}
            </>
          )}
        </span>
      )}
      {!plan.enabled && goals.length > 0 && (
        <span class="line sub" data-goal-row="not-enabled">
          {t['plan.goal.notEnabled']}
        </span>
      )}
      {plan.enabled && plan.dispatchMode === 'off' && (
        <span class="line sub" data-goal-row="preview">
          {t['plan.goal.preview']}
        </span>
      )}
      {plan.enabled && <WantNow device={device} plan={entry} control={control} t={t} lang={lang} />}
    </>
  );
}

