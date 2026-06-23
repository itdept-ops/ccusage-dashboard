import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { FamilyToday, FamilyTodayEvent } from '../../../core/models';
import { AtriumWidgetShell, WidgetPhase } from './widget-shell';
import { ReorderableWidget } from './reorderable';

/**
 * Atrium "Next event" widget — the caller's soonest upcoming calendar event today. Best-effort: it owns
 * its own cold {@link Api.familyToday} subscription with `catchError(of(null))` + `takeUntilDestroyed`
 * (the family-home.ts:148 pattern), so a calendar/network failure only blanks THIS card.
 *
 * The `nextEvent` reducer is COPIED verbatim from family-home.ts:114 (not imported) to keep this widget
 * fully decoupled from the live page's internals.
 */
@Component({
  selector: 'atr-event-widget',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AtriumWidgetShell],
  template: `
    <atr-widget-shell
      title="Next event" route="/family/calendar" accentVar="--atr-event"
      [phase]="phase()" emptyText="Nothing else on the calendar today."
      [reordering]="reordering()"
      (retry)="reload()" (moveUp)="moveUp.emit()" (moveDown)="moveDown.emit()" (hide)="hide.emit()">

      @if (next(); as e) {
        <div body class="ev">
          <span class="ev__title">{{ e.title }}</span>
          <span class="ev__time">{{ e.allDay ? 'All day' : e.localTime }}</span>
        </div>
      }
    </atr-widget-shell>
  `,
  styles: [`
    .ev { display: flex; flex-direction: column; gap: 4px; }
    .ev__title { font-weight: 700; font-size: 17px; color: var(--atr-ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .ev__time { font-size: 13px; color: var(--atr-event); }
  `],
})
export class EventWidget extends ReorderableWidget {
  private readonly api = inject(Api);
  private readonly destroyRef = inject(DestroyRef);

  private readonly today = signal<FamilyToday | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);

  /** Always visible to any signed-in user — no perm gate (presence/today is broadly readable). */
  readonly visible = computed(() => true);

  /** COPIED from family-home.ts:114 — do NOT import FamilyHome. */
  private nextEventOf(evs: FamilyTodayEvent[]): FamilyTodayEvent | null {
    if (!evs.length) return null;
    const now = Date.now();
    const upcoming = evs
      .filter(e => !e.allDay && e.startUtc && Date.parse(e.startUtc) >= now)
      .sort((a, b) => (a.startUtc ?? '').localeCompare(b.startUtc ?? ''));
    if (upcoming.length) return upcoming[0];
    return evs.find(e => e.allDay) ?? null;
  }

  readonly next = computed<FamilyTodayEvent | null>(() => this.nextEventOf(this.today()?.events ?? []));

  readonly phase = computed<WidgetPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    return this.next() ? 'ready' : 'empty';
  });

  constructor() {
    super();
    this.reload();
  }

  reload(): void {
    this.loadingState.set(true);
    this.failed.set(false);
    this.api.familyToday()
      .pipe(catchError(() => { this.failed.set(true); return of<FamilyToday | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(t => {
        if (t) this.today.set(t);
        this.loadingState.set(false);
      });
  }
}
