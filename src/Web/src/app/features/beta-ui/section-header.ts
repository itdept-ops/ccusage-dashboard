import {
  ChangeDetectionStrategy, Component, input, output,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

/**
 * BETA-KIT SectionHeader — a card/section title row: an accent dot (the page gradient), a title +
 * optional subtitle, and an optional trailing CTA (either a chevron-style "more" affordance or a
 * labelled action). A new beta-kit primitive. When `cta` is set OR `chevron` is true the whole
 * header is a button that emits `action`; otherwise it's static heading text. The accent dot reads
 * --accent-a/--accent-b (or an override pair) so a header can carry a per-domain hue inside an
 * otherwise-themed page. Honors reduced-motion. Dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-section-header
 *   inputs:    title (string, required), subtitle (string, default ''),
 *              cta (string, default '' — trailing action label; sets the row interactive),
 *              chevron (boolean, default false — show a trailing › and make the row interactive),
 *              accentA (string, default 'var(--accent-a)'), accentB (string, default 'var(--accent-b)'),
 *              icon (string Material ligature, default '' — replaces the dot with an accent-tinted glyph)
 *   outputs:   action (void) — fired on tap when interactive (cta or chevron)
 *
 * Usage: `<app-bs-section-header title="Today" subtitle="3 logged" chevron (action)="openAll()" />`
 */
@Component({
  selector: 'app-bs-section-header',
  standalone: true,
  imports: [MatIconModule, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (interactive()) {
      <button type="button" class="sh-row sh-interactive" (click)="action.emit()"
              [style.--sa]="accentA()" [style.--sb]="accentB()">
        <ng-container [ngTemplateOutlet]="body"></ng-container>
        <span class="sh-cta">
          @if (cta()) { <span class="sh-cta-label">{{ cta() }}</span> }
          <span class="sh-chev" aria-hidden="true">›</span>
        </span>
      </button>
    } @else {
      <div class="sh-row" [style.--sa]="accentA()" [style.--sb]="accentB()">
        <ng-container [ngTemplateOutlet]="body"></ng-container>
      </div>
    }

    <ng-template #body>
      @if (icon()) {
        <mat-icon class="sh-icon" aria-hidden="true">{{ icon() }}</mat-icon>
      } @else {
        <span class="sh-dot" aria-hidden="true"></span>
      }
      <span class="sh-titles">
        <span class="sh-title">{{ title() }}</span>
        @if (subtitle()) { <span class="sh-sub">{{ subtitle() }}</span> }
      </span>
    </ng-template>
  `,
  styles: [`
    :host { display: block; }
    .sh-row {
      display: flex; align-items: center; gap: 10px; width: 100%;
      padding: 0; background: transparent; border: none; text-align: left;
      color: var(--ink); font-family: var(--font-ui);
    }
    .sh-interactive {
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      transition: opacity 120ms var(--ease-out);
    }
    .sh-interactive:active { opacity: .7; }
    .sh-interactive:focus-visible { outline: 2px solid var(--focus); outline-offset: 3px; border-radius: 8px; }
    .sh-dot {
      flex: 0 0 auto; width: 10px; height: 10px; border-radius: 50%;
      background: linear-gradient(135deg, var(--sa, var(--accent-a)), var(--sb, var(--accent-b)));
      box-shadow: 0 0 0 4px color-mix(in srgb, var(--sa, var(--accent-a)) 18%, transparent);
    }
    .sh-icon {
      flex: 0 0 auto; width: 20px; height: 20px; font-size: 20px; line-height: 20px;
      color: var(--ink-dim);
    }
    @supports ((-webkit-background-clip: text) or (background-clip: text)) {
      .sh-icon {
        background: linear-gradient(135deg, var(--sa, var(--accent-a)), var(--sb, var(--accent-b)));
        -webkit-background-clip: text; background-clip: text; -webkit-text-fill-color: transparent;
      }
    }
    .sh-titles { display: flex; flex-direction: column; gap: 1px; min-width: 0; flex: 1 1 auto; }
    .sh-title {
      font-size: 16px; font-weight: 700; letter-spacing: -.01em; color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .sh-sub { font-size: 12px; font-weight: 600; color: var(--ink-dim); }
    .sh-cta {
      flex: 0 0 auto; display: inline-flex; align-items: center; gap: 4px;
      color: var(--ink-dim); font-size: 13px; font-weight: 700;
    }
    .sh-cta-label { color: var(--accent-a); }
    .sh-chev { font-size: 20px; line-height: 1; color: var(--ink-faint); }
  `],
})
export class BetaSectionHeader {
  /** The section title. */
  readonly title = input.required<string>();
  /** Optional secondary line. */
  readonly subtitle = input<string>('');
  /** Trailing action label (presence makes the row interactive). */
  readonly cta = input<string>('');
  /** Show a trailing chevron + make the row interactive. */
  readonly chevron = input<boolean>(false);
  /** Accent dot/icon start stop (override to carry a per-domain hue). */
  readonly accentA = input<string>('var(--accent-a)');
  /** Accent dot/icon end stop. */
  readonly accentB = input<string>('var(--accent-b)');
  /** Material ligature; when set, replaces the dot with an accent-tinted glyph. */
  readonly icon = input<string>('');
  /** Fired on tap when interactive. */
  readonly action = output<void>();

  /** True when the header should render as a tappable button. */
  protected interactive(): boolean { return !!this.cta() || this.chevron(); }
}
