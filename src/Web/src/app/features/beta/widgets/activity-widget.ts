import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { AuthService } from '../../../core/auth';
import { PERM, RequestLogEntry } from '../../../core/models';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

/**
 * Atrium "Recent activity" widget — the latest 6 request-log rows (method, path, status, who). Best-effort
 * own subscription to {@link Api.requestLogs} (catch → null). Gated on {@link PERM.activityView}; the
 * endpoint is admin-only, so the page auto-hides the card when the perm is missing.
 */
@Component({
  selector: 'atr-activity-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell],
  template: `
    <atr-widget-shell
      title="Recent activity" route="/activity" accentVar="--atr-ink-dim"
      [phase]="phase()" emptyText="No recent requests."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (rows().length) {
        <ul body class="ac">
          @for (r of rows(); track r.id) {
            <li class="ac__row">
              <span class="ac__status" [class.ac__status--err]="r.statusCode >= 400">{{ r.statusCode }}</span>
              <span class="ac__method">{{ r.method }}</span>
              <span class="ac__path">{{ r.path }}</span>
              <span class="ac__who">{{ r.userName || '—' }}</span>
            </li>
          }
        </ul>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .ac { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
    .ac__row { display: grid; grid-template-columns: 36px 44px 1fr auto; align-items: center; gap: 8px; font-size: 12px; }
    .ac__status { font-variant-numeric: tabular-nums; color: var(--atr-online); font-weight: 700; }
    .ac__status--err { color: var(--atr-spend); }
    .ac__method { color: var(--atr-ink-dim); font-weight: 700; }
    .ac__path { color: var(--atr-ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .ac__who { color: var(--atr-ink-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 90px; }
  `],
})
export class ActivityWidget extends ReorderableWidget {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly data = signal<RequestLogEntry[] | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);

  /** Auto-hide unless the user holds activity.view. */
  readonly visible = computed(() => {
    this.auth.permissions();
    return this.auth.hasPermission(PERM.activityView);
  });

  readonly rows = computed<RequestLogEntry[]>(() => this.data() ?? []);

  readonly phase = computed<WidgetPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    return this.rows().length ? 'ready' : 'empty';
  });

  constructor() {
    super();
    this.reload();
  }

  reload(): void {
    if (!this.auth.hasPermission(PERM.activityView)) { this.loadingState.set(false); return; }
    this.loadingState.set(true);
    this.failed.set(false);
    this.api.requestLogs({ take: 6 })
      .pipe(catchError(() => { this.failed.set(true); return of<RequestLogEntry[] | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(list => {
        if (list) this.data.set(list);
        this.loadingState.set(false);
      });
  }
}
