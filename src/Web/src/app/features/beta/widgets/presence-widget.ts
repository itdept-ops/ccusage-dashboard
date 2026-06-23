import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { Presence } from '../../../core/models';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

const ONLINE_WINDOW_MS = 5 * 60_000;

/**
 * Atrium "Who's online" widget — an avatar row of teammates seen in the last 5 minutes. Best-effort own
 * subscription to {@link Api.presence} (catch → null). Available to any signed-in user, so no perm gate
 * — but the call can still fail, hence the failed/retry state.
 *
 * `initials` is a small COPIED helper (no live import); presence rows carry only name + picture + a
 * privacy-safe lastSeen, never an email.
 */
@Component({
  selector: 'atr-presence-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell],
  template: `
    <atr-widget-shell
      title="Who's online" route="/users" accentVar="--atr-online"
      [phase]="phase()" emptyText="No one else is online right now."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (online().length) {
        <div body class="pr">
          @for (p of online(); track p.name) {
            <span class="pr__chip" [title]="p.name + (p.isSelf ? ' (you)' : '')">
              @if (p.picture) {
                <img class="pr__img" [src]="p.picture" [alt]="p.name" referrerpolicy="no-referrer" />
              } @else {
                <span class="pr__init" aria-hidden="true">{{ initials(p.name) }}</span>
              }
              <span class="pr__dot" aria-hidden="true"></span>
            </span>
          }
          <span class="pr__count">{{ online().length }} online</span>
        </div>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .pr { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .pr__chip { position: relative; width: 38px; height: 38px; flex: 0 0 auto; }
    .pr__img, .pr__init {
      width: 38px; height: 38px; border-radius: 999px; object-fit: cover; display: grid; place-items: center;
      border: 1px solid var(--atr-edge);
    }
    .pr__init { background: rgba(255,255,255,.08); color: var(--atr-ink); font-size: 13px; font-weight: 700; }
    .pr__dot {
      position: absolute; right: -1px; bottom: -1px; width: 11px; height: 11px; border-radius: 999px;
      background: var(--atr-online); border: 2px solid var(--atr-card);
    }
    .pr__count { font-size: 12px; color: var(--atr-ink-dim); margin-left: 2px; }
  `],
})
export class PresenceWidget extends ReorderableWidget {
  private readonly api = inject(Api);
  private readonly destroyRef = inject(DestroyRef);

  private readonly people = signal<Presence[] | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);

  /** No perm gate — but still auto-hidden by the page when loaded-and-empty (no one online). */
  readonly visible = computed(() => true);

  readonly online = computed<Presence[]>(() => {
    const now = Date.now();
    return (this.people() ?? []).filter(p => now - Date.parse(p.lastSeenUtc) < ONLINE_WINDOW_MS);
  });

  readonly phase = computed<WidgetPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    return this.online().length ? 'ready' : 'empty';
  });

  /** COPIED helper — first letters of up to two name words. */
  initials(name: string): string {
    return name.trim().split(/\s+/).slice(0, 2).map(w => w[0]?.toUpperCase() ?? '').join('') || '?';
  }

  constructor() {
    super();
    this.reload();
  }

  reload(): void {
    this.loadingState.set(true);
    this.failed.set(false);
    this.api.presence()
      .pipe(catchError(() => { this.failed.set(true); return of<Presence[] | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(list => {
        if (list) this.people.set(list);
        this.loadingState.set(false);
      });
  }
}
