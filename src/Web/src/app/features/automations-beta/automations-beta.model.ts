// ============================================================================
// Automations "Relay" beta — small view-model helpers shared by the page + its
// subcomponents. No state, no backend: pure mappings over the EXISTING
// AutomationRule / RuleAction / RuleConditionOp contract from core/models.ts.
// ============================================================================

import { AutomationRule, RuleAction, RuleConditionOp } from '../../core/models';

/** A trigger option: the kind + a human label + a short verb + a Material icon + numeric-payload unit. */
export interface TriggerOpt {
  kind: string;
  /** Long "When I …" label (matches the live /automations catalog VERBATIM). */
  label: string;
  /** Terse chip word for the WHEN chip (e.g. "Workout logged"). */
  chip: string;
  /** Material ligature for the trigger glyph. */
  icon: string;
  /** Unit word for a numeric payload ("minutes", "day number"); null => no numeric condition. */
  unit: string | null;
}

/** An action option: the enum value + a human label + a short chip word + a Material icon. */
export interface ActionOpt {
  value: RuleAction;
  label: string;
  chip: string;
  icon: string;
}

/**
 * The trigger catalog — kept byte-identical to the live `/automations` page's `triggers` so the beta
 * surface reads/writes the SAME kinds. (The server owns the authoritative set; this mirrors it for labels.)
 */
export const TRIGGERS: readonly TriggerOpt[] = [
  { kind: 'workout.logged', label: 'When I log a workout', chip: 'Workout logged', icon: 'fitness_center', unit: 'minutes' },
  { kind: 'challenge.dayComplete', label: 'When I complete a 75-Hard day', chip: '75-Hard day done', icon: 'military_tech', unit: 'day number' },
  { kind: 'challenge.started', label: 'When I start the 75-Hard challenge', chip: '75-Hard started', icon: 'flag', unit: null },
  { kind: 'hydration.goalHit', label: 'When I hit my water goal', chip: 'Water goal hit', icon: 'water_drop', unit: null },
];

/** The fixed, safe action catalog (own channels only) — mirrors the live action <select>. */
export const ACTIONS: readonly ActionOpt[] = [
  { value: 0, label: 'Notify me in-app', chip: 'In-app', icon: 'notifications_active' },
  { value: 1, label: 'Send to my Discord', chip: 'Discord', icon: 'forum' },
  { value: 2, label: 'Notify me in-app + Discord', chip: 'In-app + Discord', icon: 'campaign' },
];

export function triggerOpt(kind: string): TriggerOpt {
  return TRIGGERS.find(t => t.kind === kind) ?? TRIGGERS[0];
}

export function actionOpt(a: RuleAction): ActionOpt {
  return ACTIONS.find(o => o.value === a) ?? ACTIONS[0];
}

/** The THEN chips for a rule — one for In-app, one for Discord, in display order. */
export function actionChips(a: RuleAction): ActionOpt[] {
  if (a === 2) return [ACTIONS[0], ACTIONS[1]];
  return [actionOpt(a)];
}

/** A short, human description of a rule's condition (or "Always") — mirrors the live conditionLabel. */
export function conditionLabel(r: AutomationRule): string {
  const trig = triggerOpt(r.triggerKind);
  if (r.conditionOp === 0 || r.conditionValue == null || !trig.unit) return 'Always';
  const op = r.conditionOp === 1 ? '≥' : r.conditionOp === 2 ? '≤' : '=';
  return `${op} ${r.conditionValue} ${trig.unit}`;
}

/** The label for a condition operator (drives the create sheet's op picker). */
export function condOpLabel(op: RuleConditionOp): string {
  switch (op) {
    case 1: return 'At least (≥)';
    case 2: return 'At most (≤)';
    case 3: return 'Exactly (=)';
    default: return 'Any value';
  }
}

/** A friendly relative-time string ("just now", "3h ago", "Jun 12") from an ISO UTC stamp. */
export function relativeTime(iso: string | null | undefined): string {
  if (!iso) return '';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const diffMs = Date.now() - then;
  const sec = Math.round(diffMs / 1000);
  if (sec < 45) return 'just now';
  const min = Math.round(sec / 60);
  if (min < 60) return `${min}m ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.round(hr / 24);
  if (day < 7) return `${day}d ago`;
  return new Date(then).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}
