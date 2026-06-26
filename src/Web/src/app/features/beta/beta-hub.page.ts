import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { AuthService } from '../../core/auth';
import { BetaPullRefresh } from '../beta-ui';
import { BETA_EXPERIMENTS, BetaExperiment, canSeeExperiment } from './beta-experiments';

/** A launcher tile = the shared experiment entry + the destination surface's OWN signature accent. */
interface HubTile extends BetaExperiment {
  /** Signature gradient start/end for this destination (drives the icon chip, glow, edge + arrow). */
  readonly accentA: string;
  readonly accentB: string;
  /** A tighter one-liner for the tile (falls back to the shared blurb). */
  readonly desc: string;
}

/**
 * Each destination's SIGNATURE accent gradient, keyed by route (Strata=multi/violet, Bills=cream,
 * Home=violet, Dashboard=pink, Family=amber, Wrapped=purple, Settings=slate). Kept here so the shared
 * {@link BETA_EXPERIMENTS} source (which the nav also iterates) stays presentation-free. Falls back to
 * the hub's own blue accent for any future entry that lacks a mapping.
 */
const SURFACE_ACCENTS: Record<string, { a: string; b: string; desc?: string }> = {
  '/tracker-beta': { a: '#7c5cff', b: '#3b82f6', desc: 'A clean-sheet, mobile-first fitness tracker.' },     // Strata — multi/violet → blue
  '/beta/bills':   { a: '#f0b760', b: '#fef3c7', desc: 'Snap a receipt, split it, share a claim link.' },   // Bills  — cream
  '/beta/home':    { a: '#8b5cff', b: '#4f7bff', desc: 'Your cross-domain glance: rings, events, presence.' }, // Home  — violet
  '/beta/dashboard': { a: '#fb7185', b: '#f472b6', desc: 'Token + cost analytics, glanceable on mobile.' },  // Dashboard — pink
  '/beta/family':  { a: '#f0a35a', b: '#fbbf24', desc: 'Your whole household at a glance.' },                 // Family — amber
  '/beta/wrapped': { a: '#a855f7', b: '#7c5cff', desc: 'Your Hub, the highlight reel.' },                     // Wrapped — purple
  '/beta/settings': { a: '#64748b', b: '#94a3b8', desc: 'Your quick toggles, mobile-first.' },               // Settings — slate
  '/beta/chat':    { a: '#2dd4bf', b: '#0ea5e9', desc: 'Fast, native-feel chat — bubbles, reactions, typing.' }, // Messenger — teal
  '/beta/ask':     { a: '#818cf8', b: '#6366f1', desc: 'Chat with an AI grounded in your own numbers.' },     // Ask — indigo
  '/beta/meals':   { a: '#34d399', b: '#a3e635', desc: 'Plan the week, swipe the days, fill the cart.' },     // Meals — green
  '/beta/people':  { a: '#fb7185', b: '#f43f5e', desc: 'Your circle, online-first — message or nudge in a tap.' }, // People — rose
  '/beta/fleet':   { a: '#22d3ee', b: '#06b6d4', desc: 'Every machine + reporter: live pulses, spend, board.' },  // Fleet — cyan
  '/beta/trophies': { a: '#fbbf24', b: '#f59e0b', desc: 'Your achievements wall — earned badges gleam.' },    // Trophies — gold
  '/beta/automations': { a: '#fb923c', b: '#ef4444', desc: 'If-this-then-that rules as WHEN → THEN cards.' },  // Automations — orange
};

/**
 * Beta hub — a premium MOBILE LAUNCHER for the experimental surfaces, rebuilt onto the shared beta-ui
 * "Strata" foundation (`@use '../beta-ui/beta-kit'`). An immersive header ("Beta" + tagline + a live
 * count with an accent bloom) sits over a grid of rich entry tiles: each tile carries its destination's
 * OWN signature accent gradient (Strata violet, Bills cream, Home violet, Dashboard pink, Family amber,
 * Wrapped purple, Settings slate) on an icon chip + glow + accent edge, a title, a one-line description,
 * and a subtle "experimental" treatment. Depth (sediment + lift + glow), a staggered spring entrance,
 * press feedback, and pull-to-refresh give it a native-app feel. The HUB owns a BLUE signature accent.
 *
 * Each tile routes to its page via the existing `routerLink`, and the visible list is still filtered by
 * per-card permission so a card only appears if its own feature flag (e.g. tracker.beta) is granted —
 * the gating is UNCHANGED from the flat version.
 *
 * ISOLATED + gated by `beta.access`: nothing here touches the global --tech-* tokens or any live page;
 * the flagship tracker-beta and the kit itself are consumed, never modified.
 */
@Component({
  selector: 'app-beta-hub',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './beta-hub.page.scss',
  imports: [RouterLink, MatIconModule, BetaPullRefresh],
  template: `
    <!-- The scroll column IS the kit pull-to-refresh (it owns overflow + the live accent spinner). -->
    <app-bs-pull-refresh class="bh-ptr" [busy]="refreshing()" (refresh)="refresh()">
      <div class="bh-scroll">

        <!-- Immersive page header — "Beta" + tagline + a live count, with an accent bloom behind it. -->
        <header class="bh-head">
          <div class="bh-head__bloom" aria-hidden="true"></div>
          <div class="bh-head__top">
            <div class="bh-head__text">
              <span class="bh-head__eyebrow"><span class="bh-spark" aria-hidden="true"></span> Experimental</span>
              <h1 class="bh-head__title">Beta</h1>
              <p class="bh-head__tag">Early surfaces we're shaping — they may change, move, or vanish.</p>
            </div>
            @if (tiles().length) {
              <div class="bh-count" aria-hidden="true">
                <span class="bh-count__n">{{ tiles().length }}</span>
                <span class="bh-count__lbl">{{ tiles().length === 1 ? 'lab' : 'labs' }}</span>
              </div>
            }
          </div>
        </header>

        @if (tiles().length) {
          <!-- Launcher grid — staggered spring entrance, each tile carries its own signature accent. -->
          <div class="bh-grid">
            @for (t of tiles(); track t.route; let i = $index) {
              <div class="bh-tile-in" [style.--i]="i">
                <a class="bh-tile" [routerLink]="t.route"
                   [style.--ta]="t.accentA" [style.--tb]="t.accentB"
                   [attr.aria-label]="t.title + ' — ' + t.desc">
                  <div class="bh-tile__top">
                    <span class="bh-tile__icon"><mat-icon aria-hidden="true">{{ t.icon }}</mat-icon></span>
                    <span class="bh-tag"><span class="bh-tag__dot" aria-hidden="true"></span> Beta</span>
                  </div>
                  <div class="bh-tile__body">
                    <span class="bh-tile__title">
                      {{ t.title }}
                      <span class="bh-tile__arrow" aria-hidden="true">→</span>
                    </span>
                    <span class="bh-tile__desc">{{ t.desc }}</span>
                  </div>
                </a>
              </div>
            }
          </div>
        } @else {
          <div class="bh-empty">
            <span class="bh-empty__ic" aria-hidden="true"><mat-icon>science</mat-icon></span>
            <p class="bh-empty__msg">No beta experiments are available to you yet. As features open up,
              they'll land here first.</p>
          </div>
        }
      </div>
    </app-bs-pull-refresh>
  `,
})
export class BetaHubPage {
  private readonly auth = inject(AuthService);

  /** True while a (visual) pull-to-refresh settles — there's no live data here, so it's a brief reflow. */
  readonly refreshing = signal(false);

  /** Tiles visible to the current session (cards without a `perm` always show), each with its accent. */
  readonly tiles = computed<HubTile[]>(() => {
    this.auth.permissions(); // re-run when permissions change
    return BETA_EXPERIMENTS
      .filter(x => canSeeExperiment(x, p => this.auth.hasPermission(p)))
      .map(x => {
        const sig = SURFACE_ACCENTS[x.route];
        return {
          ...x,
          accentA: sig?.a ?? 'var(--accent-a)',
          accentB: sig?.b ?? 'var(--accent-b)',
          desc: sig?.desc ?? x.blurb,
        };
      });
  });

  /**
   * Pull-to-refresh: the hub is a static index (the visible set is derived from the session's permissions),
   * so this just re-asserts the permission read and flips the spinner briefly for the native-feel gesture.
   */
  refresh(): void {
    this.refreshing.set(true);
    this.auth.permissions(); // re-read (cheap); the computed recomputes on any change
    setTimeout(() => this.refreshing.set(false), 450);
  }
}
