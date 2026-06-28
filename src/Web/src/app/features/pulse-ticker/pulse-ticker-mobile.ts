import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { timeAgo } from '../../shared/format';
import { BetaSkeleton } from '../beta-ui';
import { PulseTickerStore, PulseTickerItem } from './pulse-ticker.store';

/**
 * MOBILE "Pulse" ticker — the dashboard-beta twin of {@link PulseTicker}. Same shared {@link PulseTickerStore}
 * (one wire shape + gate + refresh policy for both platforms); only the skin differs. Built on the Strata
 * kit tokens (--accent-a/-b, --ink, --bg-rise, --hairline, the radii) so it inherits the page's pink accent.
 *
 * Renders NOTHING unless the caller can see the feed AND there's ≥1 moment (or first-load / error) — so it
 * never injects an empty card. A subtle "live" dot pulses only while the chat hub is connected; the pulse +
 * the skeleton sheen are suppressed under prefers-reduced-motion (no marquee/auto-scroll exists to guard).
 */
@Component({
  selector: 'app-pulse-ticker-mobile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule, BetaSkeleton],
  template: `
    @if (store.canSee() && show()) {
      <section class="pm" aria-label="Activity pulse">
        <header class="pm__head">
          <span class="pm__title">
            <span class="pm__live" [class.pm__live--on]="store.live()" aria-hidden="true"></span>
            Pulse
          </span>
          <a class="pm__all" routerLink="/feed">See all <mat-icon aria-hidden="true">arrow_forward</mat-icon></a>
        </header>

        @if (store.loading() && !store.hasItems()) {
          <div class="pm__sk">
            @for (i of [1,2,3]; track i) {
              <div class="pm__sk-row">
                <app-bs-skeleton width="34px" height="34px" radius="10px" />
                <app-bs-skeleton width="68%" height="13px" />
              </div>
            }
          </div>
        } @else if (store.error() && !store.hasItems()) {
          <div class="pm__msg">
            <mat-icon aria-hidden="true">error_outline</mat-icon>
            <span>Couldn't load the pulse.</span>
            <button type="button" class="pm__retry" (click)="store.refresh()">Retry</button>
          </div>
        } @else {
          <ul class="pm__list">
            @for (it of store.items(); track it.id) {
              <li class="pm__row">
                <span class="pm__av" [attr.data-kind]="it.kind" aria-hidden="true">
                  <mat-icon>{{ it.icon }}</mat-icon>
                </span>
                <span class="pm__body">
                  <span class="pm__text"><strong>{{ it.actorName }}</strong> {{ it.verb }}</span>
                  <span class="pm__when">{{ relative(it) }}</span>
                </span>
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styles: [`
    :host { display: block; }
    .pm {
      display: flex; flex-direction: column; gap: 10px;
      padding: 14px; border-radius: var(--r-card, 18px);
      background: var(--bg-rise, #161320); border: 1px solid var(--hairline, rgba(255,255,255,.08));
    }
    .pm__head { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
    .pm__title {
      display: inline-flex; align-items: center; gap: 8px;
      font-size: 15px; font-weight: 700; color: var(--ink, #f4f1fb);
    }
    .pm__live {
      width: 8px; height: 8px; border-radius: 50%; flex: 0 0 auto;
      background: var(--ink-dim, #9a93ad);
    }
    .pm__live--on {
      background: var(--accent-a, #f472b6);
      animation: pm-pulse 2.4s ease-out infinite;
    }
    @keyframes pm-pulse {
      0% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent-a, #f472b6) 55%, transparent); }
      70% { box-shadow: 0 0 0 7px color-mix(in srgb, var(--accent-a, #f472b6) 0%, transparent); }
      100% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent-a, #f472b6) 0%, transparent); }
    }
    .pm__all {
      display: inline-flex; align-items: center; gap: 2px;
      font-size: 13px; font-weight: 700; text-decoration: none;
      color: color-mix(in srgb, var(--accent-a, #f472b6) 75%, var(--ink, #fff));
      mat-icon { font-size: 16px; width: 16px; height: 16px; }
    }

    .pm__list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
    .pm__row {
      display: flex; align-items: center; gap: 11px;
      min-height: 48px; padding: 7px 0; border-bottom: 1px solid var(--hairline, rgba(255,255,255,.07));
    }
    .pm__row:last-child { border-bottom: 0; }
    .pm__av {
      display: grid; place-items: center; width: 34px; height: 34px; flex: 0 0 auto;
      border-radius: 10px;
      color: color-mix(in srgb, var(--accent-a, #f472b6) 80%, var(--ink, #fff));
      background: color-mix(in srgb, var(--accent-a, #f472b6) 16%, transparent);
      mat-icon { font-size: 19px; width: 19px; height: 19px; }
    }
    // Per-kind accent flip onto the secondary accent for non-fitness kinds.
    .pm__av[data-kind='hydration.goalHit'] {
      color: color-mix(in srgb, var(--accent-b, #a855f7) 85%, var(--ink, #fff));
      background: color-mix(in srgb, var(--accent-b, #a855f7) 18%, transparent);
    }
    .pm__body { display: flex; flex-direction: column; gap: 1px; min-width: 0; flex: 1 1 auto; }
    .pm__text {
      font-size: 13.5px; color: var(--ink-dim, #b7afc7);
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      strong { color: var(--ink, #f4f1fb); font-weight: 600; }
    }
    .pm__when { font-size: 11.5px; color: var(--ink-dim, #8b83a0); font-variant-numeric: tabular-nums; }

    .pm__sk { display: flex; flex-direction: column; gap: 12px; padding: 2px 0; }
    .pm__sk-row { display: flex; align-items: center; gap: 11px; }

    .pm__msg {
      display: flex; align-items: center; gap: 8px; padding: 4px 0;
      font-size: 13px; color: var(--ink-dim, #b7afc7);
      mat-icon { font-size: 18px; width: 18px; height: 18px; }
    }
    .pm__retry {
      margin-left: auto; border: 1px solid var(--hairline, rgba(255,255,255,.14)); background: transparent;
      color: var(--ink, #f4f1fb); border-radius: var(--r-pill, 12px); padding: 5px 13px;
      font: inherit; font-size: 12.5px; font-weight: 600; cursor: pointer;
    }

    @media (prefers-reduced-motion: reduce) {
      .pm__live--on { animation: none; }
    }
  `],
})
export class PulseTickerMobile {
  readonly store = inject(PulseTickerStore);

  constructor() {
    this.store.attach(inject(DestroyRef));
  }

  readonly show = computed(
    () => this.store.hasItems() || this.store.loading() || this.store.error(),
  );

  relative(it: PulseTickerItem): string {
    return timeAgo(it.createdUtc);
  }
}
