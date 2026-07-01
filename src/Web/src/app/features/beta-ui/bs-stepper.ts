import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, model, output, signal,
} from '@angular/core';
import { Haptics } from '../../core/haptics';

/** Visual size of the stepper. `sm` is a compact inline control; `md` (default) is the standard row control. */
export type StepperSize = 'sm' | 'md';

/**
 * BETA-KIT Stepper — a minus / value / plus quantity control. The two round tap targets flank a
 * live value readout; each tap nudges the model by `step`, clamped to [min, max]. Press-and-hold
 * on either button AUTO-REPEATS and accelerates (a slow first tick, then faster) so large ranges
 * are reachable without a hundred taps — hold is opt-in via `repeat`. An optional `formatValue`
 * fn renders the number as a rich label (e.g. minutes → "1h 15m", grams → "250 g") while the model
 * still carries the raw number. A new beta-kit primitive (no flagship equivalent). Buttons disable
 * at the bounds; the whole control disables via `disabled`. Targets stay >=44px; the value uses the
 * Clash Display numeral. Honors reduced-motion via the page-host killswitch. Composes {@link Haptics}
 * for a faint per-step tick; otherwise dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-stepper
 *   inputs:    value (model<number>, two-way — the current quantity, default 0),
 *              min (number, default 0), max (number, default Infinity), step (number, default 1),
 *              disabled (boolean, default false), label (string, aria-label for the group),
 *              repeat (boolean, default true — press-and-hold auto-repeat with acceleration),
 *              size (StepperSize 'sm'|'md', default 'md'),
 *              formatValue ((v:number)=>string | null, default null — rich readout formatter),
 *              unit (string, default '' — plain suffix shown when no formatValue is given)
 *   outputs:   change (number — the new clamped value); the implicit valueChange also fires
 *
 * Usage: `<app-bs-stepper [(value)]="reps" [min]="1" [max]="20" label="Reps" (change)="onReps($event)" />`
 *        `<app-bs-stepper [(value)]="mins" [step]="5" [max]="180" [formatValue]="fmtDuration" />`
 */
@Component({
  selector: 'app-bs-stepper',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'bs-step',
    role: 'group',
    '[attr.aria-label]': 'label() || null',
    '[class.bs-step--sm]': "size() === 'sm'",
    '[class.is-disabled]': 'disabled()',
  },
  template: `
    <button type="button" class="bs-step-btn" aria-label="Decrease"
            [disabled]="disabled() || atMin()"
            (pointerdown)="onHold($event, -1)"
            (pointerup)="endHold()" (pointercancel)="endHold()" (pointerleave)="endHold()"
            (click)="onTap(-1)"
            (keydown)="onKeydown($event)">
      <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
        <path d="M5 12h14" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/>
      </svg>
    </button>

    <output class="bs-step-val" aria-live="polite">{{ display() }}</output>

    <button type="button" class="bs-step-btn" aria-label="Increase"
            [disabled]="disabled() || atMax()"
            (pointerdown)="onHold($event, 1)"
            (pointerup)="endHold()" (pointercancel)="endHold()" (pointerleave)="endHold()"
            (click)="onTap(1)"
            (keydown)="onKeydown($event)">
      <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
        <path d="M12 5v14M5 12h14" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/>
      </svg>
    </button>
  `,
  styles: [`
    :host(.bs-step) {
      display: inline-flex; align-items: center; gap: 4px;
      padding: 4px; box-sizing: border-box;
      background: var(--bg-sink); border: 1px solid var(--hairline);
      border-radius: var(--r-pill); box-shadow: var(--press);
    }
    :host(.bs-step.is-disabled) { opacity: .5; pointer-events: none; }
    .bs-step-btn {
      flex: 0 0 auto; width: 44px; height: 44px; min-width: 44px; min-height: 44px;
      display: inline-flex; align-items: center; justify-content: center;
      border: none; border-radius: var(--r-pill);
      background: var(--bg-rise); color: var(--ink);
      box-shadow: var(--lift-1);
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      transition: transform 120ms var(--ease-out), box-shadow 120ms var(--ease-out), background 160ms var(--ease-out);
    }
    .bs-step-btn:active { transform: scale(.92); box-shadow: var(--press); }
    .bs-step-btn:disabled { opacity: .4; cursor: default; box-shadow: none; }
    .bs-step-btn:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .bs-step-val {
      flex: 1 1 auto; min-width: 56px; text-align: center; padding: 0 8px;
      font-family: var(--font-display); font-variant-numeric: tabular-nums;
      font-size: 20px; font-weight: 600; letter-spacing: -.02em; color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    /* compact variant — smaller round targets (still >=44px hit via the pseudo pad) + tighter numeral */
    :host(.bs-step--sm) { gap: 2px; padding: 3px; }
    :host(.bs-step--sm) .bs-step-btn { width: 44px; height: 44px; }
    :host(.bs-step--sm) .bs-step-val { min-width: 40px; font-size: 16px; padding: 0 4px; }
  `],
})
export class BetaStepper {
  /** Two-way current quantity. */
  readonly value = model<number>(0);
  /** Lower bound (inclusive). */
  readonly min = input<number>(0);
  /** Upper bound (inclusive). Defaults to no ceiling. */
  readonly max = input<number>(Infinity);
  /** Amount each nudge adds/subtracts. */
  readonly step = input<number>(1);
  /** When true the whole control is inert. */
  readonly disabled = input<boolean>(false);
  /** aria-label for the group. */
  readonly label = input<string>('');
  /** Press-and-hold auto-repeat with acceleration. */
  readonly repeat = input<boolean>(true);
  /** Visual size. */
  readonly size = input<StepperSize>('md');
  /** Optional rich readout formatter (e.g. minutes → "1h 15m"). Null shows the raw number (+ unit). */
  readonly formatValue = input<((v: number) => string) | null>(null);
  /** Plain suffix appended to the raw number when no formatValue is supplied (e.g. 'g', 'kcal'). */
  readonly unit = input<string>('');
  /** Fired with the new clamped value after every change. */
  readonly change = output<number>();

  private readonly haptics = inject(Haptics);
  private readonly destroyRef = inject(DestroyRef);

  /** True when the value sits at (or below) the lower bound. */
  protected readonly atMin = computed(() => this.value() <= this.min());
  /** True when the value sits at (or above) the upper bound. */
  protected readonly atMax = computed(() => this.value() >= this.max());
  /** The formatted readout (rich formatter → raw+unit → raw). */
  protected readonly display = computed(() => {
    const v = this.value();
    const fmt = this.formatValue();
    if (fmt) return fmt(v);
    const u = this.unit();
    return u ? `${v} ${u}` : `${v}`;
  });

  /** Timer handle for an active press-and-hold, and the current dir (+1/-1). */
  private holdTimer: ReturnType<typeof setTimeout> | null = null;
  private holdFired = false;
  private tick = 0;

  constructor() {
    // Never leak a running repeat timer if the component is torn down mid-hold.
    this.destroyRef.onDestroy(() => this.endHold());
  }

  /** Clamp `n` to [min, max] and snap NaN to min. */
  private clamp(n: number): number {
    if (Number.isNaN(n)) return this.min();
    return Math.min(this.max(), Math.max(this.min(), n));
  }

  /** Apply one nudge in `dir` (+1/-1); returns true if the value actually moved. */
  private nudge(dir: 1 | -1): boolean {
    if (this.disabled()) return false;
    const next = this.clamp(this.value() + dir * this.step());
    if (next === this.value()) return false;
    this.value.set(next);
    this.change.emit(next);
    this.haptics.select();
    return true;
  }

  /** Plain click handler (also fires after a hold's pointerup — guarded so a hold doesn't double-step). */
  protected onTap(dir: 1 | -1): void {
    if (this.holdFired) { this.holdFired = false; return; }
    this.nudge(dir);
  }

  /** pointerdown — begin a press-and-hold auto-repeat when enabled (the initial step is left to the click). */
  protected onHold(e: PointerEvent, dir: 1 | -1): void {
    if (this.disabled() || !this.repeat() || e.button != null && e.button > 0) return;
    this.endHold();
    this.holdFired = false;
    this.tick = 0;
    // Fire the first step immediately, then schedule an accelerating repeat.
    if (!this.nudge(dir)) return;
    this.holdFired = true;
    (e.target as HTMLElement).setPointerCapture?.(e.pointerId);
    this.scheduleRepeat(dir);
  }

  /** Schedule the next repeat tick; the interval shrinks (500→400→…→80ms floor) so holds accelerate. */
  private scheduleRepeat(dir: 1 | -1): void {
    const delay = Math.max(80, 500 - this.tick * 60);
    this.holdTimer = setTimeout(() => {
      this.tick++;
      if (this.nudge(dir)) this.scheduleRepeat(dir);
      else this.endHold();
    }, delay);
  }

  /** Cancel any running press-and-hold. */
  protected endHold(): void {
    if (this.holdTimer !== null) { clearTimeout(this.holdTimer); this.holdTimer = null; }
  }

  /** Arrow keys nudge; the buttons' native Enter/Space clicks handle the rest. */
  protected onKeydown(e: KeyboardEvent): void {
    if (this.disabled()) return;
    if (e.key === 'ArrowUp' || e.key === 'ArrowRight') { e.preventDefault(); this.nudge(1); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowLeft') { e.preventDefault(); this.nudge(-1); }
  }
}
