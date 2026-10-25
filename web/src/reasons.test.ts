import { describe, expect, it } from 'vitest';
import { DECISION_ACTIONS, REASON_CODES, parseDecision, type Decision } from './api/decisions';
import sample from './api/guard.sample.json';
import { messages } from './i18n';
import { describeDecision, levelText, reasonSentence, verificationText } from './reasons';

// Positions in guard.sample.json's decisions (newest first).
const SWITCH_ON = 6;
const MARKED_UNRESPONSIVE = 7;
const OVERRIDE_SETPOINT = 8;
const KILL_SWITCH_ENGAGED = 10;
const MODE_RESOLVED = 11;

function decision(index: number, change?: (d: any) => void): Decision {
  const copy = JSON.parse(JSON.stringify(sample.decisions[index]));
  change?.(copy);
  return parseDecision(copy, 'decision');
}

// 04-ALGORITHMS §6: the UI renders a human sentence per reason code. A missing or shared sentence
// would make two different reasons read the same in the log, or show a blank reason.
describe('reason sentences', () => {
  it.each(Object.entries(messages))('%s has a sentence of its own for every code of list version 3', (_lang, t) => {
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
  it('says when a device asked to switch off cannot be switched off', () => {
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
    const kill = describeDecision(decision(KILL_SWITCH_ENGAGED), t, 'en');
    const mode = describeDecision(
      decision(
        MODE_RESOLVED,
        (d) =>
          (d.inputs.mode = {
            ceiling: 'active',
            uiChoice: 'active',
            note: 'active_not_enabled',
            controlAllowed: null,
            configurationValid: null,
            stateConfirmed: null,
            missing: [],
            source: null,
          }),
      ),
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
    expect(mode.details).toEqual(['add-on option: Active', t['modeNote.active_not_enabled']]);
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
    const release = describeDecision(decision(KILL_SWITCH_ENGAGED, (d) => (d.action = 'kill_switch_disengaged')), tl, lang);
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
      decision(KILL_SWITCH_ENGAGED, (d) =>
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

// Stage 3: dispatch's own decisions, worded so the shadow week can be reviewed from the log alone.
describe('describeDecision, dispatch decisions', () => {
  const t = messages.en;

  // Shadow mode: a would-start is recorded, never commanded, and the record says so.
  it('says a dispatch start was not commanded and shows who decided, the target and the headroom', () => {
    const text = describeDecision(decision(SWITCH_ON), t, 'en');

    expect(text.change).toBe('Switch on');
    expect(text.command).toBe('Not commanded: Shadow mode: computed and logged, nothing switched.');
    expect(text.reason).toBe('Headroom is available again.');
    expect(text.details).toEqual(['decided by dispatch', 'target: on', 'L2: may add 8.6 A']);
  });

  // DISP-07: the verification behind "not responding" is on the record.
  it('shows the verification that marked a device as not responding', () => {
    const text = describeDecision(decision(MARKED_UNRESPONSIVE), t, 'en');

    expect([text.change, text.reason]).toEqual(['Marked as not responding', 'The device does not respond to commands.']);
    expect(text.details).toContain('expected 1,700 W, measured 12 W: no response');
    expect(describeDecision(decision(MARKED_UNRESPONSIVE), messages.pl, 'pl').details).toContain(
      'oczekiwano 1700 W, zmierzono 12 W: brak reakcji',
    );
  });

  // SAFE-04: a forced change names the override and the circuit group it is constrained by.
  it('names the manual override and the circuit group of a forced change', () => {
    const text = describeDecision(decision(OVERRIDE_SETPOINT), t, 'en');

    expect(text.change).toBe('Setpoint change');
    expect(text.reason).toBe('Manual override.');
    expect(text.details).toEqual(['circuit group carport', 'target: 6 A', 'L1: may add 1.53 A', 'carport L1: may add 4 A']);
  });

  it('says which mode a manual override was set to', () => {
    const text = describeDecision(
      decision(OVERRIDE_SETPOINT, (d) => Object.assign(d, { action: 'override_set', target: null, refusedBy: null })),
      t,
      'en',
    );

    expect([text.change, text.command]).toEqual(['Manual override set', null]);
    expect(text.details).toContain('override: Force off');
  });

  // These records carry the code of the condition they report on; its sentence would misstate them
  // ("The device does not respond" for a device that responds again).
  it.each([
    ['unresponsive_cleared', 'device_unresponsive', 'A probe matched: the device responds again and takes part in dispatch.'],
    [
      'settling_timed_out',
      'settling_window_open',
      'A measurement the settling window waits for stayed silent until its hard timeout; no mismatch was counted.',
    ],
    ['foreign_load_detected', 'reserve_threshold', 'Load appeared on the phase without a command of ours.'],
  ] as const)('words %s by its action, not by its reason code', (action, reason, sentence) => {
    const text = describeDecision(decision(MARKED_UNRESPONSIVE, (d) => Object.assign(d, { action, reason })), t, 'en');

    expect(text.reason).toBe(sentence);
    expect(text.reason).not.toBe(reasonSentence(t, reason));
  });

  it('shows a foreign load against its baseline', () => {
    const text = describeDecision(
      decision(SWITCH_ON, (d) =>
        Object.assign(d, {
          scope: 'phase',
          device: null,
          action: 'foreign_load_detected',
          reason: 'reserve_threshold',
          refusedBy: null,
          target: null,
          inputs: { ...d.inputs, dispatch: { ...d.inputs.dispatch, verification: { expected: 400, measured: 2450, unit: 'watts', verdict: 'inconclusive' } } },
        }),
      ),
      t,
      'en',
    );

    expect(text.command).toBeNull();
    expect(text.details).toContain('foreign load 2,450 W against a baseline of 400 W');
  });

  it('says why a deferred change was not commanded', () => {
    const text = describeDecision(
      decision(SWITCH_ON, (d) => Object.assign(d, { action: 'deferred', refusedBy: 'lockout_min_off' })),
      t,
      'en',
    );

    expect([text.change, text.command]).toEqual(['Change deferred', 'Not commanded: Minimum off time not yet elapsed (lockout).']);
  });

  it.each([
    [{ relays: [true], setpointA: null, enabled: true }, 'on'],
    [{ relays: [false], setpointA: null, enabled: true }, 'off'],
    [{ relays: [true, true, false], setpointA: null, enabled: true }, '2 of 3 stages on'],
    [{ relays: null, setpointA: 13, enabled: true }, '13 A'],
    [{ relays: null, setpointA: 6, enabled: false }, 'off (stop actuator)'],
  ])('words the level %j as %s', (level, text) => {
    expect(levelText(level, t, 'en')).toBe(text);
  });

  // A change that was not applied (shadow mode) or never measured has no measured value to show:
  // "expected 0 W, measured 0 W" would read as a check that passed.
  it.each([
    [{ expected: 1700, measured: 12, unit: 'watts', verdict: 'mismatch' }, 'expected 1,700 W, measured 12 W: no response'],
    [{ expected: 6, measured: 9.6, unit: 'amperes', verdict: 'match' }, 'expected 6 A, measured 9.6 A: match'],
    [{ expected: 0, measured: 0, unit: 'watts', verdict: 'not_applied' }, 'not applied (shadow mode)'],
    [{ expected: 0, measured: 0, unit: 'watts', verdict: 'timed_out' }, 'no new measurement in time'],
    [{ expected: 6, measured: 0, unit: 'amperes', verdict: 'unverifiable' }, 'cannot be verified (no draw signal)'],
  ] as const)('words the verification %j as %s', (v, text) => {
    expect(verificationText(v, t, 'en')).toBe(text);
  });
});

// Stage C: the active self-test switches devices itself, and the log must say so; a refusal because
// another increase is in flight reads as its own reason, not as a closed settling window.
describe('describeDecision, control stage', () => {
  it('names the active self-test as the origin of a switching', () => {
    const switchOn = describeDecision(decision(SWITCH_ON, (d) => (d.origin = 'self_test')), messages.en, 'en');

    expect(switchOn.details).toContain('switched by the active phase self-test');
  });

  it('words change_limit apart from settling_window_open, in both languages', () => {
    for (const t of [messages.en, messages.pl]) {
      expect(reasonSentence(t, 'change_limit')).not.toBe(reasonSentence(t, 'settling_window_open'));
    }
    const deferred = describeDecision(decision(SWITCH_ON, (d) => Object.assign(d, { commanded: false, refusedBy: 'change_limit' })), messages.en, 'en');
    expect(deferred.command).toBe(`${messages.en.notCommanded}: ${messages.en['reason.change_limit']}`);
  });

  it('says a kill switch held after an untrusted state.json is held until confirmed', () => {
    const kill = describeDecision(decision(KILL_SWITCH_ENGAGED, (d) => (d.inputs.killSwitch = 'corrupt_state')), messages.en, 'en');

    expect(kill.details).toEqual([messages.en['killSwitchSource.corrupt_state']]);
  });
});
