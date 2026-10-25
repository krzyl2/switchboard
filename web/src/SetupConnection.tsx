import { useEffect, useRef, useState } from 'preact/hooks';
import { SetupIssues } from './SetupIssues';
import { firstOnPage, focusField, parseNumber, TOPOLOGIES, topologyPhases } from './SetupMeter';
import type { PhaseValues, ReserveExplanation, ReservePhase } from './api/setup';
import type { PhaseName } from './api/signals';
import { formatAge, formatAmps, formatFixed, formatInterval, formatKw, formatNumber } from './format';
import { fill, type Lang, type Messages } from './i18n';
import { fieldId, issuesOfStep } from './setupDraft';
import { StateLabel } from './ui';
import type { StepProps } from './useSetup';
import './setupConnection.css';

// Step 2 of screen 03 (docs/mockups "Parametry przyłącza"): characteristic, voltage and the reserve
// per phase (CONN-01, CONN-04), with the minimum safe reserve derived from the detected refresh next
// to each reserve (SIG-07) and the kettle arithmetic of 04 §1 in the user's own numbers. Every
// number comes from the add-on (ReserveExplanation, the runtime's own tier table and
// MinimumSafeReserve): this page never computes a margin of its own.

/** After the last edit of a connection field, the add-on is asked for the numbers again this much later (presentation timing, not a control parameter). */
const RECALCULATE_AFTER_MS = 800;

const CHARACTERISTICS = ['B', 'C', 'unknown'] as const;

type CheckView = { kind: 'idle' } | { kind: 'refused'; field: string } | { kind: 'failed' };

function reserveOf(values: PhaseValues | null, phase: PhaseName): number | null {
  return values?.[phase] ?? null;
}

/** The label of a step-2 field, for the sentence that says what to fix (UI-05). */
function fieldLabel(field: string, t: Messages): string {
  if (field === 'connection.characteristic') return t['setupConnection.characteristic.label'];
  if (field === 'connection.voltageV') return t['setupConnection.voltage.label'];
  const reserve = /^connection\.reserveW\.(L[123])$/.exec(field);
  if (reserve !== null) return fill(t['setupConnection.reserve.phase'], { phase: reserve[1]! });
  return field;
}

/**
 * Step 2 (Connection): characteristic (never prefilled), voltage (prefilled from the add-on's
 * starting value), reserve per phase with the minimum safe reserve beside it, and the kettle
 * arithmetic. The layout and breaker rating are asked in step 1 and summarized here. The add-on
 * recomputes the numbers shortly after every change; "Check and continue" moves on only when no
 * step-2 field is refused.
 */
export function SetupConnection({ setup, t, lang, goTo }: StepProps) {
  const connection = setup.draft.connection;
  const topology = connection.topology;
  const phases = topologyPhases(topology);
  const [check, setCheck] = useState<CheckView>({ kind: 'idle' });
  const [failed, setFailed] = useState(false);
  const validatedKey = useRef<string | null>(null);
  const key = JSON.stringify(connection);
  const missingReserve = phases.find((p) => reserveOf(connection.reserveW, p) === null);
  const complete =
    (TOPOLOGIES as readonly string[]).includes(topology ?? '') &&
    (connection.breakerRatingA ?? 0) > 0 &&
    connection.characteristic !== null &&
    (connection.voltageV ?? 0) > 0 &&
    missingReserve === undefined;

  // The voltage starts at the add-on's prefill (the user confirms or changes it); the characteristic never does.
  useEffect(() => {
    if (connection.voltageV === null && setup.prefillVoltageV !== null) setup.set('connection.voltageV', setup.prefillVoltageV);
  }, []);

  // The numbers follow the draft: asked at once on arrival, then shortly after the last change.
  useEffect(() => {
    if (!complete || setup.busy !== 'idle' || validatedKey.current === key) return;
    const timer = setTimeout(
      () => {
        validatedKey.current = key;
        void setup.validate().then((result) => setFailed(result === null));
      },
      validatedKey.current === null ? 0 : RECALCULATE_AFTER_MS,
    );
    return () => clearTimeout(timer);
  }, [key, complete, setup.busy]);

  const run = async () => {
    setCheck({ kind: 'idle' });
    if (missingReserve !== undefined) {
      const field = `connection.reserveW.${missingReserve}`;
      setCheck({ kind: 'refused', field });
      focusField(field);
      return;
    }
    validatedKey.current = key;
    const result = await setup.validate();
    if (result === null) {
      setCheck({ kind: 'failed' });
      return;
    }
    setFailed(false);
    const refused = firstOnPage(
      issuesOfStep(result.issues, 'connection')
        .filter((issue) => issue.severity === 'error' && issue.field !== null)
        .map((issue) => issue.field!),
    );
    if (refused !== undefined) {
      setCheck({ kind: 'refused', field: refused });
      focusField(refused);
      return;
    }
    goTo('sources');
  };

  const reserve = complete ? (setup.validation?.reserve ?? null) : null;
  const recalculating = complete && (setup.busy === 'validating' || validatedKey.current !== key);
  // One key per edit: the reducer applies it to the latest draft, so quick edits of several phases never undo each other.
  const setReserve = (phase: PhaseName, value: number | null) => setup.set(`connection.reserveW.${phase}`, value);

  return (
    <section class="card setup-step sc-scope" aria-labelledby="setup-connection-title" data-step="connection">
      <header class="cardhead">
        <h2 id="setup-connection-title">{t['setupConnection.title']}</h2>
        <p class="sub">{t['setupConnection.stepLine']}</p>
      </header>
      <p>{t['setupConnection.lead']}</p>

      <div class="sc-section" data-part="layout">
        <h3>{t['setupConnection.layout.title']}</h3>
        <p class="sc-layout">
          <span data-topology={topology ?? ''}>
            {topology !== null && (TOPOLOGIES as readonly string[]).includes(topology)
              ? t[`setupMeter.topology.${topology as (typeof TOPOLOGIES)[number]}`]
              : t['setupConnection.layout.missing']}
          </span>
          <span class="mono">
            {connection.breakerRatingA !== null
              ? fill(t['setupConnection.layout.rating'], { rating: formatNumber(connection.breakerRatingA, lang, 1) })
              : t['setupConnection.layout.missing']}
          </span>
          <button type="button" class="linkbutton" onClick={() => goTo('meter')}>
            {t['setupConnection.layout.change']}
          </button>
        </p>
        <p class="sub">{t['setupConnection.layout.hint']}</p>
      </div>

      <div class="sc-section sc-fields" data-part="breaker">
        <div class="picker">
          <label for={fieldId('connection.characteristic')}>{t['setupConnection.characteristic.label']}</label>
          <select
            id={fieldId('connection.characteristic')}
            value={connection.characteristic ?? ''}
            onChange={(event) => {
              const value = (event.currentTarget as HTMLSelectElement).value;
              setup.set('connection.characteristic', value === '' ? null : value);
            }}
          >
            <option value="">{t['setupConnection.characteristic.choose']}</option>
            {CHARACTERISTICS.map((c) => (
              <option key={c} value={c}>
                {t[`setupConnection.characteristic.${c}`]}
              </option>
            ))}
            {connection.characteristic !== null && !(CHARACTERISTICS as readonly string[]).includes(connection.characteristic) && (
              <option value={connection.characteristic}>{connection.characteristic}</option>
            )}
          </select>
          <span class="sub">{t['setupConnection.characteristic.hint']}</span>
        </div>
        <div class="picker">
          <label for={fieldId('connection.voltageV')}>{t['setupConnection.voltage.label']}</label>
          <input
            id={fieldId('connection.voltageV')}
            type="number"
            inputMode="decimal"
            min="0"
            step="any"
            value={connection.voltageV ?? ''}
            onInput={(event) => setup.set('connection.voltageV', parseNumber((event.currentTarget as HTMLInputElement).value))}
          />
          <span class="sub">{t['setupConnection.voltage.hint']}</span>
        </div>
      </div>

      <div class="sc-section" data-part="reserve">
        <h3>{t['setupConnection.reserve.title']}</h3>
        <p class="sub">{t['setupConnection.reserve.hint']}</p>
        <div class="sc-fields">
          {phases.map((phase) => {
            const value = reserveOf(connection.reserveW, phase);
            return (
              <div class="picker" key={phase} data-phase={phase}>
                <label for={fieldId(`connection.reserveW.${phase}`)}>{fill(t['setupConnection.reserve.phase'], { phase })}</label>
                <input
                  id={fieldId(`connection.reserveW.${phase}`)}
                  type="number"
                  inputMode="decimal"
                  min="0"
                  step="any"
                  value={value ?? ''}
                  onInput={(event) => setReserve(phase, parseNumber((event.currentTarget as HTMLInputElement).value))}
                />
                <MinimumLine value={value} report={reserve?.perPhase.find((p) => p.phase === phase)} shown={reserve !== null} t={t} lang={lang} />
              </div>
            );
          })}
        </div>
        <p class="sub">{t['setupConnection.reserve.why']}</p>
      </div>

      <div class="sc-section" data-part="arithmetic">
        <h3>{t['setupConnection.math.title']}</h3>
        {reserve !== null && connection.breakerRatingA !== null ? (
          <Arithmetic
            explanation={reserve.explanation}
            ratingA={connection.breakerRatingA}
            uneven={new Set(phases.map((p) => reserveOf(connection.reserveW, p))).size > 1}
            busy={recalculating}
            t={t}
            lang={lang}
          />
        ) : (
          <p class="sub">{t['setupConnection.math.pending']}</p>
        )}
        {recalculating && <p class="updated">{t['setupConnection.math.recalculating']}</p>}
        {failed && (
          <p class="note" role="alert">
            <StateLabel tone="crit" label={t['setupConnection.math.failed']} />
          </p>
        )}
      </div>

      <SetupIssues issues={setup.issuesOf('connection')} t={t} />
      {check.kind === 'refused' && (
        <p class="note" role="alert" data-refused={check.field}>
          <StateLabel tone="crit" label={fill(t['setupConnection.check.refused'], { field: fieldLabel(check.field, t) })} />
          {check.field === `connection.reserveW.${missingReserve ?? ''}` && <span class="sub">{t['setupConnection.reserve.empty']}</span>}
        </p>
      )}
      {check.kind === 'failed' && (
        <p class="note" role="alert">
          <StateLabel tone="crit" label={t['setupConnection.check.failed']} />
        </p>
      )}
      <div class="sc-actions">
        <button type="button" class="log-button" disabled={setup.busy !== 'idle'} onClick={() => void run()}>
          {setup.busy === 'validating' ? t['setupConnection.check.running'] : t['setupConnection.check.run']}
        </button>
      </div>
    </section>
  );
}

/** The minimum safe reserve beside one phase's reserve, with the refresh it was derived from (SIG-07). */
function MinimumLine({ value, report, shown, t, lang }: { value: number | null; report: ReservePhase | undefined; shown: boolean; t: Messages; lang: Lang }) {
  if (value === null) return <span class="sub">{t['setupConnection.reserve.empty']}</span>;
  if (!shown) return null;
  if (report === undefined) return <span class="sub">{t['setupConnection.reserve.noMinimum']}</span>;
  // One decimal: a reserve of 631.5 W next to a minimum rounded to 632 W would read as below it.
  const values = { minimum: formatNumber(report.minimumSafeW, lang, 1), refresh: formatInterval(report.refreshS, lang) };
  return (
    <span class="sub" data-minimum={report.minimumSafeW} data-detected={String(report.refreshDetected)}>
      {fill(t[report.refreshDetected ? 'setupConnection.reserve.minimum' : 'setupConnection.reserve.minimumMaxAge'], values)}
      {report.belowMinimum && (
        <>
          {' '}
          <StateLabel tone="warn" label={t['setupConnection.reserve.below']} />
        </>
      )}
    </span>
  );
}

/**
 * The kettle arithmetic of 04 §1 with the user's own numbers (CONN-04): the phase filled up to the
 * reserve, then the reference step. Where it lands on the breaker curve decides the sentence: under
 * the rating, tolerated for a while with a reduction planned, or a shed (the reserve is too small).
 */
function Arithmetic({ explanation: e, ratingA, uneven, busy, t, lang }: { explanation: ReserveExplanation; ratingA: number; uneven: boolean; busy: boolean; t: Messages; lang: Lang }) {
  let band: string;
  switch (e.reaction) {
    case 'may_add':
    case 'hold':
      band = t['setupConnection.math.under'];
      break;
    case 'reduce_ordinary_cycle':
    case 'reduce_next_cycle':
      band = fill(t['setupConnection.math.reduce'], { allowed: e.allowedS === null ? t['common.none'] : formatAge(e.allowedS, lang) });
      break;
    case 'shed_one_immediately':
    case 'shed_all_on_phase':
      band = t['setupConnection.math.shed'];
      break;
  }
  return (
    <div class="note sc-math" data-reaction={e.reaction} aria-busy={busy ? 'true' : undefined}>
      <p>
        <strong>{fill(t['setupConnection.math.head'], { rating: `${formatNumber(ratingA, lang, 1)} A`, phase: formatKw(e.phaseW, lang) })}</strong>{' '}
        {fill(t['setupConnection.math.allocatable'], { reserve: formatKw(e.reserveW, lang), allocatable: formatKw(e.allocatableW, lang) })}
      </p>
      <p>
        {fill(t['setupConnection.math.kettle'], { step: formatKw(e.referenceStepW, lang), worst: formatAmps(e.worstCaseA, lang), multiple: formatFixed(e.multiple, lang, 2) })}{' '}
        <strong data-part="band">{band}</strong>
      </p>
      <p>{t['setupConnection.math.curve']}</p>
      {uneven && <p class="sub">{t['setupConnection.math.smallest']}</p>}
    </div>
  );
}
