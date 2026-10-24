import type { Decision, Level, OperatingMode, ReasonCode, Verification } from './api/decisions';
import { formatNumber, formatQuantity } from './format';
import { fill, type Lang, type Messages } from './i18n';

// Human sentences for decisions (04-ALGORITHMS §6: the UI renders a sentence per reason code). Kept
// apart from the page so the full decision log (Stage 4) words every decision the same way.

/** The sentence for a reason code; every code of list version 2 has one in each language. */
export function reasonSentence(t: Messages, code: ReasonCode): string {
  return t[`reason.${code}`];
}

export function modeLabel(t: Messages, mode: OperatingMode): string {
  return mode === 'shadow' ? t.modeShadow : t.modeActive;
}

/** A device level in words: on/off for one relay, stages on for several, the setpoint for a modulating device. */
export function levelText(level: Level, t: Messages, lang: Lang): string {
  if (level.setpointA !== null) {
    return level.enabled ? formatQuantity(level.setpointA, 'A', lang) : t.levelStopped;
  }
  const relays = level.relays ?? [];
  if (relays.length === 0) return '—';
  if (relays.length === 1) return t[`relay.${relays[0] ? 'on' : 'off'}`];
  return fill(t.stagesOn, { on: String(relays.filter(Boolean).length), total: String(relays.length) });
}

/**
 * A verification's two values in their unit (A for a setpoint or draw current, W for a power step).
 * Nothing was measured against an expectation when the change was not applied (shadow mode), had
 * no new measurement in time, or cannot be measured at all: only the verdict is said then.
 */
export function verificationText(v: Verification, t: Messages, lang: Lang): string {
  if (v.verdict === 'not_applied' || v.verdict === 'timed_out' || v.verdict === 'unverifiable') return t[`verdict.${v.verdict}`];
  const unit = v.unit === 'amperes' ? 'A' : 'W';
  return fill(t.verificationDetail, {
    expected: formatQuantity(v.expected, unit, lang),
    measured: formatQuantity(v.measured, unit, lang),
    verdict: t[`verdict.${v.verdict}`],
  });
}

export interface DecisionText {
  /** What changed: "Switch off", "Safe state entered". */
  change: string;
  /** Whether a command went out, and why not; null for phase and system events. */
  command: string | null;
  /** The reason code's sentence; for the end of a condition, a sentence saying it ended. */
  reason: string;
  /** What else the record says: an outranked lockout, the inputs that produced it. */
  details: string[];
}

/** Device changes that could be a command: the record says whether one went out, or why not. */
const DEVICE_ACTIONS: ReadonlySet<Decision['action']> = new Set([
  'switch_off',
  'reduce_to_minimum',
  'release',
  'switch_on',
  'set_setpoint',
  'deferred',
]);

/**
 * Decisions whose reason code names the condition they report on rather than what happened (the
 * end of a condition, a timeout, a foreign load, which list version 2 has no code of its own for):
 * they are worded by their action.
 */
function actionReason(decision: Decision, t: Messages): string | null {
  switch (decision.action) {
    case 'kill_switch_disengaged':
      return t['reason.kill_switch.released'];
    case 'safe_state_exited':
      return t['reason.safe_state.exited'];
    case 'unresponsive_cleared':
      return t['reason.device_unresponsive.cleared'];
    case 'settling_timed_out':
      return t['reason.settling_window_open.timed_out'];
    case 'foreign_load_detected':
      return t['reason.foreign_load'];
    default:
      return null;
  }
}

export function describeDecision(decision: Decision, t: Messages, lang: Lang): DecisionText {
  const { action, inputs } = decision;
  const change =
    action === 'mode_resolved' ? `${t['action.mode_resolved']}: ${modeLabel(t, decision.mode)}` : t[`action.${action}`];

  let command: string | null = null;
  if (DEVICE_ACTIONS.has(action)) {
    if (decision.commanded) command = t.commanded;
    else if (decision.refusedBy !== null) command = `${t.notCommanded}: ${reasonSentence(t, decision.refusedBy)}`;
  }

  const details: string[] = [];
  // "Manual override" twice says nothing more: the origin is named unless the reason already does.
  if (decision.origin !== null && decision.scope === 'device' && !(decision.origin === 'override' && decision.reason === 'manual_override')) {
    details.push(t[`origin.${decision.origin}`]);
  }
  if (decision.group !== null) details.push(fill(t.groupDetail, { group: decision.group }));
  if (decision.target !== null && action !== 'override_set') {
    details.push(fill(t.targetDetail, { level: levelText(decision.target, t, lang) }));
  }
  if (decision.overridden !== null) details.push(`${t.overrides}: ${reasonSentence(t, decision.overridden)}`);
  // A device without a stop path cannot be switched off: a recorded "switch off" must not read as done.
  if (action === 'switch_off' && inputs.device !== null && !inputs.device.canSwitchOff) {
    details.push(t.decisionCannotSwitchOff);
  }
  const phase = inputs.phase;
  if (phase !== null) {
    if (phase.dataValid && phase.currentA !== null) {
      const parts = [`${phase.phase} ${formatQuantity(phase.currentA, 'A', lang)}`];
      if (phase.multiple !== null) parts.push(`${formatNumber(phase.multiple, lang, 2)} × In`);
      parts.push(`${t.budgetShort} ${formatNumber(phase.budget * 100, lang, 0)}%`);
      details.push(parts.join(' · '));
    } else if (phase.invalidReason !== null) {
      details.push(`${phase.phase}: ${t[`invalidReason.${phase.invalidReason}`]}`);
    } else {
      details.push(`${phase.phase}: ${t.meterDataInvalid}`);
    }
  }
  const dispatch = inputs.dispatch;
  if (dispatch !== null) {
    if (action === 'override_set') details.push(fill(t.overrideDetail, { mode: t[`override.${dispatch.override}`] }));
    for (const h of dispatch.headroom) {
      if (h.envelopeA === null) continue;
      details.push(
        fill(t.headroomDetail, {
          constraint: h.group === null ? h.phase : `${h.group} ${h.phase}`,
          envelope: formatQuantity(h.envelopeA, 'A', lang),
        }),
      );
    }
    const v = dispatch.verification;
    if (v !== null && action === 'foreign_load_detected') {
      details.push(fill(t.foreignDetail, { measured: formatQuantity(v.measured, 'W', lang), baseline: formatQuantity(v.expected, 'W', lang) }));
    } else if (v !== null && v.verdict !== 'timed_out') {
      details.push(verificationText(v, t, lang));
    }
  }
  if (inputs.killSwitch !== null) details.push(t[`killSwitchSource.${inputs.killSwitch}`]);
  if (inputs.safeStateCause !== null) details.push(t[`safeStateCause.${inputs.safeStateCause}`]);
  if (inputs.mode !== null) {
    details.push(`${t.modeCeiling}: ${modeLabel(t, inputs.mode.ceiling)}`);
    if (inputs.mode.note !== 'none') details.push(t[`modeNote.${inputs.mode.note}`]);
  }

  return { change, command, reason: actionReason(decision, t) ?? reasonSentence(t, decision.reason), details };
}
