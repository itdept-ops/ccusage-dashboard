import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { timeAgo } from '../../shared/format';
import { PulseTickerStore, PulseTickerItem } from './pulse-ticker.store';

/**
 * DESKTOP "Pulse" ticker — a compact, on-brand strip for the dashboard that surfaces the latest ~5 circle
 * moments (actor display-name + a short verb + per-kind icon + relative time), with a subtle "live" dot and
 * a "See all" link to /feed. Reads the shared {@link PulseTickerStore} (one source of truth for desktop +
 * mobile), which stays live off the existing chat hub + a light poll — no backend broadcast added.
 *
 * Renders NOTHING unless the caller can see the feed AND there's ≥1 moment (or we're still on the first
 * load / hit an error) — so it never injects an empty card into the dashboard. Styled with the app-wide
 * `--tech-*` tokens to match the desktop shell. The live dot's keyframe is suppressed under
 * prefers-reduced-motion; there is no marquee/auto-scroll, so nothing else needs guarding.
 */
@Component({
  selector: 'app-pulse-ticker',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule],
  template: `
    @if (store.canSee() && show()) {
      <section class="pt panel" aria-label="Activity pulse">
        <header class="pt__head">
          <span class="pt__title">
            <span class="pt__live" [class.pt__live--on]="store.live()" aria-hidden="true"></span>
            <span>Pulse</span>
            <span class="pt__sub">latest from your circle</span>
          </span>
          <a class="pt__all" routerLink="/feed">See all <mat-icon aria-hidden="true">arrow_forward</mat-icon></a>
        </header>

        @if (store.loading() && !store.hasItems()) {
          <ul class="pt__list" aria-hidden="true">
            @for (i of [1,2,3]; track i) {
              <li class="pt__row pt__row--sk">
                <span class="pt__sk pt__sk--av"></span>
                <span class="pt__sk pt__sk--line"></span>
              </li>
            }
          </ul>
        } @else if (store.error() && !store.hasItems()) {
          <div class="pt__msg">
            <mat-icon aria-hidden="true">error_outline</mat-icon>
            <span>Couldn't load the pulse.</span>
            <button type="button" class="pt__retry" (click)="store.refresh()">Retry</button>
          </div>
        } @else {
          <ul class="pt__list">
            @for (it of store.items(); track it.id) {
              <li class="pt__row">
                <span class="pt__av" [attr.data-kind]="it.kind" aria-hidden="true">
                  <mat-icon>{{ it.icon }}</mat-icon>
                </span>
                <span class="pt__body">
                  <span class="pt__text"><strong>{{ it.actorName }}</strong> {{ it.verb }}</span>
                  <span class="pt__when">{{ relative(it) }}</span>
                </span>
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styleUrl: './pulse-ticker.scss',
})
export class PulseTicker {
  readonly store = inject(PulseTickerStore);

  constructor() {
    this.store.attach(inject(DestroyRef));
  }

  /** Show the card while loading-first, on a first-load error, or once we have moments — never an empty card. */
  readonly show = computed(
    () => this.store.hasItems() || this.store.loading() || this.store.error(),
  );

  relative(it: PulseTickerItem): string {
    return timeAgo(it.createdUtc);
  }
}
