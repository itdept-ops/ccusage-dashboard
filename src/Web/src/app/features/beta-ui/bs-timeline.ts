import {
  booleanAttribute, ChangeDetectionStrategy, Component, input,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/** One entry on the timeline. All fields but `title` are optional. */
export interface TimelineItem {
  /** Stable identifier for @for tracking. */
  id: string | number;
  /** The entry headline (required). */
  title: string;
  /** A supporting second line (name, place, category…). */
  subtitle?: string;
  /** Body text rendered below the title/subtitle. */
  content?: string;
  /** Marker glyph (Material ligature). Defaults to a filled dot when omitted. */
  icon?: string;
  /** The date/time stamp: the left-rail label (vertical) or column head (horizontal). */
  stamp?: string;
  /** Optional per-item accent override (any CSS color) for the marker + rail dot. */
  accent?: string;
}

/** One date column in the HORIZONTAL variant — a stamp head + its entries. */
export interface TimelineColumn {
  /** Stable identifier for @for tracking. */
  id: string | number;
  /** The column head (date label). */
  stamp: string;
  /** The time-stamped entries in this column. */
  items: TimelineItem[];
}

/**
 * BETA-KIT Timeline — a chronological history primitive with TWO layouts driven by `mode`.
 *
 * VERTICAL (default): a left rail (connector line) with a marker per row (a per-item icon or a
 * filled accent dot), each row holding a stamp + title + optional subtitle + optional content.
 * Rows come either from the `items` input OR, when `items` is empty, from projected content
 * (drop your own <div> rows in). HORIZONTAL: a swipeable row of date COLUMNS (from the `columns`
 * input), each a stamp head over a stack of time-stamped entries — snap-scrolls on the x-axis.
 *
 * Markers/rail/dots read --accent-a/--accent-b (per-item `accent` overrides); text reads
 * --ink/--ink-dim; the rail + column heads use --hairline; radii/elevation from the kit tokens.
 * DUAL-TOKEN: each token resolves the kit value FIRST with a --tech-* fallback, so the SAME
 * primitive renders on a kit-host page AND on a plain --tech-* desktop vertical with no host setup.
 * The connector line is static (nothing for reduced-motion to suppress); the horizontal scroller
 * uses inertial touch scroll only. Dependency-free (Material icons only) + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-timeline
 *   inputs:    mode ('vertical' | 'horizontal', default 'vertical'),
 *              items (TimelineItem[], default [] — the VERTICAL rows; empty => projected content),
 *              columns (TimelineColumn[], default [] — the HORIZONTAL date columns),
 *              label (string, default '' — aria-label for the list),
 *              compact (boolean via booleanAttribute, default false — tighter row rhythm)
 *   outputs:   (none — read-only presentation)
 *   content:   in VERTICAL mode with empty `items`, projected rows render inside the rail
 *
 * Usage:
 *   <app-bs-timeline [items]="events" label="Activity" />
 *   <app-bs-timeline mode="horizontal" [columns]="weekCols" label="This week" />
 */
@Component({
  selector: 'app-bs-timeline',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (mode() === 'horizontal') {
      <div class="tl-hz" role="list" [attr.aria-label]="label() || null">
        @for (col of columns(); track col.id) {
          <section class="tl-col" role="listitem">
            <header class="tl-col__head">{{ col.stamp }}</header>
            <div class="tl-col__stack">
              @for (it of col.items; track it.id) {
                <article class="tl-entry" [style.--tl-mk]="it.accent || null">
                  <span class="tl-entry__mk" aria-hidden="true">
                    @if (it.icon) { <mat-icon>{{ it.icon }}</mat-icon> }
                  </span>
                  <div class="tl-entry__body">
                    @if (it.stamp) { <span class="tl-entry__stamp">{{ it.stamp }}</span> }
                    <span class="tl-entry__title">{{ it.title }}</span>
                    @if (it.subtitle) { <span class="tl-entry__sub">{{ it.subtitle }}</span> }
                    @if (it.content) { <p class="tl-entry__content">{{ it.content }}</p> }
                  </div>
                </article>
              }
            </div>
          </section>
        }
      </div>
    } @else {
      <ol class="tl-vt" [class.compact]="compact()" [attr.aria-label]="label() || null">
        @if (items().length) {
          @for (it of items(); track it.id) {
            <li class="tl-row" [style.--tl-mk]="it.accent || null">
              <span class="tl-row__marker" aria-hidden="true">
                @if (it.icon) { <mat-icon>{{ it.icon }}</mat-icon> } @else { <span class="tl-row__dot"></span> }
              </span>
              <div class="tl-row__body">
                @if (it.stamp) { <span class="tl-row__stamp">{{ it.stamp }}</span> }
                <span class="tl-row__title">{{ it.title }}</span>
                @if (it.subtitle) { <span class="tl-row__sub">{{ it.subtitle }}</span> }
                @if (it.content) { <p class="tl-row__content">{{ it.content }}</p> }
              </div>
            </li>
          }
        } @else {
          <li class="tl-row tl-row--projected">
            <span class="tl-row__marker" aria-hidden="true"><span class="tl-row__dot"></span></span>
            <div class="tl-row__body"><ng-content /></div>
          </li>
        }
      </ol>
    }
  `,
  styles: [`
    /* Resolve each token once with a --tech-* fallback so the timeline works on kit + tech pages alike. */
    :host {
      display: block;
      --tl-accent-a: var(--accent-a, var(--tech-accent, #7c8cff));
      --tl-accent-b: var(--accent-b, var(--tech-accent-2, var(--tech-accent, #3b82f6)));
      --tl-ink: var(--ink, var(--tech-text, #e6edf6));
      --tl-ink-dim: var(--ink-dim, var(--tech-text-secondary, #9ba9bd));
      --tl-ink-faint: var(--ink-faint, var(--tech-text-tertiary, #6d7194));
      --tl-edge: var(--hairline, var(--tech-border-subtle, rgba(255,255,255,.09)));
      --tl-surface: var(--bg-rise, var(--tech-panel, #11161f));
      --tl-r-tile: var(--r-tile, var(--tech-r-control, 14px));
      --tl-shadow-1: var(--lift-1, 0 2px 10px rgba(0,0,0,.12));
      --tl-font-ui: var(--font-ui, inherit);
      /* per-item marker paint; falls back to the accent gradient stop-a */
      --tl-mk: var(--tl-accent-a);
    }

    /* ── VERTICAL ─────────────────────────────────────────────────────────── */
    .tl-vt { list-style: none; margin: 0; padding: 0; }
    .tl-row {
      position: relative; display: grid;
      grid-template-columns: 28px 1fr; gap: 12px;
      padding: 0 0 18px;
    }
    .tl-vt.compact .tl-row { padding-bottom: 12px; gap: 10px; }
    /* the connector line runs down the marker column, stopping at the last row */
    .tl-row::before {
      content: ''; position: absolute; left: 13px; top: 22px; bottom: -2px;
      width: 2px; background: var(--tl-edge);
    }
    .tl-row:last-child::before { display: none; }
    .tl-row__marker {
      position: relative; z-index: 1; grid-column: 1;
      width: 28px; height: 28px; display: grid; place-items: center;
      border-radius: 50%;
      color: var(--tl-mk);
      background: color-mix(in srgb, var(--tl-mk) 18%, transparent);
    }
    .tl-row__marker mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .tl-row__dot {
      width: 10px; height: 10px; border-radius: 50%;
      background: linear-gradient(135deg, var(--tl-mk), var(--tl-accent-b));
    }
    .tl-row__body { grid-column: 2; min-width: 0; display: flex; flex-direction: column; gap: 2px; padding-top: 3px; }
    .tl-row__stamp {
      font-family: var(--tl-font-ui); font-size: 11px; font-weight: 700;
      letter-spacing: .04em; text-transform: uppercase; color: var(--tl-ink-faint);
    }
    .tl-row__title { font-family: var(--tl-font-ui); font-size: 15px; font-weight: 700; color: var(--tl-ink); }
    .tl-row__sub { font-family: var(--tl-font-ui); font-size: 13px; font-weight: 600; color: var(--tl-ink-dim); }
    .tl-row__content { margin: 3px 0 0; font-size: 13px; line-height: 1.5; color: var(--tl-ink-dim); }

    /* ── HORIZONTAL ───────────────────────────────────────────────────────── */
    .tl-hz {
      display: flex; gap: 12px;
      overflow-x: auto; overflow-y: hidden;
      scroll-snap-type: x proximity;
      -webkit-overflow-scrolling: touch;
      overscroll-behavior-x: contain;
      padding-bottom: 6px;
      touch-action: pan-x;
    }
    .tl-col {
      flex: 0 0 auto; width: min(72vw, 240px);
      scroll-snap-align: start;
      display: flex; flex-direction: column; gap: 10px;
    }
    .tl-col__head {
      position: sticky; top: 0;
      font-family: var(--tl-font-ui); font-size: 12px; font-weight: 800;
      letter-spacing: .03em; text-transform: uppercase; color: var(--tl-ink);
      padding-bottom: 6px; border-bottom: 2px solid var(--tl-edge);
    }
    .tl-col__stack { display: flex; flex-direction: column; gap: 8px; }
    .tl-entry {
      display: grid; grid-template-columns: 24px 1fr; gap: 10px;
      padding: 10px 12px; border-radius: var(--tl-r-tile);
      background: var(--tl-surface); box-shadow: var(--tl-shadow-1);
      border: 1px solid var(--tl-edge);
    }
    .tl-entry__mk {
      width: 24px; height: 24px; display: grid; place-items: center;
      border-radius: 50%; color: var(--tl-mk);
      background: color-mix(in srgb, var(--tl-mk) 18%, transparent);
    }
    .tl-entry__mk mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .tl-entry__body { min-width: 0; display: flex; flex-direction: column; gap: 1px; }
    .tl-entry__stamp {
      font-family: var(--tl-font-ui); font-size: 10px; font-weight: 700;
      letter-spacing: .04em; text-transform: uppercase; color: var(--tl-ink-faint);
    }
    .tl-entry__title { font-family: var(--tl-font-ui); font-size: 14px; font-weight: 700; color: var(--tl-ink); }
    .tl-entry__sub { font-family: var(--tl-font-ui); font-size: 12px; font-weight: 600; color: var(--tl-ink-dim); }
    .tl-entry__content { margin: 2px 0 0; font-size: 12px; line-height: 1.45; color: var(--tl-ink-dim); }
  `],
})
export class BetaTimeline {
  /** Layout: a vertical rail (default) or a swipeable row of date columns. */
  readonly mode = input<'vertical' | 'horizontal'>('vertical');
  /** The VERTICAL rows. When empty, projected content renders inside the rail instead. */
  readonly items = input<TimelineItem[]>([]);
  /** The HORIZONTAL date columns (used only when mode is 'horizontal'). */
  readonly columns = input<TimelineColumn[]>([]);
  /** aria-label for the list/column group. */
  readonly label = input<string>('');
  /** Tighter row rhythm for dense histories (accepts the bare `compact` attribute). */
  readonly compact = input<boolean, unknown>(false, { transform: booleanAttribute });
}
