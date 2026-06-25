import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { BetaSkeleton } from '../../beta-ui';

/** The four lifecycle phases every Hearth glance card renders. */
export type HearthPhase = 'loading' | 'ready' | 'empty' | 'failed';

/**
 * HEARTH card shell — the shared DEPTH chrome for every Family-Hearth glance card, rebuilt on the shared
 * beta-ui "Strata" foundation. It is NOT a flat card: a sediment `--bg-rise` extrusion lifted with
 * `--lift-2`, a gradient hairline edge that picks up the per-domain accent, an accent glow that blooms on
 * press, and a tasteful skeleton / empty / failed scaffold so one card's failure NEVER escapes its own
 * card. The accent is passed as a from/to gradient pair, so the dot, edge, and glow all render the
 * domain's hue as a real gradient (never flat) inside the page's amber→rose accent.
 *
 * This is the canonical Atrium `atr-widget-shell` pattern applied to the family page (NOT imported from
 * /beta/home — a self-contained copy so the family page owns its chrome). Inherits the kit token contract
 * from the page `:host` cascade (no global `--tech-*`, no live imports). The READY body projects into
 * `[body]`; a `[head-trailing]` slot carries a count/pill in the header; the header chevron deep-links to
 * the matching LIVE family page via `route`.
 */
@Component({
  selector: 'fb-hearth-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule, BetaSkeleton],
  template: `
    <section class="w" [class.w--press]="pressable()"
             [style.--wa]="accentA()" [style.--wb]="accentB()">
      <span class="w__edge" aria-hidden="true"></span>
      <header class="w__head">
        @if (icon()) {
          <span class="w__glyph" aria-hidden="true"><mat-icon>{{ icon() }}</mat-icon></span>
        } @else {
          <span class="w__dot" aria-hidden="true"></span>
        }
        <span class="w__title">{{ title() }}</span>
        <ng-content select="[head-trailing]"></ng-content>
        @if (route(); as r) {
          <a class="w__open" [routerLink]="r" [attr.aria-label]="'Open ' + title()">
            <mat-icon aria-hidden="true">chevron_right</mat-icon>
          </a>
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
    .w {
      position: relative;
      display: block;
      border-radius: var(--r-card);
      background:
        radial-gradient(120% 90% at 0% 0%, color-mix(in srgb, var(--wa, var(--accent-a)) 10%, transparent), transparent 60%),
        var(--bg-rise);
      box-shadow: var(--lift-2);
      padding: 16px;
      scroll-snap-align: start;
      overflow: hidden;
      isolation: isolate;
      transition: transform 140ms var(--ease-out), box-shadow 220ms var(--ease-out);
    }
    /* Gradient hairline edge that picks up the domain accent (top-lit), masked to a 1px ring. */
    .w__edge {
      position: absolute; inset: 0; border-radius: inherit; padding: 1px; pointer-events: none; z-index: 0;
      background: linear-gradient(150deg,
        color-mix(in srgb, var(--wa, var(--accent-a)) 55%, transparent),
        var(--hairline) 38%,
        color-mix(in srgb, var(--wb, var(--accent-b)) 28%, transparent));
      -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
      -webkit-mask-composite: xor; mask-composite: exclude;
    }
    .w > :not(.w__edge) { position: relative; z-index: 1; }

    .w--press { cursor: pointer; -webkit-tap-highlight-color: transparent; }
    .w--press:active {
      transform: scale(.985);
      box-shadow: var(--lift-1),
        0 0 0 1px color-mix(in srgb, var(--wa, var(--accent-a)) 40%, transparent),
        0 8px 30px color-mix(in srgb, var(--wa, var(--accent-a)) 28%, transparent);
    }

    .w__head { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
    .w__dot {
      flex: 0 0 auto; width: 10px; height: 10px; border-radius: 50%;
      background: linear-gradient(135deg, var(--wa, var(--accent-a)), var(--wb, var(--accent-b)));
      box-shadow: 0 0 0 4px color-mix(in srgb, var(--wa, var(--accent-a)) 16%, transparent);
    }
    .w__glyph {
      flex: 0 0 auto; display: grid; place-items: center; width: 30px; height: 30px; border-radius: 10px;
      background: linear-gradient(135deg,
        color-mix(in srgb, var(--wa, var(--accent-a)) 24%, transparent),
        color-mix(in srgb, var(--wb, var(--accent-b)) 24%, transparent));
    }
    .w__glyph mat-icon {
      font-size: 19px; width: 19px; height: 19px;
      color: color-mix(in srgb, var(--wa, var(--accent-a)) 78%, var(--ink));
    }
    .w__title {
      font-family: var(--font-ui); font-weight: 700; font-size: 13px; letter-spacing: .03em;
      text-transform: uppercase; color: var(--ink-dim);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
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
    .w__open:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .w__open mat-icon { font-size: 22px; width: 22px; height: 22px; }

    .w__skel { display: flex; flex-direction: column; gap: 12px; padding: 2px 0 6px; }

    /* Tasteful states — never a bare line. An accent-tinted glyph anchors the message. */
    .w__state { display: flex; align-items: center; gap: 10px; padding: 8px 2px; }
    .w__state-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 36px; height: 36px; border-radius: 12px;
      background: color-mix(in srgb, var(--wa, var(--accent-a)) 12%, transparent);
    }
    .w__state-ic mat-icon { font-size: 20px; width: 20px; height: 20px; color: color-mix(in srgb, var(--wa, var(--accent-a)) 80%, var(--ink)); }
    .w__state--fail .w__state-ic { background: color-mix(in srgb, var(--warn) 14%, transparent); }
    .w__state--fail .w__state-ic mat-icon { color: var(--warn); }
    .w__state-msg { margin: 0; flex: 1 1 auto; color: var(--ink-dim); font-size: 13px; }
    .w__retry {
      flex: 0 0 auto; display: inline-flex; align-items: center; gap: 6px;
      min-height: 36px; padding: 0 12px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: color-mix(in srgb, var(--ink) 4%, transparent);
      color: var(--ink); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer;
      transition: transform 120ms var(--ease-spring);
    }
    .w__retry:active { transform: scale(.94); }
    .w__retry:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .w__retry mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `],
})
export class HearthShell {
  /** Card title shown in the header. */
  readonly title = input.required<string>();
  /** Deep-link to the matching LIVE family page; null hides the chevron. */
  readonly route = input<string | null>(null);
  /** Material glyph replacing the accent dot (e.g. `cleaning_services`); empty keeps the dot. */
  readonly icon = input<string>('');
  /** This domain's accent gradient stops (read off the page accent contract by default). */
  readonly accentA = input<string>('var(--accent-a)');
  readonly accentB = input<string>('var(--accent-b)');
  /** Current lifecycle phase. */
  readonly phase = input.required<HearthPhase>();
  /** Friendly nudge shown in the empty state. */
  readonly emptyText = input<string>('Nothing here yet.');
  /** Material glyph anchoring the empty state. */
  readonly emptyIcon = input<string>('inbox');

  readonly retry = output<void>();

  /** Press feedback only when the card has a tap target (a route). */
  protected readonly pressable = computed(() => !!this.route());
}
