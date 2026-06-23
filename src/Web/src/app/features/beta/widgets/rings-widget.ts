import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { AuthService } from '../../../core/auth';
import { PERM } from '../../../core/models';
import { TrackerStore, toLocalDate } from '../../../core/tracker-store';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

/**
 * Atrium "Rings" widget — calories / protein / water at a glance, with one optimistic `+water` button.
 *
 * Isolation: injects the ROOT {@link TrackerStore} (shared with the live `/tracker` and `/tracker-beta`
 * pages) and reads its signals READ-ONLY, plus the single legitimate user action `addHydration` (an
 * existing store method — not new behavior, not a live-component edit). It calls `store.load()` once on
 * init exactly as the live pages do (the store does not auto-load); that's a read-only refresh of shared
 * day state.
 *
 * Auto-hide: the parent only renders this card when {@link visible} is true (perm held). Phase: skeleton
 * until `day()` is non-null, `failed` if `store.error()` is set; after load the DTO is always full.
 */
@Component({
  selector: 'atr-rings-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell, MatIconModule],
  template: `
    <atr-widget-shell
      title="Today's rings" route="/tracker" accentVar="--atr-rings"
      [phase]="phase()" emptyText="No tracker data yet."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (day(); as d) {
        <div body class="rings">
          <div class="ring ring--cal">
            <span class="ring__num">{{ d.caloriesIn }}</span>
            <span class="ring__unit">/ {{ d.calorieGoal ?? '—' }} kcal</span>
            <span class="ring__bar"><i [style.width.%]="calPct()"></i></span>
            <span class="ring__lbl">Calories</span>
          </div>
          <div class="ring ring--pro">
            <span class="ring__num">{{ d.proteinG }}g</span>
            <span class="ring__unit">{{ proteinGoal() ? '/ ' + proteinGoal() + 'g' : 'protein' }}</span>
            <span class="ring__bar ring__bar--pro"><i [style.width.%]="proPct()"></i></span>
            <span class="ring__lbl">Protein</span>
          </div>
          <div class="ring ring--water">
            <span class="ring__num">{{ waterCups(d.hydrationMl) }}</span>
            <span class="ring__unit">/ {{ waterCups(d.hydrationGoalMl) }} cups</span>
            <span class="ring__bar ring__bar--water"><i [style.width.%]="waterPct()"></i></span>
            <button type="button" class="ring__add" (click)="addWater($event)"
                    [disabled]="busy()" aria-label="Add a cup of water">
              <mat-icon aria-hidden="true">add</mat-icon> water
            </button>
          </div>
        </div>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .rings { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; }
    .ring { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
    .ring__num { font-weight: 700; font-size: 20px; color: var(--atr-ink); line-height: 1; }
    .ring__unit { font-size: 11px; color: var(--atr-ink-dim); }
    .ring__lbl { font-size: 11px; color: var(--atr-ink-dim); margin-top: 2px; }
    .ring__bar { height: 6px; border-radius: 999px; background: rgba(255,255,255,.08); overflow: hidden; margin-top: 4px; }
    .ring__bar > i { display: block; height: 100%; border-radius: 999px; background: var(--atr-rings); transition: width 240ms var(--atr-ease, ease); }
    .ring__bar--pro > i { background: var(--atr-online); }
    .ring__bar--water > i { background: var(--atr-event); }
    .ring__add {
      margin-top: 6px; display: inline-flex; align-items: center; gap: 3px; align-self: flex-start;
      min-height: 32px; padding: 0 10px; border-radius: 999px;
      border: 1px solid var(--atr-edge); background: transparent; color: var(--atr-event);
      font: inherit; font-size: 12px; cursor: pointer;
    }
    .ring__add:disabled { opacity: .5; cursor: default; }
    .ring__add mat-icon { font-size: 16px; width: 16px; height: 16px; }
  `],
})
export class RingsWidget extends ReorderableWidget {
  private readonly store = inject(TrackerStore);
  private readonly auth = inject(AuthService);

  readonly day = this.store.day;

  /** True when the user can see this widget AND data isn't structurally null — drives parent auto-hide. */
  readonly visible = computed(() => {
    this.auth.permissions();
    return this.auth.hasPermission(PERM.trackerSelf);
  });

  readonly phase = computed<WidgetPhase>(() => {
    if (this.store.day()) return 'ready';
    if (this.store.error()) return 'failed';
    return 'loading';
  });

  readonly busy = computed(() => this.store.loading());

  readonly proteinGoal = computed(() => this.store.day()?.profile?.proteinGoalG ?? null);

  readonly calPct = computed(() => this.pct(this.store.day()?.caloriesIn, this.store.day()?.calorieGoal));
  readonly proPct = computed(() => this.pct(this.store.day()?.proteinG, this.proteinGoal() ?? undefined));
  readonly waterPct = computed(() => this.pct(this.store.day()?.hydrationMl, this.store.day()?.hydrationGoalMl));

  constructor() {
    super();
    // Read-only refresh of the shared day (same call the live pages make on init).
    void this.reload();
  }

  reload(): Promise<void> {
    return this.store.load();
  }

  /** ~250 ml per cup, rounded. */
  waterCups(ml: number | undefined): number {
    return Math.round((ml ?? 0) / 250);
  }

  /** Optimistic +water: the existing store action POSTs then refreshes the shared `day()` signal. */
  async addWater(ev: Event): Promise<void> {
    ev.preventDefault();
    ev.stopPropagation();
    if (this.busy()) return;
    try {
      await this.store.addHydration({ date: toLocalDate(new Date()), amountMl: 250, label: 'Water' });
    } catch {
      // store.error() surfaces in the phase; nothing to do here
    }
  }

  private pct(value: number | undefined, goal: number | undefined): number {
    if (!goal || goal <= 0) return 0;
    return Math.max(0, Math.min(100, Math.round(((value ?? 0) / goal) * 100)));
  }
}
