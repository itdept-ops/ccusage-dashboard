import {
  ChangeDetectionStrategy, Component, computed, inject, input, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { CommentDto, PERM } from '../../core/models';
import { timeAgo } from '../../shared/format';

/** A pending optimistic comment carries a negative temp id until the server returns the real row. */
const MAX_BODY = 500;

/**
 * <app-feed-comments> — the comment thread + composer under one /feed event. Lazy-opens (the thread is only
 * fetched the first time it's expanded), renders each comment as an avatar-initial + DisplayName + relative
 * time + body, and posts new comments OPTIMISTICALLY (a temp row appears immediately, then reconciles with the
 * server row or rolls back on failure) — mirroring the cheer() pattern on the feed.
 *
 * PRIVACY: every author is an AppUser id + DisplayName-formatted name (never an email); the server enforces
 * the SAME circle/visibility gate as the feed (404 when the event isn't visible) and validates the body. The
 * delete affordance shows on the caller's own comments, or any comment when the caller holds chat.moderate.
 */
@Component({
  selector: 'app-feed-comments',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatIconModule],
  styleUrl: './feed-comments.scss',
  template: `
    <div class="fc">
      <button type="button" class="fc__toggle" [class.is-open]="open()"
              [attr.aria-expanded]="open()" (click)="toggle()">
        <mat-icon aria-hidden="true">{{ open() ? 'mode_comment' : 'chat_bubble_outline' }}</mat-icon>
        @if (count() > 0) {
          <span class="fc__toggle-count">{{ count() }}</span>
        }
        <span class="fc__toggle-label">{{ open() ? 'Hide' : (count() > 0 ? 'Comments' : 'Comment') }}</span>
      </button>

      @if (open()) {
        <div class="fc__panel">
          @if (loading()) {
            <p class="fc__state" role="status">Loading comments…</p>
          } @else if (errored()) {
            <p class="fc__state fc__state--err" role="alert">
              Couldn't load comments.
              <button type="button" class="fc__retry" (click)="reload()">Retry</button>
            </p>
          } @else {
            @if (comments().length) {
              <ul class="fc__list">
                @for (c of comments(); track c.id) {
                  <li class="fc__item" [class.is-pending]="c.id < 0">
                    <span class="fc__avatar" aria-hidden="true">{{ initials(c.authorName) }}</span>
                    <div class="fc__body">
                      <p class="fc__line">
                        <span class="fc__name">{{ c.authorName }}</span>
                        <time class="fc__time" [attr.datetime]="c.createdUtc">{{ timeAgo(c.createdUtc) }}</time>
                      </p>
                      <p class="fc__text">{{ c.body }}</p>
                    </div>
                    @if (canDelete(c)) {
                      <button type="button" class="fc__del" aria-label="Delete comment"
                              [disabled]="c.id < 0" (click)="remove(c)">
                        <mat-icon aria-hidden="true">close</mat-icon>
                      </button>
                    }
                  </li>
                }
              </ul>
            } @else {
              <p class="fc__state fc__empty">No comments yet — be the first.</p>
            }

            <form class="fc__composer" (submit)="submit($event)">
              <input class="fc__input" type="text" [(ngModel)]="draft" name="comment"
                     [maxlength]="maxBody" autocomplete="off"
                     placeholder="Add a comment…" aria-label="Add a comment" />
              <button type="submit" class="fc__send" [disabled]="!canSend()"
                      aria-label="Post comment">
                <mat-icon aria-hidden="true">send</mat-icon>
              </button>
            </form>
          }
        </div>
      }
    </div>
  `,
})
export class FeedComments {
  private api = inject(Api);
  private auth = inject(AuthService);

  /** The feed event this thread hangs under. */
  readonly eventId = input.required<number>();
  /** The server's known comment count for this row (so the closed pill shows a count without a fetch). */
  readonly initialCount = input<number>(0);

  readonly open = signal(false);
  readonly loading = signal(false);
  readonly errored = signal(false);
  readonly loaded = signal(false);
  readonly comments = signal<CommentDto[]>([]);
  draft = '';

  readonly maxBody = MAX_BODY;
  readonly timeAgo = timeAgo;

  /** A monotonically-decreasing temp id source for optimistic rows. */
  private tempId = -1;
  /** Re-entrancy guard so a submit/delete can't double-fire. */
  private busy = false;

  /** The pill count: the live thread length once loaded, else the server-provided initial count. */
  readonly count = computed(() => (this.loaded() ? this.comments().length : this.initialCount()));

  /** Whether the composer can post (non-empty after trim, not mid-flight). */
  readonly canSend = computed(() => this.draft.trim().length > 0 && !this.loading());

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    if (next && !this.loaded()) this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.errored.set(false);
    this.api
      .feedComments(this.eventId())
      .pipe(catchError(() => { this.errored.set(true); return of<CommentDto[] | null>(null); }))
      .subscribe((rows) => {
        if (rows) {
          this.comments.set(rows);
          this.loaded.set(true);
        }
        this.loading.set(false);
      });
  }

  /** Post a comment optimistically (temp row appears now; reconciles with the server row or rolls back). */
  submit(ev: Event): void {
    ev.preventDefault();
    const body = this.draft.trim();
    if (!body || this.busy) return;
    this.busy = true;

    const temp: CommentDto = {
      id: this.tempId--, eventId: this.eventId(), authorUserId: this.auth.session()?.userId ?? 0,
      authorName: this.auth.session()?.name ?? 'You', body, mine: true,
      createdUtc: new Date().toISOString(), editedUtc: null,
    };
    const tempRef = temp.id;
    this.comments.update((cur) => [...cur, temp]);
    this.draft = '';

    this.api
      .addFeedComment(this.eventId(), body)
      .pipe(catchError(() => of<CommentDto | null>(null)))
      .subscribe((real) => {
        this.comments.update((cur) =>
          real
            ? cur.map((c) => (c.id === tempRef ? real : c))
            : cur.filter((c) => c.id !== tempRef), // rollback on failure
        );
        if (!real) this.draft = body; // restore the text so the user can retry
        this.busy = false;
      });
  }

  /** Delete a comment (author or chat.moderate). Optimistically removes it, restores on failure. */
  remove(c: CommentDto): void {
    if (c.id < 0 || this.busy) return;
    this.busy = true;
    const snapshot = this.comments();
    this.comments.update((cur) => cur.filter((x) => x.id !== c.id));
    this.api
      .deleteFeedComment(c.id)
      .pipe(catchError(() => of<{ rolledBack: true } | null>({ rolledBack: true })))
      .subscribe((res) => {
        if (res) this.comments.set(snapshot); // restore on failure
        this.busy = false;
      });
  }

  /** Whether the caller may delete this comment (own comment, or holds chat.moderate). */
  canDelete(c: CommentDto): boolean {
    return c.mine || this.auth.hasPermission(PERM.chatModerate);
  }

  /** Two-letter initials for the avatar fallback (name only — never an email). */
  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }
}
