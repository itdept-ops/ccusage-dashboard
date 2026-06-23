import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

/** The four lifecycle phases every Atrium widget renders. `hidden` is handled by the parent (the card is
 *  simply not projected), so a widget body only ever sees the other three. */
export type WidgetPhase = 'loading' | 'ready' | 'empty' | 'failed';

/**
 * The shared chrome for an Atrium widget card: a tap-through surface (deep-links to the live page via
 * `route`), a header (accent dot + title + optional trailing slot), and the standard skeleton / empty /
 * failed scaffolding. Each widget projects its own READY body into `[body]`; the shell owns the rest so
 * every card states are visually consistent and one widget's failure never escapes its own card.
 *
 * Pure presentational + isolated: uses only inherited `--atr-*` tokens (no global `--tech-*`, no live
 * imports). The accent color is passed as a token name so each domain keeps its hue.
 */
@Component({
  selector: 'atr-widget-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule],
  template: `
    <section class="w" [class.w--reorder]="reordering()">
      <header class="w__head">
        <span class="w__dot" [style.background]="'var(' + accentVar() + ')'" aria-hidden="true"></span>
        <span class="w__title">{{ title() }}</span>

        @if (reordering()) {
          <span class="w__reorder">
            <button type="button" class="w__rbtn" (click)="moveUp.emit()" aria-label="Move up">
              <mat-icon aria-hidden="true">keyboard_arrow_up</mat-icon>
            </button>
            <button type="button" class="w__rbtn" (click)="moveDown.emit()" aria-label="Move down">
              <mat-icon aria-hidden="true">keyboard_arrow_down</mat-icon>
            </button>
            <button type="button" class="w__rbtn w__rbtn--off" (click)="hide.emit()" aria-label="Hide widget">
              <mat-icon aria-hidden="true">visibility_off</mat-icon>
            </button>
          </span>
        } @else {
          <ng-content select="[head-trailing]"></ng-content>
          @if (route(); as r) {
            <a class="w__open" [routerLink]="r" [attr.aria-label]="'Open ' + title()">
              <mat-icon aria-hidden="true">chevron_right</mat-icon>
            </a>
          }
        }
      </header>

      @switch (phase()) {
        @case ('loading') {
          <div class="w__skel" aria-hidden="true">
            <span class="w__skel-line"></span>
            <span class="w__skel-line w__skel-line--short"></span>
          </div>
        }
        @case ('failed') {
          <div class="w__state">
            <p class="w__state-msg">Couldn't load.</p>
            <button type="button" class="w__retry" (click)="retry.emit()">
              <mat-icon aria-hidden="true">refresh</mat-icon> Retry
            </button>
          </div>
        }
        @case ('empty') {
          <div class="w__state">
            <p class="w__state-msg">{{ emptyText() }}</p>
          </div>
        }
        @default {
          <ng-content select="[body]"></ng-content>
        }
      }
    </section>
  `,
  styles: [`
    .w {
      display: block;
      border-radius: var(--r-card, 22px);
      background: var(--atr-card);
      border: 1px solid var(--atr-edge);
      box-shadow: var(--lift);
      padding: 16px;
      scroll-snap-align: start;
    }
    .w--reorder { outline: 2px dashed var(--atr-ink-dim); outline-offset: 2px; }

    .w__head { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
    .w__dot { flex: 0 0 auto; width: 9px; height: 9px; border-radius: 999px; }
    .w__title { font-weight: 700; font-size: 14px; letter-spacing: .01em; color: var(--atr-ink); }

    .w__open {
      margin-left: auto; display: grid; place-items: center;
      width: 32px; height: 32px; border-radius: 999px;
      color: var(--atr-ink-dim); text-decoration: none;
      transition: background 120ms ease, color 120ms ease;
    }
    .w__open:hover { background: rgba(255,255,255,.06); color: var(--atr-ink); }

    .w__reorder { margin-left: auto; display: flex; gap: 6px; }
    .w__rbtn {
      display: grid; place-items: center;
      width: 36px; height: 36px; border-radius: 10px;
      border: 1px solid var(--atr-edge); background: transparent; color: var(--atr-ink);
      cursor: pointer;
    }
    .w__rbtn--off { color: var(--atr-spend); }
    .w__rbtn mat-icon { font-size: 20px; width: 20px; height: 20px; }

    .w__skel { display: flex; flex-direction: column; gap: 10px; padding: 4px 0 8px; }
    .w__skel-line {
      height: 16px; border-radius: 8px;
      background: linear-gradient(100deg, rgba(255,255,255,.04) 30%, rgba(255,255,255,.10) 50%, rgba(255,255,255,.04) 70%);
      background-size: 200% 100%;
      animation: atr-shimmer 1.4s ease infinite;
    }
    .w__skel-line--short { width: 55%; }
    @keyframes atr-shimmer { to { background-position: -200% 0; } }

    .w__state { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 6px 0; }
    .w__state-msg { margin: 0; color: var(--atr-ink-dim); font-size: 13px; }
    .w__retry {
      display: inline-flex; align-items: center; gap: 6px;
      min-height: 36px; padding: 0 12px; border-radius: 999px;
      border: 1px solid var(--atr-edge); background: transparent; color: var(--atr-ink);
      font: inherit; font-size: 13px; cursor: pointer;
    }
    .w__retry mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `],
})
export class AtriumWidgetShell {
  /** Card title shown in the header. */
  readonly title = input.required<string>();
  /** Deep-link to the matching LIVE page; null hides the chevron (e.g. presence has no single target). */
  readonly route = input<string | null>(null);
  /** The CSS custom-property NAME for this domain's accent (e.g. `--atr-rings`). */
  readonly accentVar = input<string>('--atr-ink');
  /** Current lifecycle phase. */
  readonly phase = input.required<WidgetPhase>();
  /** Friendly nudge shown in the empty state. */
  readonly emptyText = input<string>('Nothing here yet.');
  /** Whether the parent is in long-press reorder mode (swaps the trailing slot for move/hide controls). */
  readonly reordering = input<boolean>(false);

  readonly retry = output<void>();
  readonly moveUp = output<void>();
  readonly moveDown = output<void>();
  readonly hide = output<void>();
}
