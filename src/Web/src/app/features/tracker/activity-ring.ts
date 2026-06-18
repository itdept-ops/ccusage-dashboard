import { Component, computed, input } from '@angular/core';

/**
 * A lightweight SVG steps ring — the activity twin of {@link HydrationRing}. Shows progress of `steps`
 * toward `goal` (the daily step goal), with the headline being the step count and a "x of y" caption.
 * When no goal is set the ring stays a subtle full track and the caption just reads "steps". Pure SVG
 * (no chart lib), theme-driven via --tech tokens; turns to the success colour once the goal is met.
 * A visually-hidden text equivalent is supplied via the role="img" aria-label.
 */
@Component({
  selector: 'app-activity-ring',
  template: `
    <svg viewBox="0 0 120 120" class="ring" role="img" [attr.aria-label]="ariaLabel()">
      <circle class="ring__track" cx="60" cy="60" [attr.r]="radius" fill="none" stroke-width="11" />
      <circle class="ring__bar" cx="60" cy="60" [attr.r]="radius" fill="none" stroke-width="11"
              stroke-linecap="round" transform="rotate(-90 60 60)"
              [class.ring__bar--met]="met()"
              [attr.stroke-dasharray]="circumference"
              [attr.stroke-dashoffset]="dashOffset()" />
      <text x="60" y="55" class="ring__value" text-anchor="middle">{{ headline() }}</text>
      <text x="60" y="73" class="ring__label" text-anchor="middle">{{ caption() }}</text>
    </svg>
  `,
  styleUrl: './activity-ring.scss',
})
export class ActivityRing {
  /** Steps logged for the day. */
  readonly steps = input.required<number>();
  /** The daily step goal (> 0), or null/undefined when none is set. */
  readonly goal = input<number | null | undefined>(null);

  readonly radius = 52;
  readonly circumference = 2 * Math.PI * this.radius;

  /** Progress toward the goal (0..1+, clamped at 1 for the arc fill). */
  private readonly progress = computed(() => {
    const g = this.goal();
    if (!g || g <= 0) return 0;
    return this.steps() / g;
  });

  /** True once the goal is reached or exceeded — drives the celebratory colour + caption. */
  readonly met = computed(() => {
    const g = this.goal();
    return !!g && g > 0 && this.steps() >= g;
  });

  readonly dashOffset = computed(() => {
    const g = this.goal();
    // No goal → subtle full track (the count still shows in the centre).
    if (!g || g <= 0) return this.circumference;
    return this.circumference * (1 - Math.min(1, this.progress()));
  });

  /** The big centre number: the step count (e.g. "8,240"). */
  readonly headline = computed(() => Math.round(this.steps()).toLocaleString());

  /** Sub-caption: "x of y" toward the goal, "goal met", or just "steps" when no goal. */
  readonly caption = computed(() => {
    const g = this.goal();
    if (!g || g <= 0) return 'steps';
    if (this.met()) return 'goal met';
    return `of ${g.toLocaleString()}`;
  });

  readonly ariaLabel = computed(() => {
    const g = this.goal();
    if (g && g > 0) {
      if (this.met()) return `Step goal met: ${this.headline()} of a ${g.toLocaleString()} step goal.`;
      return `${this.headline()} of a ${g.toLocaleString()} step goal.`;
    }
    return `${this.headline()} steps.`;
  });
}
