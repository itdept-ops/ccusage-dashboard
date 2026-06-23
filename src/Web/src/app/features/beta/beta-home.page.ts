import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal, viewChildren } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { ChallengeStore } from '../../core/challenge-store';
import { FamilyToday, PERM } from '../../core/models';
import { TrackerStore } from '../../core/tracker-store';

import { AtriumLayoutStore, AtriumWidgetId } from './widgets/layout-store';
import { PullToRefreshDirective } from './widgets/pull-to-refresh';
import { RingsWidget } from './widgets/rings-widget';
import { HardWidget } from './widgets/hard-widget';
import { EventWidget } from './widgets/event-widget';
import { PresenceWidget } from './widgets/presence-widget';
import { SpendWidget } from './widgets/spend-widget';
import { ActivityWidget } from './widgets/activity-widget';

/**
 * Home "Atrium" — a NEW, beta-only cross-domain glance surface. One widget per domain the user actually
 * has (rings, 75-Hard, next event, who's online, spend, recent activity) on a single thumb-scroll column.
 *
 * HARD ISOLATION: this is additive. It reuses root stores ({@link TrackerStore}/{@link ChallengeStore})
 * and {@link Api} READ-ONLY (+ the one existing `addHydration` action) and defines its OWN `--atr-*`
 * command-center tokens on `:host` (see beta-home.page.scss) — never the global `--tech-*`. No live page
 * or component is modified.
 *
 * RESILIENCE: every widget loads best-effort in parallel — store-backed widgets fire `store.load()` in
 * their own constructors; Api-backed widgets own a `catchError(of(null))` subscription each. Each widget
 * renders its own skeleton/empty/failed state, and a widget AUTO-HIDES when its perm is missing (the
 * page's per-id gates below) so one dead domain never blanks the page. Pull-to-refresh re-runs all loads.
 */
@Component({
  selector: 'app-beta-home',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './beta-home.page.scss',
  imports: [
    RouterLink, MatIconModule, PullToRefreshDirective,
    RingsWidget, HardWidget, EventWidget, PresenceWidget, SpendWidget, ActivityWidget,
  ],
  template: `
    <!-- Fixed glass top bar: greeting + date + reorder/settings gear. -->
    <header class="atr-bar">
      <div class="atr-bar__text">
        <span class="atr-bar__greet">{{ greeting() || 'Welcome back' }}</span>
        @if (dateLabel(); as dl) { <span class="atr-bar__date">{{ dl }}</span> }
      </div>
      <div class="atr-bar__actions">
        @if (layout.reordering()) {
          <button type="button" class="atr-bar__btn atr-bar__btn--primary" (click)="layout.setReorder(false)">Done</button>
        } @else {
          <button type="button" class="atr-bar__btn" (click)="layout.toggleReorder()" aria-label="Rearrange widgets">
            <mat-icon aria-hidden="true">tune</mat-icon>
          </button>
          <a class="atr-bar__btn" routerLink="/settings" aria-label="Settings">
            <mat-icon aria-hidden="true">settings</mat-icon>
          </a>
        }
      </div>
    </header>

    <!-- Thumb-scroll column. Pull-to-refresh re-runs all widget loads. -->
    <main class="atr-scroll" (atrPullRefresh)="refreshAll()">
      @if (layout.reordering()) {
        <p class="atr-hint">Reorder mode — use the arrows to move cards, the eye to hide.
          @if (hiddenCount()) { <button type="button" class="atr-hint__reset" (click)="layout.reset()">Reset all</button> }
        </p>
      }

      @for (id of layout.visibleOrder(); track id) {
        @if (gate(id)) {
          @switch (id) {
            @case ('rings') {
              <atr-rings-widget [reordering]="layout.reordering()"
                (moveUp)="layout.moveUp('rings')" (moveDown)="layout.moveDown('rings')" (hide)="layout.toggle('rings')" />
            }
            @case ('hard') {
              <atr-hard-widget [reordering]="layout.reordering()"
                (moveUp)="layout.moveUp('hard')" (moveDown)="layout.moveDown('hard')" (hide)="layout.toggle('hard')" />
            }
            @case ('event') {
              <div class="atr-defer">
                <atr-event-widget [reordering]="layout.reordering()"
                  (moveUp)="layout.moveUp('event')" (moveDown)="layout.moveDown('event')" (hide)="layout.toggle('event')" />
              </div>
            }
            @case ('presence') {
              <div class="atr-defer">
                <atr-presence-widget [reordering]="layout.reordering()"
                  (moveUp)="layout.moveUp('presence')" (moveDown)="layout.moveDown('presence')" (hide)="layout.toggle('presence')" />
              </div>
            }
            @case ('spend') {
              <div class="atr-defer">
                <atr-spend-widget [reordering]="layout.reordering()"
                  (moveUp)="layout.moveUp('spend')" (moveDown)="layout.moveDown('spend')" (hide)="layout.toggle('spend')" />
              </div>
            }
            @case ('activity') {
              <div class="atr-defer">
                <atr-activity-widget [reordering]="layout.reordering()"
                  (moveUp)="layout.moveUp('activity')" (moveDown)="layout.moveDown('activity')" (hide)="layout.toggle('activity')" />
              </div>
            }
          }
        }
      }

      @if (!anyVisible()) {
        <p class="atr-empty">No widgets to show. Open the rearrange menu to turn some back on, or grab more
          permissions to unlock domains.</p>
      }

      <a class="atr-quicklog" routerLink="/tracker">
        <mat-icon aria-hidden="true">add</mat-icon> Quick log
      </a>
    </main>
  `,
})
export class BetaHomePage {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly tracker = inject(TrackerStore);
  private readonly challenge = inject(ChallengeStore);
  private readonly destroyRef = inject(DestroyRef);

  /** Layout (order + on/off + reorder mode) is provided at the route — beta-only, never global. */
  readonly layout = inject(AtriumLayoutStore);

  /** The Api-backed children that own their own fetch; pull-to-refresh re-runs their `reload()`. */
  private readonly eventWidgets = viewChildren(EventWidget);
  private readonly presenceWidgets = viewChildren(PresenceWidget);
  private readonly spendWidgets = viewChildren(SpendWidget);
  private readonly activityWidgets = viewChildren(ActivityWidget);

  private readonly today = signal<FamilyToday | null>(null);
  readonly greeting = computed(() => this.today()?.greeting ?? '');

  /** Friendly "Thursday, June 23" — COPIED from family-home.ts:127 (not imported). */
  readonly dateLabel = computed<string>(() => {
    const iso = this.today()?.dateLocal;
    if (!iso) return '';
    const d = new Date(`${iso}T00:00:00`);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
  });

  /** Per-widget permission gate (the page's auto-hide). Re-runs on permission change. The data-null
   *  auto-hide (e.g. no challenge, no one online) is handled inside each widget's own phase/visible. */
  gate(id: AtriumWidgetId): boolean {
    this.auth.permissions(); // re-run on change
    switch (id) {
      case 'rings': return this.auth.hasPermission(PERM.trackerSelf);
      case 'hard': return this.auth.hasPermission(PERM.trackerSelf);
      case 'event': return true;     // family/today is broadly readable; widget self-empties otherwise
      case 'presence': return true;  // presence is available to any signed-in user
      case 'spend': return this.auth.hasPermission(PERM.familyFinance);
      case 'activity': return this.auth.hasPermission(PERM.activityView);
      default: return false;
    }
  }

  readonly hiddenCount = computed(() => this.layout.hidden().size);

  /** True if at least one ordered+enabled widget also passes its perm gate. */
  readonly anyVisible = computed(() => {
    this.auth.permissions();
    return this.layout.visibleOrder().some(id => this.gate(id));
  });

  constructor() {
    // Top-bar greeting/date — best-effort, never blocks the column.
    this.loadToday();
    // Store-backed widgets each call load() in their own constructor; nothing else to kick off here.
  }

  private loadToday(): void {
    this.api.familyToday()
      .pipe(catchError(() => of<FamilyToday | null>(null)), takeUntilDestroyed(this.destroyRef))
      .subscribe(t => { if (t) this.today.set(t); });
  }

  /**
   * Pull-to-refresh: re-run the store loads (rings/hard read the shared signals) and re-fetch the
   * top-bar. Api-backed widgets re-subscribe via their own retry; here we refresh the shared sources and
   * the greeting. Best-effort and parallel — a failure in one source never aborts the others.
   */
  async refreshAll(): Promise<void> {
    this.loadToday();
    // Api-backed children re-fetch via their own reload (each catches its own error).
    this.eventWidgets().forEach(w => w.reload());
    this.presenceWidgets().forEach(w => w.reload());
    this.spendWidgets().forEach(w => w.reload());
    this.activityWidgets().forEach(w => w.reload());
    // Store-backed widgets read the shared signals these refresh.
    await Promise.allSettled([
      this.tracker.load(),
      this.challenge.load(),
    ]);
  }
}
