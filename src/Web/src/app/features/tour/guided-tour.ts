import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { TourService, TourStep } from '../../core/tour';

/** A resolved spotlight box in viewport coordinates (the target element's padded bounding rect). */
interface SpotRect {
  top: number;
  left: number;
  width: number;
  height: number;
}

/** Where the tooltip card sits relative to the spotlight (resolved from the step hint + available room). */
type Side = 'top' | 'bottom' | 'left' | 'right';

const SPOT_PAD = 8; // px breathing room around the highlighted element
const CARD_W = 320; // nominal card width used for placement math
const CARD_GAP = 14; // gap between spotlight and card
const VIEWPORT_MARGIN = 12; // keep the card this far from the viewport edge

/**
 * GuidedTour — a REUSABLE coach-mark / spotlight overlay. Mounted ONCE at the shell root; it renders
 * itself only while {@link TourService.active} points at a tour. For the current step it resolves the
 * target element by its `data-tour` id, paints a dimmed full-screen backdrop with a rounded CUT-OUT +
 * glow ring around the target, and positions a tooltip CARD (title · blurb · "n / N" · Back/Next/Skip,
 * Finish on the last step) on the best-fitting side.
 *
 * ROBUSTNESS: a step whose anchor isn't in the DOM (not yet rendered, or hidden by a permission gate) is
 * skipped automatically in the chosen direction; if NO remaining step resolves, the tour ends cleanly.
 * Position is recomputed on scroll / resize so the spotlight tracks the element.
 *
 * A11Y: role="dialog" + aria-label on the card; focus moves into the card on each step and is restored to
 * the prior active element on close; Tab is trapped within the card; Escape = Skip; Arrow/Enter operate the
 * controls natively. Respects prefers-reduced-motion (the global guard kills the ring/card animation).
 *
 * It uses ONLY --tech-* tokens (app chrome language), never the Aurora/Strata scoped vars.
 *
 * CONTRACT: selector `app-guided-tour`; no inputs — it is driven entirely by {@link TourService}.
 */
@Component({
  selector: 'app-guided-tour',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule],
  template: `
    @if (tour.active(); as def) {
      @if (rect(); as r) {
        <!-- Dimmed backdrop with a transparent cut-out: four panels around the spotlight + a glow ring.
             Clicking the backdrop is inert (the tour is driven only by the card controls / Escape). -->
        <div class="gt-backdrop" aria-hidden="true">
          <div class="gt-dim gt-dim--top" [style.height.px]="r.top"></div>
          <div class="gt-dim gt-dim--bottom" [style.top.px]="r.top + r.height"></div>
          <div class="gt-dim gt-dim--left"
               [style.top.px]="r.top" [style.height.px]="r.height" [style.width.px]="r.left"></div>
          <div class="gt-dim gt-dim--right"
               [style.top.px]="r.top" [style.height.px]="r.height" [style.left.px]="r.left + r.width"></div>
          <div class="gt-ring"
               [style.top.px]="r.top" [style.left.px]="r.left"
               [style.width.px]="r.width" [style.height.px]="r.height"></div>
        </div>

        <div #card class="gt-card" [class]="'gt-card--' + side()"
             role="dialog" aria-modal="true" [attr.aria-label]="'Tour: ' + step().title"
             [style.top.px]="cardPos().top" [style.left.px]="cardPos().left"
             (keydown)="onKeydown($event)">
          <div class="gt-card__head">
            <span class="gt-card__step">{{ index() + 1 }} / {{ def.steps.length }}</span>
            <button #skipBtn type="button" class="gt-card__close" (click)="skip()" aria-label="Skip tour">
              <mat-icon aria-hidden="true">close</mat-icon>
            </button>
          </div>

          <h2 class="gt-card__title">{{ step().title }}</h2>
          <p class="gt-card__blurb">{{ step().blurb }}</p>

          <!-- Progress dots: a quick "where am I" affordance. -->
          <div class="gt-card__dots" aria-hidden="true">
            @for (s of def.steps; track $index) {
              <span class="gt-dot" [class.gt-dot--on]="$index === index()"></span>
            }
          </div>

          <div class="gt-card__actions">
            <button type="button" class="gt-btn gt-btn--ghost" (click)="skip()">Skip</button>
            <span class="gt-spacer"></span>
            @if (index() > 0) {
              <button type="button" class="gt-btn" (click)="back()">Back</button>
            }
            <button #nextBtn type="button" class="gt-btn gt-btn--primary" (click)="next()">
              {{ isLast() ? 'Finish' : 'Next' }}
            </button>
          </div>
        </div>
      }
    }
  `,
  styleUrl: './guided-tour.scss',
})
export class GuidedTour {
  readonly tour = inject(TourService);

  private readonly card = viewChild<ElementRef<HTMLElement>>('card');
  private readonly nextBtn = viewChild<ElementRef<HTMLButtonElement>>('nextBtn');

  /** The element focused before the tour opened — focus returns here when it closes. */
  private opener: HTMLElement | null = null;

  /** Current step index within the active tour. */
  readonly index = signal(0);

  /** A tick bumped on scroll/resize so the rect/position computeds recompute against the live DOM. */
  private readonly geomTick = signal(0);

  readonly step = computed<TourStep>(() => {
    const def = this.tour.active();
    return def?.steps[this.index()] ?? { anchor: '', title: '', blurb: '' };
  });

  readonly isLast = computed(() => {
    const def = this.tour.active();
    return !!def && this.index() >= def.steps.length - 1;
  });

  /** Resolve the current step's target element by its data-tour id (null if absent / hidden). */
  private targetEl(): HTMLElement | null {
    this.geomTick(); // dependency: re-resolve on scroll/resize
    const anchor = this.step().anchor;
    if (!anchor) return null;
    const el = document.querySelector<HTMLElement>(`[data-tour="${CSS.escape(anchor)}"]`);
    if (!el) return null;
    // A present-but-invisible element (display:none / 0-box, e.g. behind a CSS nav breakpoint) doesn't
    // anchor a spotlight — treat it as absent so the step is skipped.
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return null;
    return el;
  }

  /** The padded spotlight rect for the current target, clamped to the viewport (null when unresolved). */
  readonly rect = computed<SpotRect | null>(() => {
    const el = this.targetEl();
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const top = Math.max(0, r.top - SPOT_PAD);
    const left = Math.max(0, r.left - SPOT_PAD);
    const right = Math.min(window.innerWidth, r.right + SPOT_PAD);
    const bottom = Math.min(window.innerHeight, r.bottom + SPOT_PAD);
    return { top, left, width: right - left, height: bottom - top };
  });

  /** The resolved side the card sits on, honoring the step hint but flipping when there's no room. */
  readonly side = computed<Side>(() => {
    const r = this.rect();
    if (!r) return 'bottom';
    const hint = this.step().placement;
    const vh = window.innerHeight;
    const vw = window.innerWidth;
    const roomBelow = vh - (r.top + r.height);
    const roomAbove = r.top;
    const roomRight = vw - (r.left + r.width);
    const roomLeft = r.left;
    const CARD_H = 230; // nominal; only used to choose a side, the card itself is auto-height

    // Honor the hint when it fits; otherwise pick the axis with the most room.
    if (hint === 'top' && roomAbove > CARD_H + CARD_GAP) return 'top';
    if (hint === 'bottom' && roomBelow > CARD_H + CARD_GAP) return 'bottom';
    if (hint === 'left' && roomLeft > CARD_W + CARD_GAP) return 'left';
    if (hint === 'right' && roomRight > CARD_W + CARD_GAP) return 'right';

    if (roomBelow >= CARD_H + CARD_GAP) return 'bottom';
    if (roomAbove >= CARD_H + CARD_GAP) return 'top';
    if (roomRight >= CARD_W + CARD_GAP) return 'right';
    if (roomLeft >= CARD_W + CARD_GAP) return 'left';
    return 'bottom'; // last resort — the clamp below keeps it on-screen
  });

  /** Card top/left in viewport px, placed on {@link side} and clamped to stay fully on-screen. */
  readonly cardPos = computed<{ top: number; left: number }>(() => {
    const r = this.rect();
    if (!r) return { top: VIEWPORT_MARGIN, left: VIEWPORT_MARGIN };
    const side = this.side();
    const measured = this.card()?.nativeElement.getBoundingClientRect();
    const cw = measured?.width || CARD_W;
    const ch = measured?.height || 200;
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    let top: number;
    let left: number;
    switch (side) {
      case 'top':
        top = r.top - ch - CARD_GAP;
        left = r.left + r.width / 2 - cw / 2;
        break;
      case 'left':
        top = r.top + r.height / 2 - ch / 2;
        left = r.left - cw - CARD_GAP;
        break;
      case 'right':
        top = r.top + r.height / 2 - ch / 2;
        left = r.left + r.width + CARD_GAP;
        break;
      case 'bottom':
      default:
        top = r.top + r.height + CARD_GAP;
        left = r.left + r.width / 2 - cw / 2;
        break;
    }
    // Clamp into the viewport so the card is never clipped at an edge.
    left = Math.min(Math.max(VIEWPORT_MARGIN, left), vw - cw - VIEWPORT_MARGIN);
    top = Math.min(Math.max(VIEWPORT_MARGIN, top), vh - ch - VIEWPORT_MARGIN);
    return { top, left };
  });

  constructor() {
    // On (re)activation, reset to the first RESOLVABLE step and capture the opener for focus restore.
    effect(() => {
      const def = this.tour.active();
      if (!def) return;
      const active = (typeof document !== 'undefined' ? document.activeElement : null) as HTMLElement | null;
      if (active && active.tagName !== 'BODY') this.opener = active;
      this.index.set(0);
      // Defer to next frame so anchors rendered this cycle exist before we resolve the first step.
      requestAnimationFrame(() => {
        this.advanceToResolvable(1);
        this.focusCard();
      });
    });

    // Move focus into the card whenever the resolved step changes while a tour is running.
    effect(() => {
      this.index();
      if (this.tour.active() && this.rect()) {
        requestAnimationFrame(() => this.focusCard());
      }
    });
  }

  /**
   * Walk from the current index in `dir` (+1 / -1) until a step whose anchor resolves to a visible
   * element is found. If none remains in that direction, end the tour (marking it seen so a first-run
   * with zero present anchors doesn't loop forever).
   */
  private advanceToResolvable(dir: 1 | -1): void {
    const def = this.tour.active();
    if (!def) return;
    let i = this.index();
    while (i >= 0 && i < def.steps.length) {
      const anchor = def.steps[i].anchor;
      const el = anchor ? document.querySelector<HTMLElement>(`[data-tour="${CSS.escape(anchor)}"]`) : null;
      const r = el?.getBoundingClientRect();
      if (el && r && r.width > 0 && r.height > 0) {
        this.index.set(i);
        return;
      }
      i += dir;
    }
    // Nothing resolvable left → finish gracefully.
    this.tour.end({ markSeen: true });
  }

  next(): void {
    if (this.isLast()) {
      this.finish();
      return;
    }
    this.index.update((i) => i + 1);
    this.advanceToResolvable(1);
  }

  back(): void {
    if (this.index() === 0) return;
    this.index.update((i) => i - 1);
    this.advanceToResolvable(-1);
  }

  skip(): void {
    this.close(true);
  }

  private finish(): void {
    this.close(true);
  }

  /** Tear down: mark seen (on finish/skip) and restore focus to the opener. */
  private close(markSeen: boolean): void {
    this.tour.end({ markSeen });
    const opener = this.opener;
    this.opener = null;
    if (opener?.isConnected) queueMicrotask(() => opener.focus?.());
  }

  private focusCard(): void {
    // Land on Next (the primary action) so Enter advances immediately; fall back to the card.
    const btn = this.nextBtn()?.nativeElement;
    if (btn) {
      btn.focus();
      return;
    }
    this.card()?.nativeElement.focus();
  }

  /** Keyboard contract on the card: Escape = Skip; Tab is trapped within the card. */
  onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      this.skip();
      return;
    }
    if (e.key !== 'Tab') return;
    const card = this.card()?.nativeElement;
    if (!card) return;
    const sel =
      'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]),' +
      ' select:not([disabled]), [tabindex]:not([tabindex="-1"])';
    const els = Array.from(card.querySelectorAll<HTMLElement>(sel)).filter((el) => el.offsetParent !== null);
    if (els.length === 0) return;
    const first = els[0];
    const last = els[els.length - 1];
    const activeEl = document.activeElement as HTMLElement | null;
    if (e.shiftKey) {
      if (activeEl === first || !card.contains(activeEl)) {
        e.preventDefault();
        last.focus();
      }
    } else if (activeEl === last || !card.contains(activeEl)) {
      e.preventDefault();
      first.focus();
    }
  }

  /** Reposition the spotlight + card when the page scrolls or the viewport resizes. */
  @HostListener('window:scroll')
  @HostListener('window:resize')
  onGeomChange(): void {
    if (this.tour.active()) this.geomTick.update((t) => t + 1);
  }
}
