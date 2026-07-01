import {
  ChangeDetectionStrategy, Component, computed, input,
} from '@angular/core';

/** One slice of the donut. `value` is a raw amount (not a %); the component normalizes. */
export interface DonutSegment {
  /** Slice label (legend + aria). */
  label: string;
  /** Raw amount; the ring normalizes across the sum of all segments. */
  value: number;
  /** Slice color. Any CSS color (hex, var(--accent-a), etc.). */
  color: string;
}

/**
 * BETA-KIT Donut — a segmented ring chart drawn in pure SVG (no chart lib) with a centered headline
 * number + caption overlaid dead-center. A new beta-kit primitive. Segments are supplied as
 * [{label,value,color}] and normalized across their sum; each is a rounded-cap arc laid end-to-end
 * around the circle starting at 12 o'clock, separated by a small gap. The center overlays a big
 * `headline` (e.g. a total) over a muted `caption`. An optional legend row lists each segment with a
 * color dot, label, and its value/percent.
 *
 * Distinct from {@link BetaSvgRing} (a single-value progress arc) and {@link BetaGauge} (a labelled
 * value/max meter): the donut shows a COMPOSITION — how a whole splits across categories. Colors are
 * caller-supplied per segment (commonly the kit domain hues / --accent-*). Reduced-motion drops the
 * grow transition (also enforced by the page killswitch). Dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-donut
 *   inputs:    segments (DonutSegment[], required — the composition; zero/empty => empty track),
 *              headline (string, default '' — the big centered number/text),
 *              caption (string, default '' — the muted line under the headline),
 *              size (number px, default 180 — the donut diameter),
 *              stroke (number px, default 16 — ring thickness in the 100-viewBox),
 *              gap (number, default 2 — degrees of gap between slices),
 *              track (string, default 'var(--hairline)' — the empty-ring color),
 *              legend (boolean, default false — render the legend row beneath),
 *              showPercent (boolean, default true — legend shows % rather than raw value),
 *              label (string, default '' — aria-label override; auto-derived when empty)
 *
 * Usage:
 *   <app-bs-donut [segments]="macros" headline="1,240" caption="kcal" [legend]="true" />
 *   // macros = [{label:'Protein',value:120,color:'#22d3ee'}, …]
 */
@Component({
  selector: 'app-bs-donut',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'bs-donut' },
  template: `
    <div class="dn-chart" [style.width.px]="size()" [style.height.px]="size()">
      <svg class="dn-svg" [attr.viewBox]="'0 0 ' + BOX + ' ' + BOX"
           [attr.width]="size()" [attr.height]="size()"
           role="img" [attr.aria-label]="ariaLabel()">
        <g [attr.transform]="'rotate(-90 50 50)'">
          <circle class="dn-track" cx="50" cy="50" [attr.r]="radius()"
                  [attr.stroke]="track()" [attr.stroke-width]="stroke()" fill="none" />
          @for (a of arcs(); track $index) {
            <circle class="dn-arc" cx="50" cy="50" [attr.r]="radius()"
                    [attr.stroke]="a.color" [attr.stroke-width]="stroke()" fill="none"
                    stroke-linecap="round"
                    [attr.stroke-dasharray]="a.dash"
                    [attr.stroke-dashoffset]="a.offset" />
          }
        </g>
      </svg>
      @if (headline() || caption()) {
        <div class="dn-center" aria-hidden="true">
          @if (headline()) { <div class="dn-headline">{{ headline() }}</div> }
          @if (caption()) { <div class="dn-caption">{{ caption() }}</div> }
        </div>
      }
    </div>
    @if (legend()) {
      <ul class="dn-legend">
        @for (s of segments(); track $index) {
          <li class="dn-leg-item">
            <span class="dn-dot" [style.background]="s.color" aria-hidden="true"></span>
            <span class="dn-leg-label">{{ s.label }}</span>
            <span class="dn-leg-val">{{ legendValue($index) }}</span>
          </li>
        }
      </ul>
    }
  `,
  styles: [`
    :host(.bs-donut) { display: inline-flex; flex-direction: column; align-items: center; gap: 12px; }
    .dn-chart { position: relative; display: inline-grid; place-items: center; }
    .dn-svg { display: block; overflow: visible; }
    .dn-arc { transition: stroke-dasharray 700ms var(--ease-spring); }
    .dn-center {
      position: absolute; inset: 0; display: flex; flex-direction: column;
      align-items: center; justify-content: center; gap: 1px; text-align: center; padding: 0 18%;
    }
    .dn-headline {
      font-family: var(--font-display); font-variant-numeric: tabular-nums;
      font-size: 30px; font-weight: 600; letter-spacing: -.03em; line-height: 1; color: var(--ink);
    }
    .dn-caption {
      font-family: var(--font-ui); font-size: 12px; font-weight: 700;
      letter-spacing: .04em; text-transform: uppercase; color: var(--ink-dim);
    }
    .dn-legend {
      list-style: none; margin: 0; padding: 0; width: 100%;
      display: flex; flex-direction: column; gap: 6px;
    }
    .dn-leg-item {
      display: flex; align-items: center; gap: 8px;
      font-family: var(--font-ui); font-size: 13px; color: var(--ink);
    }
    .dn-dot { flex: 0 0 auto; width: 10px; height: 10px; border-radius: var(--r-pill); }
    .dn-leg-label { flex: 1 1 auto; min-width: 0; font-weight: 600;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .dn-leg-val { flex: 0 0 auto; font-weight: 700; font-variant-numeric: tabular-nums; color: var(--ink-dim); }
    @media (prefers-reduced-motion: reduce) { .dn-arc { transition: none; } }
  `],
})
export class BetaDonut {
  /** The composition; each segment's value is a raw amount, normalized across the sum. */
  readonly segments = input.required<DonutSegment[]>();
  /** The big centered headline (e.g. a total). */
  readonly headline = input<string>('');
  /** The muted caption under the headline. */
  readonly caption = input<string>('');
  /** Donut diameter in px. */
  readonly size = input<number>(180);
  /** Ring thickness within the 100-unit viewBox. */
  readonly stroke = input<number>(16);
  /** Degrees of gap between adjacent slices. */
  readonly gap = input<number>(2);
  /** Empty-ring (track) color. */
  readonly track = input<string>('var(--hairline)');
  /** Render the legend row beneath the ring. */
  readonly legend = input<boolean>(false);
  /** Legend shows % of total rather than the raw value. */
  readonly showPercent = input<boolean>(true);
  /** aria-label override; auto-derived when empty. */
  readonly label = input<string>('');

  protected readonly BOX = 100;

  protected readonly radius = computed(() => 50 - this.stroke() / 2 - 1);
  private readonly circ = computed(() => 2 * Math.PI * this.radius());
  private readonly total = computed(() =>
    this.segments().reduce((s, x) => s + Math.max(0, x.value), 0));

  /**
   * Per-slice dash geometry. Each arc is a full-circumference circle whose dasharray reveals only
   * its own slice (a dash of `sliceLen - gapLen`, then a gap to the ring's end); the dashoffset
   * rotates the start to the running cumulative angle. Gaps are trimmed from each slice so
   * rounded caps sit inside the segment.
   */
  protected readonly arcs = computed(() => {
    const segs = this.segments();
    const total = this.total();
    const c = this.circ();
    if (total <= 0) return [] as { color: string; dash: string; offset: number }[];
    const gapLen = (this.gap() / 360) * c;
    let acc = 0; // cumulative circumference consumed
    const out: { color: string; dash: string; offset: number }[] = [];
    for (const s of segs) {
      const v = Math.max(0, s.value);
      if (v <= 0) { continue; }
      const sliceLen = (v / total) * c;
      const drawn = Math.max(0, sliceLen - gapLen);
      out.push({
        color: s.color,
        dash: `${drawn} ${c - drawn}`,
        // negative offset advances the dash start clockwise to the slice's position.
        offset: -acc,
      });
      acc += sliceLen;
    }
    return out;
  });

  protected legendValue(i: number): string {
    const s = this.segments()[i];
    if (!s) return '';
    if (this.showPercent()) {
      const t = this.total();
      const pct = t > 0 ? Math.round((Math.max(0, s.value) / t) * 100) : 0;
      return `${pct}%`;
    }
    return s.value.toLocaleString('en-US');
  }

  protected readonly ariaLabel = computed(() => {
    if (this.label()) return this.label();
    const parts = this.segments()
      .filter(s => s.value > 0)
      .map(s => `${s.label} ${this.legendPart(s)}`);
    const head = [this.headline(), this.caption()].filter(Boolean).join(' ');
    return [head, parts.join(', ')].filter(Boolean).join('. ');
  });

  private legendPart(s: DonutSegment): string {
    const t = this.total();
    const pct = t > 0 ? Math.round((Math.max(0, s.value) / t) * 100) : 0;
    return this.showPercent() ? `${pct}%` : s.value.toLocaleString('en-US');
  }
}
