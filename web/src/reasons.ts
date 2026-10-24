import type { Decision, OperatingMode, ReasonCode } from './api/decisions';
import { formatNumber, formatQuantity } from './format';
import type { Lang, Messages } from './i18n';

// Human sentences for decisions (04-ALGORITHMS §6: the UI renders a sentence per reason code). Kept
// apart from the page so the full decision log (Stage 4) words every decision the same way.

/** The sentence for a reason code; every code of list version 2 has one in each language. */
export function reasonSentence(t: Messages, code: ReasonCode): string {
  return t[`reason.${code}`];
}

export function modeLabel(t: Messages, mode: OperatingMode): string {
  return mode === 'shadow' ? t.modeShadow : t.modeActive;
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

const DEVICE_ACTIONS: ReadonlySet<Decision['action']> = new Set(['switch_off', 'reduce_to_minimum', 'release']);

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
  if (decision.overridden !== null) details.push(`${t.overrides}: ${reasonSentence(t, decision.overridden)}`);
  // A modulating device has no stop path before Stage 3: a recorded "switch off" must not read as done.
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
  if (inputs.killSwitch !== null) details.push(t[`killSwitchSource.${inputs.killSwitch}`]);
  if (inputs.safeStateCause !== null) details.push(t[`safeStateCause.${inputs.safeStateCause}`]);
  if (inputs.mode !== null) {
    details.push(`${t.modeCeiling}: ${modeLabel(t, inputs.mode.ceiling)}`);
    if (inputs.mode.note !== 'none') details.push(t[`modeNote.${inputs.mode.note}`]);
  }

  // A release and a safe-state exit carry the code of the condition that ended; its sentence says
  // the condition holds, so these two are worded by what happened instead.
  const reason =
    action === 'kill_switch_disengaged'
      ? t['reason.kill_switch.released']
      : action === 'safe_state_exited'
        ? t['reason.safe_state.exited']
        : reasonSentence(t, decision.reason);

  return { change, command, reason, details };
}
