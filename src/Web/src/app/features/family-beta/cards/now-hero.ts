import {
  ChangeDetectionStrategy, Component, OnDestroy, computed, input, signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { FamilyBriefing, FamilyToday, FamilyTodayEvent, FamilyTodayTimer } from '../../../core/models';

/** What the hero is currently surfacing, in priority order. */
type HeroMode = 'event' | 'timer' | 'narrative' | 'calm';

/**
 * The Hearth "Now" hero — the warm immersive FOCAL glass card at the top of the column, rebuilt on the
 * shared beta-ui "Strata" foundation. It degrades gracefully so it is NEVER empty: the caller's soonest
 * upcoming event today (with a live countdown chip) → else the soonest still-running timer (countdown) →
 * else the AI morning `briefing.narrative` → else a calm "all clear" line. It also surfaces a today
 * snapshot strip — today's remaining events as a mini timeline with each local time — so the hero reads
 * as a real next-event/mini-calendar card, not a flat banner.
 *
 * The amber→rose page accent drives the gradient bloom; big Clash Display numerals carry the countdown.
 * `today` + `briefing` are passed in (the page owns the shared best-effort loads), so this card holds no
 * network of its own. The `nextEventOf` reducer is COPIED verbatim from family-home.ts:114 (not imported).
 */
@Component({
  selector: 'fb-now-hero',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule],
  template: `
    <section class="hero" [class.hero--calm]="mode() === 'calm'">
      <span class="hero__bloom" aria-hidden="true"></span>

      <div class="hero__top">
        <span class="hero__eyebrow">
          <mat-icon aria-hidden="true">{{ icon() }}</mat-icon>
          {{ eyebrow() }}
        </span>
        @if (countdown(); as cd) {
          <span class="hero__chip" aria-hidden="true">
            <mat-icon aria-hidden="true">schedule</mat-icon>
            <b class="hero__cd">{{ cd }}</b>
          </span>
        }
      </div>

      @switch (mode()) {
        @case ('event') {
          <a class="hero__main" routerLink="/family/calendar">
            <span class="hero__title">{{ event()!.title }}</span>
            <span class="hero__meta">{{ event()!.allDay ? 'All day' : event()!.localTime }}</span>
          </a>
        }
        @case ('timer') {
          <a class="hero__main" routerLink="/family/timer">
            <span class="hero__title">{{ timer()!.label || 'Timer' }}</span>
            <span class="hero__meta">{{ countdown() ? 'Counting down' : 'Ending now' }}</span>
          </a>
        }
        @case ('narrative') {
          <p class="hero__narrative">{{ narrative() }}</p>
        }
        @default {
          <p class="hero__narrative">Nothing on the calendar right now — enjoy the quiet.</p>
        }
      }

      @if (laterEvents().length) {
        <div class="hero__strip" role="list" aria-label="Later today">
          @for (e of laterEvents(); track e.id) {
            <a class="hero__slot" role="listitem" routerLink="/family/calendar"
               [attr.aria-label]="e.title + ' — ' + (e.allDay ? 'All day' : e.localTime)">
              <span class="hero__slot-time">{{ e.allDay ? '—' : e.localTime }}</span>
              <span class="hero__slot-title">{{ e.title }}</span>
            </a>
          }
        </div>
      }
    </section>
  `,
  styles: [`
    .hero {
      position: relative; isolation: isolate;
      display: flex; flex-direction: column; gap: 10px;
      border-radius: var(--r-glass);
      padding: 20px;
      background: linear-gradient(150deg, var(--accent-a) -10%, var(--accent-b) 120%);
      color: #1a0f06;
      box-shadow: var(--lift-3);
      scroll-snap-align: start;
      overflow: hidden;
    }
    .hero__bloom {
      position: absolute; inset: 0; z-index: -1; pointer-events: none;
      background: radial-gradient(140% 120% at 110% -20%, rgba(255,255,255,.32), transparent 55%);
    }
    .hero--calm {
      background: var(--bg-rise);
      color: var(--ink);
      box-shadow: var(--lift-2);
    }
    .hero--calm .hero__bloom {
      background: radial-gradient(120% 100% at 100% 0%, color-mix(in srgb, var(--accent-a) 22%, transparent), transparent 60%);
    }

    .hero__top { display: flex; align-items: center; gap: 10px; }
    .hero__eyebrow {
      display: inline-flex; align-items: center; gap: 6px;
      font-size: 12px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase;
      opacity: .9;
    }
    .hero__eyebrow mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .hero__chip {
      margin-left: auto; display: inline-flex; align-items: center; gap: 5px;
      padding: 4px 10px 4px 8px; border-radius: var(--r-pill);
      background: rgba(0,0,0,.16);
    }
    .hero--calm .hero__chip { background: color-mix(in srgb, var(--accent-a) 18%, transparent); }
    .hero__chip mat-icon { font-size: 15px; width: 15px; height: 15px; opacity: .85; }
    .hero__cd { font-family: var(--font-display); font-size: 15px; font-variant-numeric: tabular-nums; letter-spacing: -.01em; }

    .hero__main { display: flex; flex-direction: column; gap: 4px; text-decoration: none; color: inherit; }
    .hero__title {
      font-family: var(--font-display); font-weight: 600; font-size: 26px; line-height: 1.1; letter-spacing: -.02em;
      overflow: hidden; text-overflow: ellipsis; display: -webkit-box;
      -webkit-line-clamp: 2; -webkit-box-orient: vertical;
    }
    .hero__meta { font-size: 14px; font-weight: 700; opacity: .9; }
    .hero__main:focus-visible { outline: 2px solid currentColor; outline-offset: 3px; border-radius: 8px; }
    .hero__narrative { margin: 0; font-size: 16px; line-height: 1.45; font-weight: 600; }

    .hero__strip {
      display: flex; gap: 8px; overflow-x: auto; margin: 4px -4px -4px; padding: 8px 4px 4px;
      border-top: 1px solid rgba(0,0,0,.14);
      scrollbar-width: none; scroll-snap-type: x proximity; -webkit-overflow-scrolling: touch;
    }
    .hero--calm .hero__strip { border-top-color: var(--hairline); }
    .hero__strip::-webkit-scrollbar { display: none; }
    .hero__slot {
      flex: 0 0 auto; scroll-snap-align: start; max-width: 60%;
      display: flex; flex-direction: column; gap: 1px;
      padding: 6px 12px; border-radius: 14px; text-decoration: none; color: inherit;
      background: rgba(255,255,255,.18);
    }
    .hero--calm .hero__slot { background: color-mix(in srgb, var(--accent-a) 10%, transparent); }
    .hero__slot-time { font-family: var(--font-display); font-size: 13px; font-weight: 600; font-variant-numeric: tabular-nums; }
    .hero__slot-title { font-size: 12px; font-weight: 700; opacity: .85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .hero__slot:focus-visible { outline: 2px solid currentColor; outline-offset: 2px; }
  `],
})
export class NowHero implements OnDestroy {
  /** The shared Today snapshot (page-owned, best-effort). */
  readonly today = input<FamilyToday | null>(null);
  /** The shared morning briefing (page-owned, best-effort). */
  readonly briefing = input<FamilyBriefing | null>(null);

  /** A 1s ticker so the countdown chips stay live without per-card network. */
  private readonly nowMs = signal(Date.now());
  private readonly ticker = setInterval(() => this.nowMs.set(Date.now()), 1000);

  /** COPIED from family-home.ts:114 — do NOT import FamilyHome. */
  private nextEventOf(evs: FamilyTodayEvent[]): FamilyTodayEvent | null {
    if (!evs.length) return null;
    const now = Date.now();
    const upcoming = evs
      .filter(e => !e.allDay && e.startUtc && Date.parse(e.startUtc) >= now)
      .sort((a, b) => (a.startUtc ?? '').localeCompare(b.startUtc ?? ''));
    if (upcoming.length) return upcoming[0];
    return evs.find(e => e.allDay) ?? null;
  }

  readonly event = computed<FamilyTodayEvent | null>(() => this.nextEventOf(this.today()?.events ?? []));

  /** The soonest still-running timer (endsUtc still ahead), if any. */
  readonly timer = computed<FamilyTodayTimer | null>(() => {
    const now = this.nowMs();
    const live = (this.today()?.timers ?? [])
      .filter(t => Date.parse(t.endsUtc) > now)
      .sort((a, b) => a.endsUtc.localeCompare(b.endsUtc));
    return live[0] ?? null;
  });

  readonly narrative = computed(() => this.briefing()?.narrative?.trim() || '');

  /** Up to three of today's still-upcoming events AFTER the hero's headline event (the mini timeline). */
  readonly laterEvents = computed<FamilyTodayEvent[]>(() => {
    const headline = this.event();
    const now = this.nowMs();
    return (this.today()?.events ?? [])
      .filter(e => !e.allDay && e.startUtc && Date.parse(e.startUtc) >= now && e.id !== headline?.id)
      .sort((a, b) => (a.startUtc ?? '').localeCompare(b.startUtc ?? ''))
      .slice(0, 3);
  });

  readonly mode = computed<HeroMode>(() => {
    if (this.event()) return 'event';
    if (this.timer()) return 'timer';
    if (this.narrative()) return 'narrative';
    return 'calm';
  });

  readonly icon = computed(() => {
    switch (this.mode()) {
      case 'event': return 'event';
      case 'timer': return 'timer';
      case 'narrative': return 'auto_awesome';
      default: return 'wb_sunny';
    }
  });

  readonly eyebrow = computed(() => {
    switch (this.mode()) {
      case 'event': return 'Up next';
      case 'timer': return 'Timer running';
      case 'narrative': return 'Today';
      default: return 'All clear';
    }
  });

  /** A friendly "2h 5m" / "8m" / "45s" countdown to the active event start or timer end. */
  readonly countdown = computed<string | null>(() => {
    const now = this.nowMs();
    let target: number | null = null;
    if (this.mode() === 'event') {
      const e = this.event();
      target = e && !e.allDay && e.startUtc ? Date.parse(e.startUtc) : null;
    } else if (this.mode() === 'timer') {
      const t = this.timer();
      target = t ? Date.parse(t.endsUtc) : null;
    }
    if (target == null || Number.isNaN(target)) return null;
    let secs = Math.max(0, Math.round((target - now) / 1000));
    if (secs <= 0) return null;
    const h = Math.floor(secs / 3600); secs -= h * 3600;
    const m = Math.floor(secs / 60); const s = secs - m * 60;
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m`;
    return `${s}s`;
  });

  ngOnDestroy(): void {
    clearInterval(this.ticker);
  }
}
