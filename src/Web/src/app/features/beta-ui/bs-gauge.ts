import {
  ChangeDetectionStrategy, Component, computed, input,
} from '@angular/core';

/**
 * BETA-KIT Gauge — a value/max progress gauge drawn as an SVG arc with a big centered value and a
 * sub-label line beneath (e.g. "1,240" / "of 2,000 kcal"). A new beta-kit primitive, DISTINCT from
 * {@link BetaSvgRing}: the ring is a compact full-circle stroke meant for glance tiles with tiny or
 * projected center text; the gauge is a large, LABELLED meter (semicircle by default, optional full
 * circle) whose center IS the readout — a formatted value + unit + caption it renders itself.
 *
 * The arc is accent-driven (a <linearGradient> from --accent-a→--accent-b, matching the flagship),
 * sweeps from the track's start to `value/max`, rounds its cap, and eases its dashoffset with
 * --ease-spring. When value exceeds max the gauge enters an OVER-GOAL state: the fill clamps to full
 * and repaints in --warn (the kit's warm over-goal hue, never red), and the caption is announced as
 * over. Reduced-motion drops the transition (also handled by the page killswitch). Dependency-free.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-gauge
 *   inputs:    value (number, required — the current amount),
 *              max (number, required — the goal/total),
 *              unit (string, default '' — appended to the sub-label, e.g. 'kcal'),
 *              caption (string, default 'of' — the connective before the max, e.g. 'of'/'used of'),
 *              full (boolean, default false — full-circle 360° gauge instead of the 180° semicircle),
 *              size (number px, default 200 — the gauge width),
 *              stroke (number px, default 14 — arc thickness in the 100-viewBox),
 *              from (string, default 'var(--accent-a)'), to (string, default 'var(--accent-b)'),
 *              track (string, default 'var(--hairline)'),
 *              warnOnOver (boolean, default true — repaint the arc --warn past 100%),
 *              format (boolean, default true — thousands-separate the displayed value/max),
 *              label (string, default '' — aria-label override; auto-derived when empty)
 *
 * Usage: `<app-bs-gauge [value]="1240" [max]="2000" unit="kcal" caption="of" />`
 */
@Component({
  selector: 'app-bs-gauge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'bs-gauge' },
  template: `
    <svg class="gg-svg" [attr.viewBox]="'0 0 ' + BOX + ' ' + boxH()"
         [attr.width]="size()" [attr.height]="size() * (boxH() / BOX)"
         role="img" [attr.aria-label]="ariaLabel()">
      <defs>
        <linearGradient [attr.id]="gradId" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" [attr.stop-color]="from()" />
          <stop offset="1" [attr.stop-color]="to()" />
        </linearGradient>
      </defs>
      <g [attr.transform]="rotate()">
        <path class="gg-track" [attr.d]="arcPath()" [attr.stroke]="track()"
              [attr.stroke-width]="stroke()" fill="none" stroke-linecap="round" />
        <path class="gg-arc" [attr.d]="arcPath()"
              [attr.stroke]="over() && warnOnOver() ? 'var(--warn)' : 'url(#' + gradId + ')'"
              [attr.stroke-width]="stroke()" fill="none" stroke-linecap="round"
              [attr.stroke-dasharray]="len()"
              [attr.stroke-dashoffset]="offset()" />
      </g>
    </svg>
    <div class="gg-center" [class.is-full]="full()" aria-hidden="true">
      <div class="gg-value">{{ shownValue() }}</div>
      <div class="gg-sub">{{ caption() }} {{ shownMax() }}{{ unit() ? ' ' + unit() : '' }}</div>
      @if (over()) { <div class="gg-over">Over goal</div> }
    </div>
  `,
  styles: [`
    :host(.bs-gauge) {
      display: inline-grid; place-items: center; position: relative;
    }
    .gg-svg { display: block; overflow: visible; }
    .gg-track { opacity: .9; }
    .gg-arc { transition: stroke-dashoffset 800ms var(--ease-spring); }
    /* Center readout: for the semicircle it hugs the lower-center (inside the open arc); for the
       full circle it is dead-center. */
    .gg-center {
      position: absolute; left: 0; right: 0; bottom: 6%;
      display: flex; flex-direction: column; align-items: center; gap: 2px;
      text-align: center; padding: 0 12%;
    }
    .gg-center.is-full { inset: 0; bottom: 0; justify-content: center; }
    .gg-value {
      font-family: var(--font-display); font-variant-numeric: tabular-nums;
      font-size: 34px; font-weight: 600; letter-spacing: -.03em; line-height: 1; color: var(--ink);
    }
    .gg-sub {
      font-family: var(--font-ui); font-size: 13px; font-weight: 700; color: var(--ink-dim);
      letter-spacing: .01em;
    }
    .gg-over {
      margin-top: 2px; font-family: var(--font-ui); font-size: 11px; font-weight: 800;
      letter-spacing: .06em; text-transform: uppercase; color: var(--warn);
    }
    @media (prefers-reduced-motion: reduce) { .gg-arc { transition: none; } }
  `],
})
export class BetaGauge {
  /** The current amount. */
  readonly value = input.required<number>();
  /** The goal / total. */
  readonly max = input.required<number>();
  /** Unit appended to the sub-label (e.g. 'kcal'). */
  readonly unit = input<string>('');
  /** Connective before the max in the sub-label (e.g. 'of'). */
  readonly caption = input<string>('of');
  /** Full-circle 360° gauge instead of the 180° semicircle. */
  readonly full = input<boolean>(false);
  /** Gauge width in px. */
  readonly size = input<number>(200);
  /** Arc thickness within the 100-unit viewBox. */
  readonly stroke = input<number>(14);
  /** Gradient start stop. */
  readonly from = input<string>('var(--accent-a)');
  /** Gradient end stop. */
  readonly to = input<string>('var(--accent-b)');
  /** Track (unfilled) color. */
  readonly track = input<string>('var(--hairline)');
  /** Repaint the arc --warn past 100%. */
  readonly warnOnOver = input<boolean>(true);
  /** Thousands-separate the displayed value/max. */
  readonly format = input<boolean>(true);
  /** aria-label override; auto-derived when empty. */
  readonly label = input<string>('');

  protected readonly BOX = 100;
  /** Unique gradient id so multiple gauges on a page don't collide. */
  protected readonly gradId = `bs-gauge-${Math.random().toString(36).slice(2, 9)}`;

  /** Radius that keeps the stroke (and its rounded cap) inside the 100-box with a 1u margin. */
  protected readonly radius = computed(() => 50 - this.stroke() / 2 - 1);
  /** viewBox height: full box for a circle, ~half + stroke room for the semicircle. */
  protected readonly boxH = computed(() => (this.full() ? this.BOX : 50 + this.stroke() / 2 + 2));

  /** Fraction 0..1 (clamped for the arc fill). */
  protected readonly frac = computed(() => {
    const m = this.max();
    if (!m || m <= 0) return 0;
    return Math.max(0, Math.min(1, this.value() / m));
  });
  /** Over-goal when the raw value exceeds max. */
  protected readonly over = computed(() => {
    const m = this.max();
    return m > 0 && this.value() > m;
  });

  /** Total arc length in user units: full circumference, or half of it for the semicircle. */
  protected readonly len = computed(() => {
    const c = 2 * Math.PI * this.radius();
    return this.full() ? c : c / 2;
  });
  /** Dashoffset from the arc's start, driven by the fill fraction. */
  protected readonly offset = computed(() => this.len() * (1 - this.frac()));

  /** The arc path: a semicircle (left→right along the top edge) or a full circle. */
  protected readonly arcPath = computed(() => {
    const r = this.radius();
    const cx = 50;
    if (this.full()) {
      const cy = 50;
      // Two half-arcs make a closed circle path that starts/ends at 12 o'clock.
      return `M ${cx} ${cy - r} A ${r} ${r} 0 1 1 ${cx} ${cy + r} A ${r} ${r} 0 1 1 ${cx} ${cy - r}`;
    }
    const cy = 50;
    // Semicircle from the left baseline, over the top, to the right baseline.
    return `M ${cx - r} ${cy} A ${r} ${r} 0 0 1 ${cx + r} ${cy}`;
  });

  /** Rotate the full circle so its start sits at 12 o'clock (the semicircle already opens downward). */
  protected readonly rotate = computed(() =>
    this.full() ? `rotate(-90 50 50)` : `rotate(0 50 50)`);

  private fmt(n: number): string {
    if (!this.format()) return String(n);
    return Math.round(n).toLocaleString('en-US');
  }
  protected readonly shownValue = computed(() => this.fmt(this.value()));
  protected readonly shownMax = computed(() => this.fmt(this.max()));

  protected readonly ariaLabel = computed(() => {
    if (this.label()) return this.label();
    const u = this.unit() ? ' ' + this.unit() : '';
    const base = `${this.shownValue()}${u} ${this.caption()} ${this.shownMax()}${u}`;
    return this.over() ? `${base}, over goal` : base;
  });
}
