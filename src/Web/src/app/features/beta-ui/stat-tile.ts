import {
  ChangeDetectionStrategy, Component, computed, input, output, signal,
} from '@angular/core';
import { BetaSvgRing } from './svg-ring';

/**
 * BETA-KIT StatTile — a metric tile: a big Clash Display numeral with a unit, a label, an optional
 * trend delta (▲/▼ tinted --signal / --warn), and an optional mini accent SvgRing for a goal
 * fraction. A new beta-kit primitive (the "wow per screen" glance unit). Sediment surface
 * (--bg-rise + --lift-1 + --r-tile); when `tappable` it press-sinks and emits `action`. The ring +
 * accents read --accent-a/--accent-b (or a per-tile override). Honors reduced-motion. Composes
 * {@link BetaSvgRing}; otherwise dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-stat-tile
 *   inputs:    value (string|number, required — the big numeral), unit (string, default ''),
 *              label (string, required — the caption), delta (number|null, default null — signed trend; sign drives ▲/▼ + color),
 *              deltaSuffix (string, default '' — e.g. '%' appended to the delta),
 *              ringValue (number|null 0..1, default null — when set, shows a mini ring at this fraction),
 *              accentA (string, default 'var(--accent-a)'), accentB (string, default 'var(--accent-b)'),
 *              tappable (boolean, default false), goodWhenUp (boolean, default true — whether a positive delta is "good"/green)
 *   outputs:   action (void) — fired on tap when tappable
 *
 * Usage: `<app-bs-stat-tile [value]="1240" unit="kcal" label="Today" [delta]="-8" deltaSuffix="%" [ringValue]="0.62" tappable (action)="open()" />`
 */
@Component({
  selector: 'app-bs-stat-tile',
  standalone: true,
  imports: [BetaSvgRing],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="st-tile" [class.st-tappable]="tappable()" [class.sink]="pressed()"
         [attr.role]="tappable() ? 'button' : null"
         [attr.tabindex]="tappable() ? 0 : null"
         [attr.aria-label]="ariaLabel()"
         [style.--ta]="accentA()" [style.--tb]="accentB()"
         (pointerdown)="onDown()" (pointerup)="onUp()" (pointercancel)="onCancel()" (pointerleave)="onCancel()"
         (click)="onClick()" (keydown.enter)="onClick()" (keydown.space)="onClick(); $event.preventDefault()">
      <div class="st-main">
        <div class="st-num">
          <span class="st-val">{{ value() }}</span>
          @if (unit()) { <span class="st-unit">{{ unit() }}</span> }
        </div>
        <div class="st-label">{{ label() }}</div>
        @if (delta() !== null) {
          <div class="st-delta" [class.is-good]="deltaGood()" [class.is-bad]="!deltaGood()">
            <span class="st-arrow" aria-hidden="true">{{ delta()! >= 0 ? '▲' : '▼' }}</span>
            <span>{{ absDelta() }}{{ deltaSuffix() }}</span>
          </div>
        }
      </div>
      @if (ringValue() !== null) {
        <app-bs-ring class="st-ring" [value]="ringValue()!" [size]="46" [stroke]="7"
                     [from]="accentA()" [to]="accentB()" />
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .st-tile {
      display: flex; align-items: center; gap: 12px;
      padding: 14px 16px; border-radius: var(--r-tile);
      background: var(--bg-rise); box-shadow: var(--lift-1);
      border: 1px solid var(--hairline);
    }
    .st-tappable {
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      transition: transform 120ms var(--ease-out), box-shadow 120ms var(--ease-out);
    }
    .st-tappable.sink { transform: scale(.98) translateY(1px); box-shadow: var(--press); }
    .st-tappable:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .st-main { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .st-num { display: flex; align-items: baseline; gap: 4px; }
    .st-val {
      font-family: var(--font-display); font-variant-numeric: tabular-nums;
      font-size: 30px; font-weight: 600; letter-spacing: -.025em; color: var(--ink); line-height: 1;
    }
    .st-unit { font-family: var(--font-ui); font-size: 13px; font-weight: 700; color: var(--ink-dim); }
    .st-label {
      font-family: var(--font-ui); font-size: 12px; font-weight: 600;
      letter-spacing: .03em; text-transform: uppercase; color: var(--ink-dim);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .st-delta {
      display: inline-flex; align-items: center; gap: 3px; margin-top: 2px;
      font-family: var(--font-ui); font-size: 12px; font-weight: 700;
    }
    .st-delta.is-good { color: var(--signal); }
    .st-delta.is-bad { color: var(--warn); }
    .st-arrow { font-size: 10px; }
    .st-ring { flex: 0 0 auto; }
  `],
})
export class BetaStatTile {
  /** The big numeral (string allows pre-formatted "1,240"). */
  readonly value = input.required<string | number>();
  /** Unit suffix shown small beside the numeral. */
  readonly unit = input<string>('');
  /** The caption label. */
  readonly label = input.required<string>();
  /** Signed trend value; null hides the delta. Sign drives the ▲/▼ + color. */
  readonly delta = input<number | null>(null);
  /** Suffix appended to the delta (e.g. '%'). */
  readonly deltaSuffix = input<string>('');
  /** 0..1 fraction; when set, renders a mini ring. Null hides it. */
  readonly ringValue = input<number | null>(null);
  /** Ring/accent start stop (override to carry a per-tile hue). */
  readonly accentA = input<string>('var(--accent-a)');
  /** Ring/accent end stop. */
  readonly accentB = input<string>('var(--accent-b)');
  /** Make the tile a tappable button. */
  readonly tappable = input<boolean>(false);
  /** Whether a positive delta is "good" (green). Set false for metrics where down is good (e.g. weight). */
  readonly goodWhenUp = input<boolean>(true);
  /** Fired on tap when tappable. */
  readonly action = output<void>();

  protected readonly pressed = signal(false);

  protected readonly absDelta = computed(() => Math.abs(this.delta() ?? 0));
  /** A delta is "good" (green) when its sign matches goodWhenUp. */
  protected readonly deltaGood = computed(() => {
    const d = this.delta() ?? 0;
    if (d === 0) return true;
    return (d > 0) === this.goodWhenUp();
  });
  protected readonly ariaLabel = computed(() => {
    const parts = [`${this.value()}${this.unit() ? ' ' + this.unit() : ''}`, this.label()];
    const d = this.delta();
    if (d !== null) parts.push(`${d >= 0 ? 'up' : 'down'} ${Math.abs(d)}${this.deltaSuffix()}`);
    return parts.join(', ');
  });

  protected onDown(): void { if (this.tappable()) this.pressed.set(true); }
  protected onUp(): void { this.pressed.set(false); }
  protected onCancel(): void { this.pressed.set(false); }
  protected onClick(): void { if (this.tappable()) this.action.emit(); }
}
