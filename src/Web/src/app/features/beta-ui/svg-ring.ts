import {
  ChangeDetectionStrategy, Component, computed, input,
} from '@angular/core';

/**
 * BETA-KIT SvgRing — a single stroke progress ring rendered as an SVG arc with an accent
 * <linearGradient> fill (never flat, matching the flagship hero-ring convention). Usable for
 * mini metrics (a streak %, a goal %, a budget burn-down) anywhere on a beta page. The arc
 * starts at 12 o'clock, rounds its cap, and eases its dashoffset with --ease-spring. An
 * optional center label/value (projected via <ng-content>, or the `value` input) sits inside.
 *
 * Gradient pulls from --accent-a/--accent-b by default (the page contract), or the caller can
 * pass explicit `from`/`to` stops to reuse a named domain hue without re-theming the host.
 * Goal-met (frac >= 1) optionally flips to --signal; over (frac passed as >1 via overflow) is
 * the caller's concern. Honors reduced-motion (transition drops). Dependency-free.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-ring
 *   inputs:    value (number 0..1, required — the fill fraction, clamped),
 *              size (number px, default 64), stroke (number px, default 8),
 *              from (string CSS color, default 'var(--accent-a)'), to (string, default 'var(--accent-b)'),
 *              track (string, default 'var(--hairline)'), signalOnFull (boolean, default false — flip to --signal at 100%),
 *              label (string, aria text equivalent for the ring)
 *   content:   optional center content via <ng-content> (e.g. a numeral)
 *
 * Usage: `<app-bs-ring [value]="0.72" [size]="72" label="72% of goal"><b>72%</b></app-bs-ring>`
 */
@Component({
  selector: 'app-bs-ring',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg class="ring-svg" [attr.viewBox]="'0 0 ' + BOX + ' ' + BOX"
         [attr.width]="size()" [attr.height]="size()"
         role="img" [attr.aria-label]="label() || null">
      <defs>
        <linearGradient [attr.id]="gradId" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" [attr.stop-color]="from()" />
          <stop offset="1" [attr.stop-color]="to()" />
        </linearGradient>
      </defs>
      <g [attr.transform]="'rotate(-90 ' + CTR + ' ' + CTR + ')'">
        <circle class="ring-track" [attr.cx]="CTR" [attr.cy]="CTR" [attr.r]="radius()"
                [attr.stroke]="track()" [attr.stroke-width]="stroke()" fill="none" />
        <circle class="ring-arc" [attr.cx]="CTR" [attr.cy]="CTR" [attr.r]="radius()"
                [attr.stroke]="full() && signalOnFull() ? 'var(--signal)' : 'url(#' + gradId + ')'"
                [attr.stroke-width]="stroke()" fill="none" stroke-linecap="round"
                [attr.stroke-dasharray]="circ()"
                [attr.stroke-dashoffset]="offset()" />
      </g>
    </svg>
    <div class="ring-center" aria-hidden="true"><ng-content></ng-content></div>
  `,
  styles: [`
    :host { display: inline-grid; place-items: center; position: relative; }
    .ring-svg { display: block; overflow: visible; }
    .ring-arc { transition: stroke-dashoffset 700ms var(--ease-spring); }
    .ring-center {
      position: absolute; inset: 0; display: grid; place-items: center;
      font-family: var(--font-display); font-variant-numeric: tabular-nums;
      color: var(--ink); line-height: 1; text-align: center;
    }
    @media (prefers-reduced-motion: reduce) { .ring-arc { transition: none; } }
  `],
})
export class BetaSvgRing {
  /** Fill fraction 0..1 (clamped). */
  readonly value = input.required<number>();
  /** Outer diameter in px. */
  readonly size = input<number>(64);
  /** Stroke width in px (within the 100-box; scaled by viewBox). */
  readonly stroke = input<number>(8);
  /** Gradient start stop. */
  readonly from = input<string>('var(--accent-a)');
  /** Gradient end stop. */
  readonly to = input<string>('var(--accent-b)');
  /** Track (unfilled) color. */
  readonly track = input<string>('var(--hairline)');
  /** Flip the arc to --signal at 100%. */
  readonly signalOnFull = input<boolean>(false);
  /** aria text equivalent for the ring. */
  readonly label = input<string>('');

  protected readonly BOX = 100;
  protected readonly CTR = 50;
  /** Unique gradient id so multiple rings on a page don't collide. */
  protected readonly gradId = `bs-ring-${Math.random().toString(36).slice(2, 9)}`;

  protected readonly radius = computed(() => 50 - this.stroke() / 2 - 1);
  protected readonly circ = computed(() => 2 * Math.PI * this.radius());
  protected readonly frac = computed(() => Math.max(0, Math.min(1, this.value())));
  protected readonly full = computed(() => this.frac() >= 1);
  protected readonly offset = computed(() => this.circ() * (1 - this.frac()));
}
