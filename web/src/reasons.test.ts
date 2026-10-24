import { describe, expect, it } from 'vitest';
import { DECISION_ACTIONS, REASON_CODES, parseDecision, type Decision } from './api/decisions';
import sample from './api/guard.sample.json';
import { messages } from './i18n';
import { describeDecision, reasonSentence } from './reasons';

function decision(index: number, change?: (d: any) => void): Decision {
  const copy = JSON.parse(JSON.stringify(sample.decisions[index]));
  change?.(copy);
  return parseDecision(copy, 'decision');
}

// 04-ALGORITHMS §6: the UI renders a human sentence per reason code. A missing or shared sentence
// would make two different reasons read the same in the log, or show a blank reason.
describe('reason sentences', () => {
  it.each(Object.entries(messages))('%s has a sentence of its own for every code of list version 2', (_lang, t) => {
    const sentences = REASON_CODES.map((code) => reasonSentence(t, code));

    expect(sentences.every((s) => typeof s === 'string' && s.trim().length > 0)).toBe(true);
    expect(new Set(sentences).size).toBe(REASON_CODES.length);
  });

  it('translates every reason and every action into Polish', () => {
    for (const code of REASON_CODES) expect(reasonSentence(messages.pl, code)).not.toBe(reasonSentence(messages.en, code));
    for (const action of DECISION_ACTIONS) expect(messages.pl[`action.${action}`]).not.toBe(messages.en[`action.${action}`]);
  });
});

describe('describeDecision', () => {
  const t = messages.en;

  // Shadow mode commands nothing; a shed that reads as done would misstate what happened.
  it('says that a shed in shadow mode was not commanded, and why', () => {
    const text = describeDecision(decision(0), t, 'en');

    expect(text.change).toBe('Switch off');
    expect(text.command).toBe('Not commanded: Shadow mode: computed and logged, nothing switched.');
    expect(text.reason).toBe('Phase current above a tier of the breaker profile.');
  });

  // Lockouts never outrank protection; the record says which one protection overrode.
  it('names the lockout a protective shed took precedence over', () => {
    expect(describeDecision(decision(0), t, 'en').details).toContain(
      'Takes precedence over: Minimum on time not yet elapsed (lockout).',
    );
    expect(describeDecision(decision(1), t, 'en').details.join(' ')).not.toContain('Takes precedence over');
  });

  // A modulating device has no stop path before Stage 3: "switch off" must not read as done.
  it('says when a device asked to switch off cannot be switched off yet', () => {
    const cannot = describeDecision(decision(1), t, 'en');
    const can = describeDecision(decision(0), t, 'en');

    expect(cannot.details).toContain(t.decisionCannotSwitchOff);
    expect(can.details).not.toContain(t.decisionCannotSwitchOff);
  });

  it('shows the phase inputs that produced the decision', () => {
    expect(describeDecision(decision(0), t, 'en').details).toContain('L1 33 A · 1.32 × In · budget 62%');
    expect(describeDecision(decision(0), messages.pl, 'pl').details).toContain('L1 33 A · 1,32 × In · budżet 62%');
    expect(describeDecision(decision(3), t, 'en').details).toContain('L2: current reading stale');
  });

  it('describes phase and system events without a command line', () => {
    const safe = describeDecision(decision(3), t, 'en');
    const kill = describeDecision(decision(7), t, 'en');
    const mode = describeDecision(
      decision(8, (d) => (d.inputs.mode = { ceiling: 'active', uiChoice: 'active', note: 'active_unavailable_before_stage3' })),
      t,
      'en',
    );

    expect([safe.change, safe.command]).toEqual(['Safe state entered', null]);
    expect([kill.change, kill.reason, kill.details]).toEqual([
      'Kill switch engaged',
      'The kill switch is engaged.',
      ['from Home Assistant'],
    ]);
    expect(mode.change).toBe('Mode resolved: Shadow');
    expect(mode.details).toEqual(['add-on option: Active', t['modeNote.active_unavailable_before_stage3']]);
  });

  // The record of a condition ending carries the code of that condition; rendering its sentence
  // would read "Kill switch released: The kill switch is engaged."
  it.each([
    ['en', 'Kill switch released', 'The kill switch was released.', 'Safe state left', 'Valid data again; the safe state ended.'],
    [
      'pl',
      'Wyłączenie wstrzymania sterowania',
      'Wstrzymanie sterowania zostało wyłączone.',
      'Wyjście ze stanu bezpiecznego',
      'Dane znów są poprawne; stan bezpieczny zakończony.',
    ],
  ] as const)('words the end of a condition by its action (%s)', (lang, released, releasedReason, exited, exitedReason) => {
    const tl = messages[lang];
    const release = describeDecision(decision(7, (d) => (d.action = 'kill_switch_disengaged')), tl, lang);
    const exit = describeDecision(
      decision(3, (d) => Object.assign(d, { action: 'safe_state_exited', reason: 'guard_stale_signal' })),
      tl,
      lang,
    );

    expect([release.change, release.reason]).toEqual([released, releasedReason]);
    expect([exit.change, exit.reason]).toEqual([exited, exitedReason]);
    expect(release.reason).not.toBe(reasonSentence(tl, 'kill_switch'));
    expect(exit.reason).not.toBe(reasonSentence(tl, 'guard_stale_signal'));
  });

  // MODE-03: a system-wide safe state says what caused it (a stop, a missing file, no meter data).
  it.each([
    ['en', 'the add-on is stopping'],
    ['pl', 'dodatek się zatrzymuje'],
  ] as const)('names the cause of a system-wide safe state (%s)', (lang, cause) => {
    const stop = describeDecision(
      decision(7, (d) =>
        Object.assign(d, {
          action: 'safe_state_entered',
          reason: 'safe_state',
          inputs: { ...d.inputs, killSwitch: null, safeStateCause: 'shutdown' },
        }),
      ),
      messages[lang],
      lang,
    );

    expect(stop.details).toEqual([cause]);
  });

  it('states a commanded decision as commanded', () => {
    expect(describeDecision(decision(0, (d) => Object.assign(d, { commanded: true, refusedBy: null })), t, 'en').command).toBe(
      'Commanded',
    );
  });
});
