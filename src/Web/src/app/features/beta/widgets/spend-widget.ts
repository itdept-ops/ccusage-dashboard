import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { AuthService } from '../../../core/auth';
import { FinanceSummary, PERM } from '../../../core/models';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

/**
 * Atrium "Spend this month" widget — month total + the top-3 spending categories as bars. Best-effort
 * own subscription to {@link Api.financeSummary} (catch → null). Gated on {@link PERM.familyFinance};
 * the endpoint 403s without it, so the page auto-hides the card when the perm is missing.
 */
@Component({
  selector: 'atr-spend-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell],
  template: `
    <atr-widget-shell
      title="Spend this month" route="/family/finance" accentVar="--atr-spend"
      [phase]="phase()" emptyText="No spending recorded this month."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (summary(); as s) {
        <div body class="sp">
          <span class="sp__total">{{ money(s.totalSpent) }}</span>
          <div class="sp__cats">
            @for (c of topCategories(); track c.category) {
              <div class="sp__cat">
                <span class="sp__cat-name">{{ c.category }}</span>
                <span class="sp__cat-bar"><i [style.width.%]="barPct(c.amount)"></i></span>
                <span class="sp__cat-amt">{{ money(c.amount) }}</span>
              </div>
            }
          </div>
        </div>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .sp { display: flex; flex-direction: column; gap: 12px; }
    .sp__total { font-weight: 700; font-size: 22px; color: var(--atr-ink); }
    .sp__cats { display: flex; flex-direction: column; gap: 8px; }
    .sp__cat { display: grid; grid-template-columns: 90px 1fr auto; align-items: center; gap: 8px; }
    .sp__cat-name { font-size: 12px; color: var(--atr-ink-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .sp__cat-bar { height: 6px; border-radius: 999px; background: rgba(255,255,255,.08); overflow: hidden; }
    .sp__cat-bar > i { display: block; height: 100%; border-radius: 999px; background: var(--atr-spend); }
    .sp__cat-amt { font-size: 12px; color: var(--atr-ink); font-variant-numeric: tabular-nums; }
  `],
})
export class SpendWidget extends ReorderableWidget {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly data = signal<FinanceSummary | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);

  /** Auto-hide unless the user holds family.finance. */
  readonly visible = computed(() => {
    this.auth.permissions();
    return this.auth.hasPermission(PERM.familyFinance);
  });

  readonly summary = this.data.asReadonly();

  /** Top-3 categories by amount desc. */
  readonly topCategories = computed(() =>
    [...(this.data()?.byCategory ?? [])].sort((a, b) => b.amount - a.amount).slice(0, 3));

  private readonly maxAmount = computed(() =>
    Math.max(1, ...this.topCategories().map(c => c.amount)));

  readonly phase = computed<WidgetPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    const s = this.data();
    return s && (s.totalSpent > 0 || s.byCategory.length) ? 'ready' : 'empty';
  });

  barPct(amount: number): number {
    return Math.max(2, Math.round((amount / this.maxAmount()) * 100));
  }

  money(n: number): string {
    return n.toLocaleString(undefined, { style: 'currency', currency: 'USD', maximumFractionDigits: 0 });
  }

  constructor() {
    super();
    this.reload();
  }

  reload(): void {
    // Don't even hit the endpoint without the perm (it would 403); the page hides us anyway.
    if (!this.auth.hasPermission(PERM.familyFinance)) { this.loadingState.set(false); return; }
    this.loadingState.set(true);
    this.failed.set(false);
    this.api.financeSummary()
      .pipe(catchError(() => { this.failed.set(true); return of<FinanceSummary | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(s => {
        if (s) this.data.set(s);
        this.loadingState.set(false);
      });
  }
}
