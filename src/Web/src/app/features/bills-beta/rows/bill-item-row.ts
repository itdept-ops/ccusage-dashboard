import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

import { BillItemDto, ChatContactDto } from '../../../core/models';
import { SwipeRow } from '../../tracker-beta/ui/swipe-row';

/** Emitted when the user picks (or clears) who an item is assigned to. `userId: null` = open for claiming. */
export interface AssignChange {
  item: BillItemDto;
  userId: number | null;
}

/**
 * Tally claim-first item row — a 56px+ tappable row wrapped in the imported {@link SwipeRow} (swipe-left
 * to delete). Tapping the row expands an inline contact-avatar strip so the owner assigns the item to a
 * contact (or "Open" to clear) WITHOUT the per-item MatDialog the live page uses. A settle toggle and the
 * amount sit on the right; claimed/assigned items get the green ink, unclaimed stay neutral.
 *
 * Pure presentation + gesture: all writes are emitted to the page, which owns the optimistic patch +
 * reconcile. Inherits the Tally palette tokens from the page `:host`; SwipeRow inherits --r-tile/--warn etc.
 */
@Component({
  selector: 'app-bill-item-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencyPipe, MatIconModule, SwipeRow],
  template: `
    <app-swipe-row [label]="'Delete ' + item().name" (delete)="delete.emit(item())">
      <div class="bir">
        <div class="bir__main" (click)="expanded.set(!expanded())"
             role="button" tabindex="0"
             (keydown.enter)="expanded.set(!expanded())" (keydown.space)="expanded.set(!expanded())"
             [attr.aria-expanded]="expanded()"
             [attr.aria-label]="'Assign ' + item().name">
          <button type="button" class="bir__check" [class.is-on]="item().settled"
                  (click)="$event.stopPropagation(); settle.emit(item())"
                  [attr.aria-label]="(item().settled ? 'Mark unsettled: ' : 'Mark settled: ') + item().name">
            <mat-icon aria-hidden="true">{{ item().settled ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
          </button>

          <div class="bir__body">
            <span class="bir__name" [class.is-settled]="item().settled">{{ item().name }}</span>
            <span class="bir__who" [class.is-claimed]="claimedLabel()">{{ claimedLabel() || 'Tap to claim' }}</span>
          </div>

          <span class="bir__amt">{{ item().amount | currency: 'USD' }}</span>
          <mat-icon class="bir__chev" aria-hidden="true">{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
        </div>

        @if (expanded()) {
          <div class="bir__claimstrip" role="listbox" [attr.aria-label]="'Assign ' + item().name">
            <button type="button" class="bir__avatar bir__avatar--open"
                    [class.is-sel]="!item().assignedToUserId"
                    (click)="pick(null)" role="option" [attr.aria-selected]="!item().assignedToUserId"
                    aria-label="Open — anyone can claim">
              <mat-icon aria-hidden="true">how_to_reg</mat-icon>
            </button>
            @for (c of contacts(); track c.userId) {
              <button type="button" class="bir__avatar"
                      [class.is-sel]="item().assignedToUserId === c.userId"
                      (click)="pick(c.userId)" role="option"
                      [attr.aria-selected]="item().assignedToUserId === c.userId"
                      [attr.aria-label]="'Assign to ' + c.name">
                @if (c.picture) {
                  <img [src]="c.picture" alt="" />
                } @else {
                  <span aria-hidden="true">{{ initials(c.name) }}</span>
                }
              </button>
            }
          </div>
        }
      </div>
    </app-swipe-row>
  `,
  styles: [`
    .bir { background: var(--paper-rise); }
    .bir__main {
      display: flex; align-items: center; gap: 10px;
      min-height: var(--tap); padding: 8px 12px;
      cursor: pointer;
    }
    .bir__check {
      flex: 0 0 auto; width: 40px; height: 40px;
      display: grid; place-items: center;
      border: none; background: transparent; cursor: pointer;
      color: var(--ink-dim);
      &.is-on { color: var(--claimed); }
      mat-icon { font-size: 24px; width: 24px; height: 24px; }
    }
    .bir__body { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .bir__name {
      font: 600 15px/1.2 var(--font-ui); color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      &.is-settled { color: var(--ink-dim); text-decoration: line-through; }
    }
    .bir__who {
      font: 500 12px/1.2 var(--font-ui); color: var(--ink-dim);
      &.is-claimed { color: var(--claimed); }
    }
    .bir__amt { flex: 0 0 auto; font: 700 16px/1 var(--font-num); color: var(--ink); }
    .bir__chev { flex: 0 0 auto; color: var(--ink-dim); font-size: 20px; width: 20px; height: 20px; }

    .bir__claimstrip {
      display: flex; gap: 8px; overflow-x: auto; overflow-y: hidden;
      padding: 4px 12px 12px; scrollbar-width: none;
      -webkit-overflow-scrolling: touch;
      &::-webkit-scrollbar { display: none; }
    }
    .bir__avatar {
      flex: 0 0 auto; width: 44px; height: 44px;
      display: grid; place-items: center;
      border-radius: 50%;
      border: 2px solid var(--rule);
      background: var(--paper-sink);
      color: var(--ink-dim);
      font: 700 13px/1 var(--font-num);
      cursor: pointer; overflow: hidden; padding: 0;
      img { width: 100%; height: 100%; object-fit: cover; }
      mat-icon { font-size: 20px; width: 20px; height: 20px; }
      &.is-sel { border-color: var(--me); color: var(--me); }
    }
    .bir__avatar--open.is-sel { border-color: var(--ink-dim); color: var(--ink); }
  `],
})
export class BillItemRow {
  /** The line item to render. */
  readonly item = input.required<BillItemDto>();
  /** The owner's contacts for the inline claim strip. */
  readonly contacts = input<ChatContactDto[]>([]);

  /** Toggle this item's settled flag. */
  readonly settle = output<BillItemDto>();
  /** Delete this item (swipe-left). */
  readonly delete = output<BillItemDto>();
  /** Assign / clear who owns this item. */
  readonly assign = output<AssignChange>();

  protected readonly expanded = signal(false);

  /** "Assigned to / Claimed by …" label, or empty when open. */
  protected readonly claimedLabel = computed(() => {
    const it = this.item();
    if (it.assignedToName) return 'For ' + it.assignedToName;
    if (it.claimedByName) return 'Claimed by ' + it.claimedByName;
    return '';
  });

  protected pick(userId: number | null): void {
    this.assign.emit({ item: this.item(), userId });
    this.expanded.set(false);
  }

  protected initials(name: string): string {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]!.toUpperCase()).join('') || '?';
  }
}
