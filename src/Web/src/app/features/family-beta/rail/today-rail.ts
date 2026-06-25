import { ChangeDetectionStrategy, Component, OnDestroy, computed, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { FamilyToday } from '../../../core/models';

/** A unified pill for the horizontal today rail. */
interface RailPill {
  key: string;
  kind: 'reminder' | 'timer';
  icon: string;
  label: string;
  /** Pre-formatted time / live countdown line. */
  meta: string;
  route: string;
  /** Sort weight — soonest/most-urgent first. */
  at: number;
}

/**
 * The horizontal "today rail" — a scroll-snapping row of reminder + timer pills, urgency-first (soonest
 * due/ending leads), rebuilt on the shared beta-ui token contract. Each pill is a glass-edged sediment
 * chip; timers show a live countdown, reminders show their pre-formatted local time + target NAME (never
 * an email — the DTO carries no email). `today` is page-owned (best-effort); the rail renders nothing when
 * there is nothing due, so it never reserves dead space.
 */
@Component({
  selector: 'fb-today-rail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule],
  template: `
    @if (pills().length) {
      <div class="rail" role="list" aria-label="Today">
        @for (p of pills(); track p.key) {
          <a class="pill" [class.pill--timer]="p.kind === 'timer'" role="listitem"
             [routerLink]="p.route" [attr.aria-label]="p.label + ' — ' + p.meta">
            <span class="pill__ic" aria-hidden="true"><mat-icon>{{ p.icon }}</mat-icon></span>
            <span class="pill__text">
              <span class="pill__label">{{ p.label }}</span>
              <span class="pill__meta">{{ p.meta }}</span>
            </span>
          </a>
        }
      </div>
    }
  `,
  styles: [`
    :host { --reminder-h: #fcd34d; --timer-h: #6ee7b7; }
    .rail {
      display: flex; gap: 10px; overflow-x: auto;
      scroll-snap-type: x proximity; -webkit-overflow-scrolling: touch;
      padding: 2px 2px 6px; margin: 0 -2px;
      scrollbar-width: none;
    }
    .rail::-webkit-scrollbar { display: none; }
    .pill {
      flex: 0 0 auto; scroll-snap-align: start;
      display: inline-flex; align-items: center; gap: 11px;
      min-height: 58px; max-width: 80vw; padding: 0 16px 0 12px;
      border-radius: var(--r-pill);
      background: var(--bg-rise); box-shadow: var(--lift-1);
      border: 1px solid var(--hairline);
      color: var(--ink); text-decoration: none;
      transition: transform 120ms var(--ease-out);
    }
    .pill:active { transform: scale(.98); }
    .pill:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .pill__ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 36px; height: 36px; border-radius: 11px;
      background: color-mix(in srgb, var(--reminder-h) 20%, transparent);
    }
    .pill__ic mat-icon { color: var(--reminder-h); font-size: 20px; width: 20px; height: 20px; }
    .pill--timer .pill__ic { background: color-mix(in srgb, var(--timer-h) 20%, transparent); }
    .pill--timer .pill__ic mat-icon { color: var(--timer-h); }
    .pill__text { display: flex; flex-direction: column; min-width: 0; }
    .pill__label {
      font-size: 14px; font-weight: 700; line-height: 1.2;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .pill__meta { font-size: 12px; color: var(--ink-dim); font-variant-numeric: tabular-nums; }
  `],
})
export class TodayRail implements OnDestroy {
  /** The shared Today snapshot (page-owned, best-effort). */
  readonly today = input<FamilyToday | null>(null);

  private readonly nowMs = signal(Date.now());
  private readonly ticker = setInterval(() => this.nowMs.set(Date.now()), 1000);

  readonly pills = computed<RailPill[]>(() => {
    const t = this.today();
    if (!t) return [];
    const now = this.nowMs();

    const reminders: RailPill[] = (t.reminders ?? []).map(r => ({
      key: `r${r.id}`,
      kind: 'reminder' as const,
      icon: 'notifications_active',
      label: r.text,
      meta: r.targetName ? `${r.localTime} · ${r.targetName}` : r.localTime,
      route: '/family/reminders',
      at: Date.parse(r.dueUtc) || Number.MAX_SAFE_INTEGER,
    }));

    const timers: RailPill[] = (t.timers ?? [])
      .filter(tm => Date.parse(tm.endsUtc) > now)
      .map(tm => ({
        key: `t${tm.id}`,
        kind: 'timer' as const,
        icon: 'timer',
        label: tm.label || 'Timer',
        meta: this.fmtLeft(Date.parse(tm.endsUtc) - now),
        route: '/family/timer',
        at: Date.parse(tm.endsUtc),
      }));

    return [...reminders, ...timers].sort((a, b) => a.at - b.at);
  });

  /** "2h 5m left" / "8m left" / "45s left" for a remaining-ms duration. */
  private fmtLeft(ms: number): string {
    let secs = Math.max(0, Math.round(ms / 1000));
    const h = Math.floor(secs / 3600); secs -= h * 3600;
    const m = Math.floor(secs / 60); const s = secs - m * 60;
    if (h > 0) return `${h}h ${m}m left`;
    if (m > 0) return `${m}m left`;
    return `${s}s left`;
  }

  ngOnDestroy(): void {
    clearInterval(this.ticker);
  }
}
