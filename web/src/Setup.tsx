import type { ComponentType } from 'preact';
import { messages, type Lang } from './i18n';
import { SetupConnection } from './SetupConnection';
import { SetupIssues } from './SetupIssues';
import { SetupLoads } from './SetupLoads';
import { SetupMeter } from './SetupMeter';
import { SetupMode } from './SetupMode';
import { SetupSources } from './SetupSources';
import { STEPS, issuesOfStep, type StepName } from './setupDraft';
import { StateLabel } from './ui';
import { useSetup, type StepProps } from './useSetup';
import './setup.css';

// Screen 03 — Setup wizard (UI-01 … UI-06), route #/setup/<1-5>. Structure and vocabulary follow
// docs/mockups "Konfiguracja wstępna": five steps in order (meter → connection → sources and
// storage → loads → mode), one shared draft loaded from the configuration file, every refusal shown
// at the step and field it names. The add-on validates and saves; this page never decides.

const COMPONENTS: Record<StepName, ComponentType<StepProps>> = {
  meter: SetupMeter,
  connection: SetupConnection,
  sources: SetupSources,
  loads: SetupLoads,
  mode: SetupMode,
};

/** #/setup/<n> for the n-th step, from 1. */
export function setupHref(step: StepName): string {
  return `#/setup/${STEPS.indexOf(step) + 1}`;
}

/** The step a #/setup route names; #/setup alone (and anything unknown) is the first. */
export function parseSetupStep(hash: string): StepName {
  const match = /^#\/setup\/([1-5])$/.exec(hash);
  return match === null ? 'meter' : STEPS[Number(match[1]) - 1]!;
}

export function Setup({ lang, step }: { lang: Lang; step: StepName }) {
  const t = messages[lang];
  const setup = useSetup();
  const goTo = (next: StepName) => {
    window.location.hash = setupHref(next);
  };
  const index = STEPS.indexOf(step);
  const Step = COMPONENTS[step];
  const view = setup.view;
  return (
    <div class="setup">
      <section class="card" aria-labelledby="setup-title">
        <header class="cardhead">
          <h2 id="setup-title">{t['setup.title']}</h2>
          <p class="sub">{t['setup.lead']}</p>
        </header>
        <nav class="stepper" aria-label={t['setup.stepsLabel']}>
          <ol>
            {STEPS.map((s) => {
              const errors = issuesOfStep(setup.issues, s).filter((i) => i.severity === 'error').length;
              return (
                <li key={s}>
                  <a href={setupHref(s)} aria-current={s === step ? 'step' : undefined} data-errors={errors > 0 ? String(errors) : undefined}>
                    {t[`setup.step.${s}`]}
                  </a>
                </li>
              );
            })}
          </ol>
        </nav>
        {setup.unsaved === 'restored' && (
          <p class="note" role="status" data-unsaved="restored">
            {t['setup.unsaved.restored']}{' '}
            <button type="button" class="linkbutton" onClick={setup.discardUnsaved}>
              {t['setup.unsaved.discard']}
            </button>
          </p>
        )}
        {setup.unsaved === 'discarded' && (
          <p class="note" role="status" data-unsaved="discarded">
            {t['setup.unsaved.discarded']}
          </p>
        )}
        {view !== null && view.fileState === 'missing' && <p class="note">{t['setup.file.missing']}</p>}
        {view !== null && view.fileState === 'invalid' && !view.unreadable && <p class="note">{t['setup.file.invalid']}</p>}
        {view !== null && view.unreadable && (
          <p class="note" role="status">
            <StateLabel tone="warn" label={t['setup.file.unreadable']} />
          </p>
        )}
        {view !== null && view.hasComments && <p class="note">{t['setup.file.comments']}</p>}
        {view !== null && view.preserved.length > 0 && (
          <details class="setup-preserved">
            <summary>{t['setup.preserved.title']}</summary>
            <p class="sub">{t['setup.preserved.lead']}</p>
            <ul>
              {view.preserved.map((p) => (
                <li key={p.path}>
                  <span class="mono">{p.path}</span> <span class="sub-inline">{p.summary}</span>
                </li>
              ))}
            </ul>
          </details>
        )}
        {setup.issues.some((i) => i.field === null) && <SetupIssues issues={setup.issues.filter((i) => i.field === null)} t={t} />}
      </section>
      {setup.load.kind === 'loading' && <p class="updated">{t['setup.loading']}</p>}
      {setup.load.kind === 'unavailable' && (
        <div class="card" role="alert">
          <p class="unavailable">
            <StateLabel tone="crit" label={t['setup.unavailable']} />
          </p>
          <button type="button" class="log-button" onClick={setup.reload}>
            {t['setup.retry']}
          </button>
        </div>
      )}
      {setup.load.kind === 'ready' && <Step setup={setup} t={t} lang={lang} goTo={goTo} />}
      <div class="setup-nav">
        {index > 0 && (
          <a class="log-button" href={setupHref(STEPS[index - 1]!)}>
            {t['setup.back']}
          </a>
        )}
        {index < STEPS.length - 1 && (
          <a class="log-button" href={setupHref(STEPS[index + 1]!)}>
            {t['setup.next']}
          </a>
        )}
      </div>
    </div>
  );
}
