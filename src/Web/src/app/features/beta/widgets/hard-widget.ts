import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { AuthService } from '../../../core/auth';
import { ChallengeStore } from '../../../core/challenge-store';
import { PERM } from '../../../core/models';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

/**
 * Atrium "75 Hard" widget — current day / total, today's points, streak, and a compact day-progress pip
 * row. Injects the ROOT {@link ChallengeStore} (shared with the live `/challenge` page), reads it
 * READ-ONLY, and calls `store.load()` once on init (the store does not auto-load).
 *
 * Auto-hide: {@link visible} is false when the perm is missing OR a load has completed with no active
 * challenge (`loaded() && challenge() === null`) — the brief's "hidden if no challenge".
 */
@Component({
  selector: 'atr-hard-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell],
  template: `
    <atr-widget-shell
      title="75 Hard" route="/challenge" accentVar="--atr-hard"
      [phase]="phase()" emptyText="No active challenge."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (challenge(); as c) {
        <div body class="hard">
          <div class="hard__row">
            <span class="hard__day">Day {{ c.currentDay }}<span class="hard__of">/ {{ c.totalDays }}</span></span>
            <span class="hard__stat">{{ c.todayPoints }} pts today</span>
            <span class="hard__stat hard__stat--streak">{{ c.currentStreak }}🔥</span>
          </div>
          <div class="hard__pips" role="img" [attr.aria-label]="c.currentDay + ' of ' + c.totalDays + ' days'">
            @for (p of pips(); track p.i) {
              <span class="hard__pip" [class.hard__pip--on]="p.done" [class.hard__pip--today]="p.today"></span>
            }
          </div>
        </div>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .hard { display: flex; flex-direction: column; gap: 12px; }
    .hard__row { display: flex; align-items: baseline; gap: 12px; }
    .hard__day { font-weight: 700; font-size: 20px; color: var(--atr-ink); }
    .hard__of { font-size: 13px; color: var(--atr-ink-dim); font-weight: 600; margin-left: 3px; }
    .hard__stat { font-size: 12px; color: var(--atr-ink-dim); }
    .hard__stat--streak { margin-left: auto; }
    .hard__pips { display: flex; flex-wrap: wrap; gap: 4px; }
    .hard__pip {
      width: 8px; height: 8px; border-radius: 3px;
      background: rgba(255,255,255,.10);
    }
    .hard__pip--on { background: var(--atr-hard); }
    .hard__pip--today { box-shadow: 0 0 0 2px color-mix(in srgb, var(--atr-hard) 50%, transparent); }
  `],
})
export class HardWidget extends ReorderableWidget {
  private readonly store = inject(ChallengeStore);
  private readonly auth = inject(AuthService);

  readonly challenge = this.store.challenge;

  /** Auto-hide gate: needs the perm AND (still loading OR an active challenge). Hidden once loaded-empty. */
  readonly visible = computed(() => {
    this.auth.permissions();
    if (!this.auth.hasPermission(PERM.trackerSelf)) return false;
    // Keep the card while loading or on error (so the skeleton/retry shows); only hide when we KNOW
    // there's no challenge.
    return !(this.store.loaded() && this.store.challenge() === null && !this.store.error());
  });

  readonly phase = computed<WidgetPhase>(() => {
    if (this.store.challenge()) return 'ready';
    if (this.store.error()) return 'failed';
    if (!this.store.loaded()) return 'loading';
    return 'empty'; // loaded, no challenge — but the page hides us via visible() anyway
  });

  /** One pip per day; filled up to (and including) the current day, with the current day marked. */
  readonly pips = computed(() => {
    const c = this.store.challenge();
    if (!c) return [];
    const total = Math.max(0, c.totalDays);
    return Array.from({ length: total }, (_, i) => {
      const dayNum = i + 1;
      return { i, done: dayNum < c.currentDay, today: dayNum === c.currentDay };
    });
  });

  constructor() {
    super();
    void this.reload();
  }

  reload(): Promise<void> {
    return this.store.load();
  }
}
