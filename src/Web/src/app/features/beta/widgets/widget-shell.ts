import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { BetaSkeleton } from '../../beta-ui';

/** The four lifecycle phases every Atrium widget renders. `hidden` is handled by the parent (the card is
 *  simply not projected), so a widget body only ever sees the other three. */
export type WidgetPhase = 'loading' | 'ready' | 'empty' | 'failed';

/**
 * STRATA widget card — the shared chrome for every Atrium HOME widget, rebuilt on the shared beta-ui
 * foundation (`@use '../../beta-ui/beta-kit'`). It is a DEPTH surface, not a flat card: a sediment
 * `--bg-rise` extrusion lifted with `--lift-2`, a gradient hairline edge that picks up the per-domain
 * accent, an accent glow that blooms on press, a spring entrance, and tap/press feedback. Each domain
 * passes a `from`/`to` accent pair so its card carries its own hue inside HOME's violet→blue page accent.
 *
 * Phases are unchanged: a `BetaSkeleton`-driven loading state, a tasteful empty state (an accent-tinted
 * glyph + the nudge — never a blank box), and a failed/retry state. Each widget projects its READY body
 * into `[body]`; the shell owns the rest so one widget's failure never escapes its own card.
 *
 * Pure presentational + isolated: inherits the kit token contract from the page `:host` (no global
 * `--tech-*`, no live imports). The accent is passed as a from/to pair (gradient stops), so the dot,
 * edge, and glow all render the domain's hue as a real gradient — never flat.
 */
@Component({
  selector: 'atr-widget-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule, BetaSkeleton],
  template: `
    <section class="w" [class.w--reorder]="reordering()" [class.w--press]="pressable()"
             [style.--wa]="accentA()" [style.--wb]="accentB()" (click)="onCardTap()">
      <span class="w__edge" aria-hidden="true"></span>
      <header class="w__head">
        <span class="w__dot" aria-hidden="true"></span>
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
            <a class="w__open" [routerLink]="r" [attr.aria-label]="'Open ' + title()"
               (click)="$event.stopPropagation()">
              <mat-icon aria-hidden="true">chevron_right</mat-icon>
            </a>
          }
        }
      </header>

      @switch (phase()) {
        @case ('loading') {
          <div class="w__skel">
            <app-bs-skeleton height="22px" width="60%" radius="10px" />
            <app-bs-skeleton height="14px" width="85%" radius="8px" />
          </div>
        }
        @case ('failed') {
          <div class="w__state w__state--fail">
            <span class="w__state-ic" aria-hidden="true"><mat-icon>cloud_off</mat-icon></span>
            <p class="w__state-msg">Couldn't load.</p>
            <button type="button" class="w__retry" (click)="retry.emit()">
              <mat-icon aria-hidden="true">refresh</mat-icon> Retry
            </button>
          </div>
        }
        @case ('empty') {
          <div class="w__state w__state--empty">
            <span class="w__state-ic" aria-hidden="true"><mat-icon>{{ emptyIcon() }}</mat-icon></span>
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
    @use '../../beta-ui/beta-kit' as kit;

    .w {
      position: relative;
      display: block;
      border-radius: var(--r-card);
      background:
        radial-gradient(120% 90% at 0% 0%, color-mix(in srgb, var(--wa, var(--accent-a)) 9%, transparent), transparent 60%),
        var(--bg-rise);
      box-shadow: var(--lift-2);
      padding: 16px;
      scroll-snap-align: start;
      overflow: hidden;
      isolation: isolate;
      transition: transform 140ms var(--ease-out), box-shadow 220ms var(--ease-out);
    }
    // Gradient hairline edge that picks up the domain accent (top-lit), masked to a 1px ring.
    // Lit EVENLY along the top edge and fading to the plain hairline below — a vertical (180deg)
    // ramp keeps the two top corners symmetric so neither rounded corner flares into a bright seam.
    .w__edge {
      position: absolute; inset: 0; border-radius: inherit; padding: 1px; pointer-events: none; z-index: 0;
      background: linear-gradient(180deg,
        color-mix(in srgb, var(--wa, var(--accent-a)) 26%, var(--hairline)),
        var(--hairline) 48%);
      -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
      -webkit-mask-composite: xor; mask-composite: exclude;
    }
    .w > :not(.w__edge) { position: relative; z-index: 1; }

    // Tap/press feedback — the whole card sinks a touch and blooms its accent glow.
    .w--press { cursor: pointer; -webkit-tap-highlight-color: transparent; }
    .w--press:active {
      transform: scale(.985);
      box-shadow: var(--lift-1),
        0 0 0 1px color-mix(in srgb, var(--wa, var(--accent-a)) 40%, transparent),
        0 8px 30px color-mix(in srgb, var(--wa, var(--accent-a)) 28%, transparent);
    }

    .w--reorder { outline: 2px dashed var(--ink-faint); outline-offset: 3px; }

    .w__head { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
    .w__dot {
      flex: 0 0 auto; width: 10px; height: 10px; border-radius: 50%;
      background: linear-gradient(135deg, var(--wa, var(--accent-a)), var(--wb, var(--accent-b)));
      box-shadow: 0 0 0 4px color-mix(in srgb, var(--wa, var(--accent-a)) 16%, transparent);
    }
    .w__title {
      font-family: var(--font-ui); font-weight: 700; font-size: 13px; letter-spacing: .03em;
      text-transform: uppercase; color: var(--ink-dim);
    }

    .w__open {
      margin-left: auto; display: grid; place-items: center;
      width: 34px; height: 34px; border-radius: 50%;
      color: var(--ink-dim); text-decoration: none;
      background: color-mix(in srgb, var(--ink) 4%, transparent);
      transition: background 120ms var(--ease-out), color 120ms var(--ease-out), transform 120ms var(--ease-out);
    }
    .w__open:hover { background: color-mix(in srgb, var(--ink) 9%, transparent); color: var(--ink); }
    .w__open:active { transform: scale(.92); }
    .w__open mat-icon { font-size: 22px; width: 22px; height: 22px; }

    .w__reorder { margin-left: auto; display: flex; gap: 6px; }
    .w__rbtn {
      display: grid; place-items: center;
      width: 36px; height: 36px; border-radius: 11px;
      border: 1px solid var(--hairline); background: color-mix(in srgb, var(--ink) 3%, transparent);
      color: var(--ink); cursor: pointer;
      transition: transform 120ms var(--ease-spring);
    }
    .w__rbtn:active { transform: scale(.9); }
    .w__rbtn--off { color: var(--warn); }
    .w__rbtn mat-icon { font-size: 20px; width: 20px; height: 20px; }

    .w__skel { display: flex; flex-direction: column; gap: 12px; padding: 2px 0 6px; }

    // Tasteful states — never a bare line. An accent-tinted glyph anchors the message.
    .w__state {
      display: flex; align-items: center; gap: 10px; padding: 8px 2px;
    }
    .w__state-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 36px; height: 36px; border-radius: 12px;
      background: color-mix(in srgb, var(--wa, var(--accent-a)) 12%, transparent);
    }
    .w__state-ic mat-icon { font-size: 20px; width: 20px; height: 20px; color: color-mix(in srgb, var(--wa, var(--accent-a)) 80%, var(--ink)); }
    .w__state--fail .w__state-ic { background: color-mix(in srgb, var(--warn) 14%, transparent); }
    .w__state--fail .w__state-ic mat-icon { color: var(--warn); }
    .w__state-msg { margin: 0; flex: 1 1 auto; color: var(--ink-dim); font-size: 13px; }
    .w__retry {
      flex: 0 0 auto;
      display: inline-flex; align-items: center; gap: 6px;
      min-height: 36px; padding: 0 12px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: color-mix(in srgb, var(--ink) 4%, transparent);
      color: var(--ink); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer;
      transition: transform 120ms var(--ease-spring);
    }
    .w__retry:active { transform: scale(.94); }
    .w__retry mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `],
})
export class AtriumWidgetShell {
  /** Card title shown in the header. */
  readonly title = input.required<string>();
  /** Deep-link to the matching LIVE page; null hides the chevron (e.g. presence has no single target). */
  readonly route = input<string | null>(null);
  /** This domain's accent gradient stops (read off the page accent contract by default). */
  readonly accentA = input<string>('var(--accent-a)');
  readonly accentB = input<string>('var(--accent-b)');
  /** Current lifecycle phase. */
  readonly phase = input.required<WidgetPhase>();
  /** Friendly nudge shown in the empty state. */
  readonly emptyText = input<string>('Nothing here yet.');
  /** Material glyph anchoring the empty state (kept generic by default). */
  readonly emptyIcon = input<string>('inbox');
  /** Whether the parent is in long-press reorder mode (swaps the trailing slot for move/hide controls). */
  readonly reordering = input<boolean>(false);

  readonly retry = output<void>();
  readonly moveUp = output<void>();
  readonly moveDown = output<void>();
  readonly hide = output<void>();

  private readonly router = inject(Router);

  /** Press feedback only when the card has a tap target (a route) and we're not reordering. */
  protected readonly pressable = computed(() => !!this.route() && !this.reordering());

  /**
   * The WHOLE card is the tap target (matching its press feedback + cursor) — tapping anywhere on a widget
   * with a `route` opens that page, not just the small chevron. Inner controls (the rings' +water button)
   * already `stopPropagation`, and the chevron link does too, so they act without also navigating. No-op
   * while reordering or for routeless widgets (e.g. presence).
   */
  protected onCardTap(): void {
    const r = this.route();
    if (r && !this.reordering()) void this.router.navigateByUrl(r);
  }
}
