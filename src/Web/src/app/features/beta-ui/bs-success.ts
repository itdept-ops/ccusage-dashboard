import {
  booleanAttribute, ChangeDetectionStrategy, Component, input, output,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

/**
 * BETA-KIT Success — a full-screen COMPLETION state for the end of a flow (a logged entry, a
 * finished onboarding, a submitted form). The bigger sibling of {@link BetaEmptyState}: it fills its
 * container and centers a big accent-ringed icon (or a projected illustration), a headline, ONE
 * sub-sentence, then a primary CTA and an optional secondary link/button. The icon does a gentle
 * one-shot "pop" on enter (suppressed under reduced-motion). Meant to be shown as the whole screen
 * body once a flow succeeds — not an inline panel.
 *
 * Slots: pass a custom illustration/lottie/SVG via the default <ng-content> (it replaces the built-in
 * glyph orb when present); the headline/body/CTAs stay driven by inputs. The primary CTA is a real
 * <button> emitting `primary`, OR an <a routerLink> when `primaryLink` is set. The optional secondary
 * is likewise a button (`secondary`) or an <a routerLink> when `secondaryLink` is set.
 *
 * Icon orb + primary CTA read --accent-a/--accent-b (default) or the --signal success green when
 * `tone="success"`; text reads --ink/--ink-dim; radii/elevation from the kit tokens. DUAL-TOKEN:
 * every token resolves the kit value FIRST with a --tech-* fallback, so the SAME primitive renders
 * on a kit-host page AND on a plain --tech-* desktop vertical with no host setup. CTAs are >=44px
 * touch targets with a visible focus ring. Dependency-free (Material icons + RouterLink) +
 * tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-success
 *   inputs:    icon (string Material ligature, default 'check_circle'),
 *              title (string, required — the headline),
 *              body (string, default '' — the one sub-sentence),
 *              tone ('accent' | 'success', default 'success' — the orb/CTA hue),
 *              primaryLabel (string, default 'Done'),
 *              primaryIcon (string, default '' — optional leading glyph on the primary CTA),
 *              primaryLink (string | null, default null — when set the primary CTA is an <a routerLink>),
 *              secondaryLabel (string, default '' — presence renders the secondary action),
 *              secondaryLink (string | null, default null — when set the secondary is an <a routerLink>),
 *              padScreen (boolean via booleanAttribute, default true — center-fill the container; false = intrinsic block)
 *   outputs:   primary (void) — fired when the <button> primary CTA is tapped (not when primaryLink is set),
 *              secondary (void) — fired when the <button> secondary is tapped (not when secondaryLink is set)
 *
 * Usage:
 *   <app-bs-success title="Logged!" body="Nice work — that's 1,240 kcal today."
 *                   primaryLabel="Back to today" primaryLink="/tracker-beta"
 *                   secondaryLabel="Log another" (secondary)="reset()" />
 */
@Component({
  selector: 'app-bs-success',
  standalone: true,
  imports: [MatIconModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.pad-screen]': 'padScreen()' },
  template: `
    <div class="sx" role="status" aria-live="polite" [attr.aria-label]="title()">
      <div class="sx__art" [class.sx--success]="tone() === 'success'" aria-hidden="true">
        <ng-content>
          <span class="sx__orb"><mat-icon>{{ icon() }}</mat-icon></span>
        </ng-content>
      </div>
      <h2 class="sx__title">{{ title() }}</h2>
      @if (body()) { <p class="sx__body">{{ body() }}</p> }

      <div class="sx__actions">
        @if (primaryLink()) {
          <a class="sx__cta sx__cta--primary" [class.sx--success]="tone() === 'success'" [routerLink]="primaryLink()">
            @if (primaryIcon()) { <mat-icon aria-hidden="true">{{ primaryIcon() }}</mat-icon> }
            {{ primaryLabel() }}
          </a>
        } @else {
          <button type="button" class="sx__cta sx__cta--primary" [class.sx--success]="tone() === 'success'"
                  (click)="primary.emit()">
            @if (primaryIcon()) { <mat-icon aria-hidden="true">{{ primaryIcon() }}</mat-icon> }
            {{ primaryLabel() }}
          </button>
        }

        @if (secondaryLabel()) {
          @if (secondaryLink()) {
            <a class="sx__cta sx__cta--secondary" [routerLink]="secondaryLink()">{{ secondaryLabel() }}</a>
          } @else {
            <button type="button" class="sx__cta sx__cta--secondary" (click)="secondary.emit()">{{ secondaryLabel() }}</button>
          }
        }
      </div>
    </div>
  `,
  styles: [`
    /* Resolve each token once with a --tech-* fallback so the state works on kit + tech pages alike. */
    :host {
      display: block;
      --sx-accent-a: var(--accent-a, var(--tech-accent, #7c8cff));
      --sx-accent-b: var(--accent-b, var(--tech-accent-2, var(--tech-accent, #3b82f6)));
      --sx-signal: var(--signal, var(--tech-success, #34d399));
      --sx-on-accent: var(--ink-on-accent, #fff);
      --sx-ink: var(--ink, var(--tech-text, #e6edf6));
      --sx-ink-dim: var(--ink-dim, var(--tech-text-secondary, #9ba9bd));
      --sx-edge: var(--hairline, var(--tech-border-subtle, rgba(255,255,255,.12)));
      --sx-r-pill: var(--r-pill, 999px);
      --sx-shadow: var(--lift-2, 0 10px 30px rgba(0,0,0,.22));
      --sx-focus: var(--focus, var(--tech-accent, #7c8cff));
      --sx-ease: var(--ease-out, cubic-bezier(.22,1,.36,1));
      --sx-spring: var(--ease-spring, cubic-bezier(.34,1.56,.64,1));
      --sx-font-ui: var(--font-ui, inherit);
      --sx-font-display: var(--font-display, var(--font-ui, inherit));
      /* the active hue pair (accent by default; success green when tone=success) */
      --sx-a: var(--sx-accent-a);
      --sx-b: var(--sx-accent-b);
    }
    /* Fill the viewport/container and center everything when padScreen (the default). */
    :host.pad-screen { min-height: 100%; height: 100%; }
    :host.pad-screen .sx {
      min-height: 100%;
      justify-content: center;
      padding-block: max(24px, env(safe-area-inset-top, 0px)) max(24px, env(safe-area-inset-bottom, 0px));
    }
    .sx {
      display: flex; flex-direction: column; align-items: center; text-align: center;
      gap: .85rem; padding: clamp(28px, 9vw, 56px) 1.25rem;
      box-sizing: border-box;
    }
    .sx__art.sx--success { --sx-a: var(--sx-signal); --sx-b: var(--sx-signal); }
    .sx__orb {
      display: grid; place-items: center;
      width: 96px; height: 96px; border-radius: 50%;
      color: var(--sx-on-accent);
      background: linear-gradient(135deg, var(--sx-a), var(--sx-b));
      box-shadow: var(--sx-shadow);
      animation: sx-pop 460ms var(--sx-spring) both;
    }
    .sx__orb mat-icon { font-size: 52px; width: 52px; height: 52px; }
    @keyframes sx-pop {
      0%   { transform: scale(.4); opacity: 0; }
      60%  { transform: scale(1.06); opacity: 1; }
      100% { transform: scale(1); }
    }
    .sx__title {
      margin: .3rem 0 0; font-family: var(--sx-font-display);
      font-size: clamp(1.4rem, 6vw, 1.8rem); font-weight: 800; letter-spacing: -.01em;
      color: var(--sx-ink);
    }
    .sx__body { margin: 0; max-width: 34ch; font-size: .95rem; line-height: 1.5; color: var(--sx-ink-dim); }
    .sx__actions {
      display: flex; flex-direction: column; align-items: stretch; gap: .5rem;
      margin-top: .6rem; width: min(100%, 320px);
    }
    .sx__cta {
      display: inline-flex; align-items: center; justify-content: center; gap: .4rem;
      min-height: 48px; padding: 0 1.3rem;
      border-radius: var(--sx-r-pill);
      font: inherit; font-family: var(--sx-font-ui); font-weight: 700; text-decoration: none;
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      transition: opacity 120ms var(--sx-ease), transform 120ms var(--sx-ease);
    }
    .sx__cta:active { opacity: .85; transform: translateY(1px); }
    .sx__cta:focus-visible { outline: 2px solid var(--sx-focus); outline-offset: 3px; }
    .sx__cta--primary {
      border: 0; color: var(--sx-on-accent);
      background: linear-gradient(135deg, var(--sx-accent-a), var(--sx-accent-b));
    }
    .sx__cta--primary.sx--success { background: var(--sx-signal); }
    .sx__cta--secondary {
      background: transparent; color: var(--sx-ink);
      border: 1px solid var(--sx-edge);
    }
    .sx__cta mat-icon { font-size: 20px; width: 20px; height: 20px; }
    @media (prefers-reduced-motion: reduce) {
      .sx__orb { animation: none; }
    }
  `],
})
export class BetaSuccess {
  /** Material ligature for the built-in orb glyph (ignored when an illustration is projected). */
  readonly icon = input<string>('check_circle');
  /** The headline ("Logged!", "All set", "You're done"…). */
  readonly title = input.required<string>();
  /** The single supporting sentence. */
  readonly body = input<string>('');
  /** Orb + primary CTA hue: the page accent, or the success green (default). */
  readonly tone = input<'accent' | 'success'>('success');
  /** Primary CTA label. */
  readonly primaryLabel = input<string>('Done');
  /** Optional leading glyph on the primary CTA. */
  readonly primaryIcon = input<string>('');
  /** When set, the primary CTA is an `<a routerLink>` to this path instead of an action button. */
  readonly primaryLink = input<string | null>(null);
  /** Secondary action label; when blank no secondary renders. */
  readonly secondaryLabel = input<string>('');
  /** When set, the secondary is an `<a routerLink>` to this path instead of an action button. */
  readonly secondaryLink = input<string | null>(null);
  /** Center-fill the container as a full-screen state (default); false = intrinsic block height. */
  readonly padScreen = input<boolean, unknown>(true, { transform: booleanAttribute });
  /** Fired when the <button> primary CTA is tapped (ignored when `primaryLink` is set). */
  readonly primary = output<void>();
  /** Fired when the <button> secondary is tapped (ignored when `secondaryLink` is set). */
  readonly secondary = output<void>();
}
