import {
  ChangeDetectionStrategy, Component, computed, inject, input,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';

import { FamilyToday, FamilyTodayList } from '../../../core/models';
import { ToastController } from '../../beta-ui';
import { HearthShell, HearthPhase } from './hearth-shell';
import { OptimisticFamily } from '../state/optimistic-family';

/**
 * Hearth "Lists" glance card — rebuilt on the shared beta-ui foundation. Each shopping / to-do list with
 * its open-count pill and the first couple of still-open items as a peek, plus a one-tap add-item box per
 * list. The glance data comes from the page-owned `today` snapshot (`today.lists` carries openCount +
 * firstFewOpenItems); add-item posts via the existing fast-action endpoint through {@link OptimisticFamily}
 * with an Undo-style failure toast ({@link ToastController}). Deep-links to the live `/family/lists`.
 *
 * `loading` is passed from the page (whether the shared snapshot has resolved) so this card shows the same
 * skeleton/empty/failed lifecycle as the self-loading cards without owning a duplicate network call.
 */
@Component({
  selector: 'fb-lists-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HearthShell, FormsModule, MatIconModule],
  template: `
    <fb-hearth-shell
      title="Lists" route="/family/lists" icon="checklist"
      accentA="#7dd3fc" accentB="#38bdf8"
      [phase]="phase()" emptyText="No lists yet — start one in Lists." emptyIcon="playlist_add">

      @if (totalOpen()) {
        <span head-trailing class="badge">{{ totalOpen() }} to get</span>
      }

      @if (phase() === 'ready') {
        <div body class="lists">
          @for (l of lists(); track l.id) {
            <div class="lst">
              <div class="lst__head">
                <mat-icon class="lst__icon" aria-hidden="true">{{ l.kind === 'shopping' ? 'shopping_cart' : 'task' }}</mat-icon>
                <span class="lst__name">{{ l.name }}</span>
                <span class="lst__count" [class.lst__count--clear]="l.openCount === 0">
                  {{ l.openCount === 0 ? 'clear' : l.openCount + ' open' }}
                </span>
              </div>
              @if (l.firstFewOpenItems.length) {
                <ul class="lst__peek">
                  @for (it of l.firstFewOpenItems.slice(0, 2); track it) {
                    <li><span class="lst__tick" aria-hidden="true"></span>{{ it }}</li>
                  }
                </ul>
              }
              <form class="add" (submit)="add(l, $event)">
                <input class="add__input" type="text" [(ngModel)]="drafts[l.id]" name="draft{{ l.id }}"
                       [placeholder]="'Add to ' + l.name" [attr.aria-label]="'Add an item to ' + l.name"
                       autocomplete="off" enterkeyhint="done" />
                <button type="submit" class="add__btn" [disabled]="!draftFor(l.id)"
                        [attr.aria-label]="'Add item to ' + l.name">
                  <mat-icon aria-hidden="true">add</mat-icon>
                </button>
              </form>
            </div>
          }
        </div>
      }
    </fb-hearth-shell>
  `,
  styles: [`
    /* --wa / --wb are injected by HearthShell on the .w host element and inherit into projected content. */
    .badge {
      margin-left: auto; font-size: 12px; font-weight: 800; padding: 3px 10px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--wa, #38bdf8) 20%, transparent);
      color: color-mix(in srgb, var(--wa, #7dd3fc) 90%, var(--ink));
    }
    .lists { display: flex; flex-direction: column; gap: 16px; }
    .lst { display: flex; flex-direction: column; gap: 8px; }
    .lst + .lst { padding-top: 14px; border-top: 1px solid var(--hairline); }
    .lst__head { display: flex; align-items: center; gap: 8px; }
    .lst__icon {
      flex: 0 0 auto; font-size: 20px; width: 20px; height: 20px;
      color: color-mix(in srgb, var(--wa, #7dd3fc) 80%, var(--ink));
    }
    .lst__name { font-size: 15px; font-weight: 700; color: var(--ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .lst__count {
      margin-left: auto; flex: 0 0 auto; font-size: 12px; font-weight: 800;
      padding: 2px 9px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--wa, #38bdf8) 16%, transparent);
      color: color-mix(in srgb, var(--wa, #7dd3fc) 90%, var(--ink));
    }
    .lst__count--clear { background: color-mix(in srgb, var(--signal) 18%, transparent); color: var(--signal); }
    .lst__peek { list-style: none; margin: 0; padding: 0 0 0 28px; display: flex; flex-direction: column; gap: 4px; }
    .lst__peek li {
      display: flex; align-items: center; gap: 8px;
      font-size: 13px; color: var(--ink-dim); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .lst__tick { flex: 0 0 auto; width: 12px; height: 12px; border-radius: 4px; border: 1.5px solid var(--ink-faint); }

    .add { display: flex; gap: 8px; margin-top: 2px; }
    .add__input {
      flex: 1 1 auto; min-width: 0; min-height: 44px; padding: 0 14px;
      border-radius: var(--r-pill); border: 1px solid var(--hairline);
      background: var(--bg-sink); color: var(--ink); font: inherit; font-size: 14px;
      transition: border-color 120ms var(--ease-out);
    }
    .add__input::placeholder { color: var(--ink-faint); }
    .add__input:focus-visible {
      outline: 2px solid color-mix(in srgb, var(--wa, #38bdf8) 90%, transparent);
      outline-offset: 1px;
    }
    .add__btn {
      flex: 0 0 auto; display: grid; place-items: center; width: 44px; height: 44px;
      border-radius: var(--r-pill); border: none; cursor: pointer;
      background: linear-gradient(135deg, var(--wa, #7dd3fc), var(--wb, #38bdf8));
      color: var(--ink-on-accent, #06243a);
      transition: transform 120ms var(--ease-spring), opacity 120ms;
    }
    .add__btn:hover:not(:disabled) { opacity: .88; }
    .add__btn:active { transform: scale(.92); }
    .add__btn:disabled { opacity: .4; cursor: default; }
    .add__btn:focus-visible { outline: 2px solid var(--ink); outline-offset: 2px; }
  `],
})
export class ListsCard {
  private readonly optimistic = inject(OptimisticFamily);
  private readonly toasts = inject(ToastController);

  /** The shared Today snapshot (page-owned, best-effort). */
  readonly today = input<FamilyToday | null>(null);
  /** Whether the shared snapshot is still loading (page-owned). */
  readonly loading = input<boolean>(true);
  /** Whether the shared snapshot load failed (page-owned). */
  readonly failed = input<boolean>(false);

  /** Per-list add-item drafts, keyed by list id. */
  readonly drafts: Record<number, string> = {};

  readonly lists = computed<FamilyTodayList[]>(() => this.today()?.lists ?? []);
  readonly totalOpen = computed(() => this.lists().reduce((s, l) => s + (l.openCount || 0), 0));

  readonly phase = computed<HearthPhase>(() => {
    if (this.loading()) return 'loading';
    if (this.failed()) return 'failed';
    return this.lists().length ? 'ready' : 'empty';
  });

  draftFor(id: number): string {
    return (this.drafts[id] ?? '').trim();
  }

  /** Add the drafted item to the list via the fast-action endpoint (optimistic; clears the box on send). */
  async add(l: FamilyTodayList, ev: Event): Promise<void> {
    ev.preventDefault();
    const text = this.draftFor(l.id);
    if (!text) return;
    this.drafts[l.id] = '';
    const retry = () => { this.drafts[l.id] = text; };
    const res = await this.optimistic.addListItem(l.id, text, /* rollback */ retry, retry);
    if (res) this.toasts.show(`Added to ${l.name}`, { tone: 'success' });
    // The list page reconciles full state on next visit; here the open count is refreshed when the page
    // re-pulls the snapshot (pull-to-refresh / day-rollover). The optimistic contract restores the draft
    // on failure so the user can retry inline.
  }
}
