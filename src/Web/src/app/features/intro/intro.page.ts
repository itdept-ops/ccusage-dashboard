import {
  ChangeDetectionStrategy, Component, ElementRef, inject, signal, viewChild,
} from '@angular/core';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

/** The localStorage key that records the user has seen (or skipped) the first-run intro. */
export const INTRO_SEEN_KEY = 'usage_iq_intro_seen_v1';

/** One pitch slide in the intro carousel. */
interface IntroSlide {
  /** Material-style ligature icon rendered in the illustration block. */
  icon: string;
  /** The headline copy. */
  headline: string;
  /** The supporting subtext. */
  subtext: string;
}

/**
 * FIRST-RUN INTRO CAROUSEL — a full-bleed, swipeable 3-slide onboarding pitch shown once before
 * the sign-in surface. Built on native CSS scroll-snap (NO JS carousel lib): the track is a
 * horizontal scroll-snap container, one slide per viewport. Dot pagination reflects the snapped
 * slide (driven by a scroll listener), a persistent top-right "Skip" affordance is always
 * reachable, and the final slide surfaces a "Get started" CTA. On skip OR finish we set the
 * localStorage seen-flag ({@link INTRO_SEEN_KEY}) and navigate to /login.
 *
 * Marketing/Aurora look: reuses the always-dark Aurora surface + display/body/CTA type so it
 * flows straight into the login/sign-in pages. Uses var(--aur-*)/(--tech-*) marketing tokens.
 *
 * Route: `/intro` (public, bare). The sign-in page redirects here first-run.
 */
@Component({
  selector: 'app-intro',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="intro" role="region" aria-roledescription="carousel" aria-label="Welcome to Usage IQ">
      <button type="button" class="intro-skip" (click)="finish()">Skip</button>

      <div #track class="intro-track" (scroll)="onScroll()" tabindex="-1">
        @for (s of slides; track s.headline; let i = $index) {
          <section class="intro-slide"
                   role="group" aria-roledescription="slide"
                   [attr.aria-label]="'Slide ' + (i + 1) + ' of ' + slides.length"
                   [attr.aria-hidden]="active() === i ? null : 'true'">
            <span class="intro-art" aria-hidden="true">
              <span class="intro-art-glow"></span>
              <mat-icon class="intro-art-icon">{{ s.icon }}</mat-icon>
            </span>
            <h1 class="intro-headline">{{ s.headline }}</h1>
            <p class="intro-sub">{{ s.subtext }}</p>
          </section>
        }
      </div>

      <div class="intro-foot">
        <div class="intro-dots" role="tablist" aria-label="Slides">
          @for (s of slides; track s.headline; let i = $index) {
            <button type="button" class="intro-dot"
                    role="tab"
                    [class.is-on]="active() === i"
                    [attr.aria-selected]="active() === i"
                    [attr.aria-label]="'Go to slide ' + (i + 1)"
                    (click)="goTo(i)"></button>
          }
        </div>

        @if (active() >= slides.length - 1) {
          <button type="button" class="intro-cta" (click)="finish()">
            Get started
            <mat-icon aria-hidden="true">arrow_forward</mat-icon>
          </button>
        } @else {
          <button type="button" class="intro-next" (click)="next()">
            Next
            <mat-icon aria-hidden="true">chevron_right</mat-icon>
          </button>
        }
      </div>
    </main>
  `,
  styleUrl: './intro.page.scss',
})
export class IntroPage {
  private readonly router = inject(Router);
  private readonly track = viewChild.required<ElementRef<HTMLDivElement>>('track');

  /** The three pitch slides, left to right. */
  protected readonly slides: IntroSlide[] = [
    { icon: 'bolt', headline: 'Track your day', subtext: 'Meals, moves, water, and money — one tap logs it, and Usage IQ keeps the running picture for you.' },
    { icon: 'diversity_3', headline: 'One family, one Hub', subtext: 'Calendars, lists, chores, and finances for everyone you love — shared in a single private space.' },
    { icon: 'auto_awesome', headline: 'Agents that act for you', subtext: 'Set it once and your AI handles the rest — proactive nudges, plans, and actions across your life.' },
  ];

  /** The index of the currently snapped slide (drives dots + CTA/Next swap). */
  protected readonly active = signal(0);

  /** Recompute the active slide from the track's scroll position (nearest snap point). */
  protected onScroll(): void {
    const el = this.track().nativeElement;
    const w = el.clientWidth || 1;
    const i = Math.round(el.scrollLeft / w);
    const clamped = Math.max(0, Math.min(this.slides.length - 1, i));
    if (clamped !== this.active()) this.active.set(clamped);
  }

  /** Scroll-snap to a given slide index. */
  protected goTo(i: number): void {
    const el = this.track().nativeElement;
    el.scrollTo({ left: i * el.clientWidth, behavior: 'smooth' });
  }

  /** Advance to the next slide. */
  protected next(): void {
    this.goTo(Math.min(this.slides.length - 1, this.active() + 1));
  }

  /** Record the seen-flag and leave for sign-in (skip or finish share one exit). */
  protected finish(): void {
    try { localStorage.setItem(INTRO_SEEN_KEY, '1'); } catch { /* private mode / storage blocked — proceed anyway */ }
    this.router.navigateByUrl('/login');
  }
}
