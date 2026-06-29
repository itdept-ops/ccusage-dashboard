import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { LeaderboardMetric, LeaderboardRowDto } from '../../../core/models';
import { HearthShell, HearthPhase } from './hearth-shell';

/** The three rankable metrics, with the humane label + glyph the segmented switch uses. */
const METRICS: readonly { key: LeaderboardMetric; label: string; icon: string; unit: string }[] = [
  { key: 'workout', label: 'Workouts', icon: 'fitness_center', unit: 'logged' },
  { key: 'challenge', label: '75 Hard', icon: 'military_tech', unit: 'days' },
  { key: 'hydration', label: 'Water', icon: 'local_drink', unit: 'goals' },
];

/**
 * Hearth "Leaderboard" glance card — the MOBILE TWIN of the Family-Hub leaderboard panel. Ranks the caller's
 * OWN household members over a switchable SHAREABLE activity metric via {@link Api.familyLeaderboard}
 * (GET /api/family/leaderboard?metric=, gated family.use). Owns its own network (like the Chores/Household
 * cards) with the shared skeleton/empty/failed lifecycle from {@link HearthShell}.
 *
 * PRIVACY: rows are id + DisplayName only (never an email); the ranked figure is a COUNT of already-shareable
 * ActivityEvents — NEVER a private tracker amount or any health figure.
 */
@Component({
  selector: 'fb-leaderboard-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HearthShell, MatIconModule],
  styleUrl: './leaderboard-card.scss',
  template: `
    <fb-hearth-shell
      title="Leaderboard" icon="leaderboard"
      accentA="#fbbf24" accentB="#fb7185"
      [phase]="phase()" emptyText="No shareable activity yet." emptyIcon="leaderboard"
      (retry)="load()">

      <div head-trailing class="lb-switch">
        @for (m of metrics; track m.key) {
          <button type="button" class="lb-switch__tab" [class.is-on]="metric() === m.key"
                  [attr.aria-label]="m.label" (click)="setMetric(m.key)">
            <mat-icon aria-hidden="true">{{ m.icon }}</mat-icon>
          </button>
        }
      </div>

      @if (phase() === 'ready') {
        <ol body class="lb">
          @for (r of rows(); track r.userId) {
            <li class="lb__row" [class.is-podium]="r.rank <= 3">
              <span class="lb__rank" [attr.data-rank]="r.rank">{{ r.rank }}</span>
              <span class="lb__avatar" aria-hidden="true">{{ initials(r.name) }}</span>
              <span class="lb__name">{{ r.name }}</span>
              <span class="lb__value">{{ r.intValue }} <span class="lb__unit">{{ unit() }}</span></span>
            </li>
          }
        </ol>
      }
    </fb-hearth-shell>
  `,
})
export class LeaderboardCard {
  private api = inject(Api);

  readonly metrics = METRICS;
  readonly metric = signal<LeaderboardMetric>('workout');
  readonly rows = signal<LeaderboardRowDto[]>([]);
  readonly loading = signal(true);
  readonly failed = signal(false);

  readonly unit = computed(() => this.metrics.find((m) => m.key === this.metric())?.unit ?? '');

  /** The card's lifecycle phase for the HearthShell scaffold. */
  readonly phase = computed<HearthPhase>(() => {
    if (this.loading()) return 'loading';
    if (this.failed()) return 'failed';
    return this.rows().length ? 'ready' : 'empty';
  });

  constructor() {
    this.load();
  }

  setMetric(m: LeaderboardMetric): void {
    if (m === this.metric()) return;
    this.metric.set(m);
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.api
      .familyLeaderboard(this.metric())
      .pipe(catchError(() => { this.failed.set(true); return of<LeaderboardRowDto[] | null>(null); }))
      .subscribe((rows) => {
        if (rows) this.rows.set(rows);
        this.loading.set(false);
      });
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }
}
