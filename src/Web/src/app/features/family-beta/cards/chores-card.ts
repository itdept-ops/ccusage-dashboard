import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { FamilyChore, FamilyChores } from '../../../core/models';
import { BetaSvgRing, BetaSwipeRow, ToastController } from '../../beta-ui';
import { HearthShell, HearthPhase } from './hearth-shell';
import { OptimisticFamily } from '../state/optimistic-family';

/**
 * Hearth "Chores" glance card — rebuilt on the shared beta-ui foundation. A real progress RING
 * ({@link BetaSvgRing}) shows how much of today's board is cleared (done / total), with a big Clash
 * Display "done/total" numeral inside; the next still-open chores list each as a {@link BetaSwipeRow} you
 * swipe RIGHT to complete (or tap the tick) — optimistic: the row flips + the ring + count bump instantly,
 * reconcile from the server board, roll back + Undo toast on failure via {@link OptimisticFamily} +
 * {@link ToastController}. Best-effort: it owns its own cold {@link Api.familyChores} subscription with
 * `catchError(of(null))`, so a chores/network failure blanks only THIS card. Deep-links to `/family/chores`.
 *
 * Tick semantics mirror the live page's role split WITHOUT importing it: a manager (parent) toggles the
 * legacy `done`; a child submits for approval. Names only (assignedToName) — never an email.
 */
@Component({
  selector: 'fb-chores-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HearthShell, MatIconModule, BetaSvgRing, BetaSwipeRow],
  template: `
    <fb-hearth-shell
      title="Chores" route="/family/chores" icon="cleaning_services"
      accentA="#fca5a5" accentB="#fb7185"
      [phase]="phase()" emptyText="All chores are done — nice." emptyIcon="task_alt"
      (retry)="reload()">

      <span head-trailing class="count" [class.count--zero]="openCount() === 0">
        {{ openCount() }} open
      </span>

      @if (phase() === 'ready') {
        <div body class="ch">
          <div class="ch__top">
            <app-bs-ring [value]="progress()" [size]="76" [stroke]="9"
                         from="#fca5a5" to="#fb7185" [signalOnFull]="true"
                         [label]="doneCount() + ' of ' + total() + ' chores done'">
              <span class="ch__ring">
                <b class="ch__ring-num">{{ doneCount() }}</b><span class="ch__ring-den">/{{ total() }}</span>
              </span>
            </app-bs-ring>
            <div class="ch__sum">
              <span class="ch__sum-big">{{ openCount() }}</span>
              <span class="ch__sum-lbl">{{ openCount() === 1 ? 'chore left' : 'chores left' }}</span>
              @if (starsToday()) { <span class="ch__sum-star" aria-label="stars available">★ {{ starsToday() }} up for grabs</span> }
            </div>
          </div>

          @if (topOpen().length) {
            <ul class="ch__list">
              @for (c of topOpen(); track c.id) {
                <li>
                  <app-bs-swipe-row rightLabel="Done" [leftDestructive]="false"
                                    [disabled]="busy().has(c.id)" [label]="c.title"
                                    (swipe)="onSwipe(c, $event)">
                    <div class="row">
                      <button type="button" class="tick" [disabled]="busy().has(c.id)"
                              (click)="tick(c)" [attr.aria-label]="'Mark ' + c.title + ' done'">
                        <mat-icon aria-hidden="true">radio_button_unchecked</mat-icon>
                      </button>
                      <span class="row__text">
                        <span class="row__title">{{ c.title }}</span>
                        @if (c.assignedToName) { <span class="row__who">{{ c.assignedToName }}</span> }
                      </span>
                      @if (c.points) { <span class="row__pts" aria-label="points">★ {{ c.points }}</span> }
                    </div>
                  </app-bs-swipe-row>
                </li>
              }
              @if (openCount() > topOpen().length) {
                <li class="more">+{{ openCount() - topOpen().length }} more open</li>
              }
            </ul>
          }
        </div>
      }
    </fb-hearth-shell>
  `,
  styles: [`
    .count {
      margin-left: auto; font-size: 12px; font-weight: 800;
      padding: 3px 10px; border-radius: var(--r-pill);
      background: color-mix(in srgb, #fb7185 22%, transparent); color: #fda4af;
    }
    .count--zero { background: color-mix(in srgb, var(--signal) 20%, transparent); color: var(--signal); }

    .ch { display: flex; flex-direction: column; gap: 14px; }
    .ch__top { display: flex; align-items: center; gap: 16px; }
    .ch__ring { display: inline-flex; align-items: baseline; }
    .ch__ring-num { font-size: 22px; font-weight: 600; color: var(--ink); }
    .ch__ring-den { font-size: 13px; font-weight: 700; color: var(--ink-dim); }
    .ch__sum { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
    .ch__sum-big { font-family: var(--font-display); font-size: 30px; font-weight: 600; letter-spacing: -.025em; color: var(--ink); line-height: 1; }
    .ch__sum-lbl { font-size: 12px; font-weight: 700; letter-spacing: .03em; text-transform: uppercase; color: var(--ink-dim); }
    .ch__sum-star { margin-top: 4px; font-size: 12px; font-weight: 700; color: var(--warn); }

    .ch__list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; align-items: center; gap: 12px; padding: 8px 12px; min-height: 48px; }
    .tick {
      flex: 0 0 auto; display: grid; place-items: center;
      width: 40px; height: 40px; margin: -4px 0 -4px -8px;
      border: none; background: transparent; color: #fb7185; cursor: pointer; border-radius: var(--r-pill);
    }
    .tick:disabled { opacity: .5; }
    .tick:focus-visible { outline: 2px solid #fb7185; outline-offset: 2px; }
    .tick mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .row__text { display: flex; flex-direction: column; min-width: 0; flex: 1 1 auto; }
    .row__title { font-size: 15px; color: var(--ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row__who { font-size: 12px; color: var(--ink-dim); }
    .row__pts { flex: 0 0 auto; font-size: 13px; color: var(--warn); font-weight: 700; }
    .more { padding: 4px 2px 0; font-size: 13px; color: var(--ink-dim); }
  `],
})
export class ChoresCard {
  private readonly api = inject(Api);
  private readonly optimistic = inject(OptimisticFamily);
  private readonly toasts = inject(ToastController);
  private readonly destroyRef = inject(DestroyRef);

  private readonly board = signal<FamilyChores | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);
  /** Chore ids with an in-flight tick (so we disable the row + ignore double-taps). */
  readonly busy = signal<ReadonlySet<number>>(new Set());

  /** Still-open chores (not done and not already submitted/approved) in board order. */
  private readonly openChores = computed<FamilyChore[]>(() =>
    (this.board()?.chores ?? []).filter(c => !c.done && c.status !== 'submitted' && c.status !== 'approved'));

  readonly openCount = computed(() => this.openChores().length);
  readonly topOpen = computed(() => this.openChores().slice(0, 4));

  /** Total + done across the whole board (drives the progress ring). */
  readonly total = computed(() => (this.board()?.chores ?? []).length);
  readonly doneCount = computed(() => Math.max(0, this.total() - this.openCount()));
  readonly progress = computed(() => { const t = this.total(); return t ? this.doneCount() / t : 0; });

  /** Stars available across the still-open chores (a glanceable "up for grabs" nudge). */
  readonly starsToday = computed(() => this.openChores().reduce((s, c) => s + (c.points || 0), 0));

  readonly phase = computed<HearthPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    return this.board() ? 'ready' : 'empty';
  });

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loadingState.set(true);
    this.failed.set(false);
    this.api.familyChores()
      .pipe(catchError(() => { this.failed.set(true); return of<FamilyChores | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(b => {
        if (b) this.board.set(b);
        this.loadingState.set(false);
      });
  }

  /** A RIGHT swipe past threshold commits the same "done" tick as the button. */
  onSwipe(ch: FamilyChore, side: 'left' | 'right'): void {
    if (side === 'right') void this.tick(ch);
  }

  /** Optimistically remove the chore from the open list, then commit via the role-correct endpoint. */
  async tick(ch: FamilyChore): Promise<void> {
    if (this.busy().has(ch.id)) return;
    const prev = this.board();
    const canManage = prev?.canManage ?? false;

    // Optimistic local bump: drop the chore from the open set immediately.
    this.board.update(b => b ? { ...b, chores: b.chores.map(c =>
      c.id === ch.id ? { ...c, done: canManage ? true : c.done, status: canManage ? c.status : 'submitted' } : c) } : b);
    this.setBusy(ch.id, true);

    const rollback = () => this.board.set(prev);
    const retry = () => void this.tick(ch);
    const result = canManage
      ? await this.optimistic.toggleChore(ch.id, true, rollback, retry)
      : await this.optimistic.submitChore(ch.id, rollback, retry);

    if (result) {
      this.board.set(result); // reconcile from the authoritative board
      this.toasts.show(canManage ? `“${ch.title}” done` : `“${ch.title}” submitted`, { tone: 'success' });
    }
    this.setBusy(ch.id, false);
  }

  private setBusy(id: number, on: boolean): void {
    this.busy.update(s => {
      const next = new Set(s);
      if (on) next.add(id); else next.delete(id);
      return next;
    });
  }
}
