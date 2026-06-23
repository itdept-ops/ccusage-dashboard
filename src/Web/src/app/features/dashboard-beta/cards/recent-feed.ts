import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

import { PagedResult, UsageRecord } from '../../../core/models';
import { CompactPipe } from '../../../shared/format';

/**
 * The RECENT feed — a vertical two-line list of {@link UsageRecord}s for the current filter, fetched
 * via the SAME `Api.records` paging the live dashboard uses (page/pageSize/sort/desc). Infinite scroll:
 * a "Load more" affordance emits `more` when there are further pages. Rows are 44px+ touch targets.
 */
@Component({
  selector: 'app-pulse-recent',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, CompactPipe],
  template: `
    <div class="rf">
      <header class="rf__head">
        <h2 class="rf__title">Recent</h2>
        @if (page()?.total) {
          <span class="rf__count">{{ page()!.total | compact }} total</span>
        }
      </header>

      @if (items().length) {
        <ul class="rf__list">
          @for (r of items(); track r.id) {
            <li class="rec">
              <span class="rec__main">
                <span class="rec__model">{{ r.model || 'unknown' }}</span>
                <span class="rec__cost">\${{ fmtCost(r.costUsd) }}</span>
              </span>
              <span class="rec__meta">
                <span class="rec__when">{{ r.timestampUtc | date:'MMM d, h:mm a' }}</span>
                <span class="rec__dot" aria-hidden="true">·</span>
                <span class="rec__proj">{{ r.projectName || r.source }}</span>
                @if (r.isSidechain) { <span class="rec__tag">subagent</span> }
                <span class="rec__tok">{{ r.totalTokens | compact }} tok</span>
              </span>
            </li>
          }
        </ul>

        @if (hasMore()) {
          <button type="button" class="rf__more" [disabled]="loadingMore()" (click)="more.emit()">
            {{ loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        }
      } @else {
        <p class="rf__empty">{{ loading() ? 'Loading…' : 'No records in this range' }}</p>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .rf { display: flex; flex-direction: column; gap: 12px; }
    .rf__head { display: flex; align-items: baseline; justify-content: space-between; gap: 10px; }
    .rf__title { margin: 0; font-size: 16px; font-weight: 700; color: var(--pulse-ink); }
    .rf__count { font-size: 12px; color: var(--pulse-ink-dim); }

    .rf__list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
    .rec {
      display: flex; flex-direction: column; gap: 3px; min-height: 56px; justify-content: center;
      padding: 10px 0; border-bottom: 1px solid var(--pulse-edge);
    }
    .rec:last-child { border-bottom: 0; }
    .rec__main { display: flex; align-items: baseline; justify-content: space-between; gap: 10px; }
    .rec__model { font-size: 14px; font-weight: 600; color: var(--pulse-ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .rec__cost { font-size: 14px; font-weight: 700; color: var(--cost-a); font-variant-numeric: tabular-nums; flex: 0 0 auto; }
    .rec__meta { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; font-size: 12px; color: var(--pulse-ink-dim); }
    .rec__dot { opacity: .6; }
    .rec__proj { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 45%; }
    .rec__tok { margin-left: auto; font-variant-numeric: tabular-nums; }
    .rec__tag {
      font-size: 10px; font-weight: 600; letter-spacing: .03em; padding: 1px 7px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--tok-b) 22%, transparent); color: var(--tok-b);
    }

    .rf__more {
      min-height: 48px; border-radius: var(--r-pill); border: 1px solid var(--pulse-edge);
      background: var(--pulse-rise); color: var(--pulse-ink); font: inherit; font-size: 14px; font-weight: 600;
      cursor: pointer; margin-top: 4px;
    }
    .rf__more:disabled { opacity: .6; }
    .rf__empty { margin: 8px 0; color: var(--pulse-ink-dim); font-size: 14px; }
  `],
})
export class PulseRecentFeed {
  readonly page = input<PagedResult<UsageRecord> | null>(null);
  readonly loading = input<boolean>(false);
  readonly loadingMore = input<boolean>(false);

  /** Emitted to ask the page for the next page (it appends + re-renders). */
  readonly more = output<void>();

  readonly items = computed(() => this.page()?.items ?? []);

  readonly hasMore = computed(() => {
    const p = this.page();
    if (!p) return false;
    return p.page < Math.ceil(p.total / p.pageSize);
  });

  fmtCost(c: number): string {
    return c.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }
}
