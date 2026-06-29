import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { ChatRealtime } from '../../core/chat-realtime';
import { FeedItem, PERM } from '../../core/models';

/** A feed row projected with the small derived bits the ticker chips need (display-name only — never email). */
export interface PulseTickerItem extends FeedItem {
  /** Two-letter avatar initials, derived from the display name. */
  initials: string;
  /** A short humane verb phrase ("logged a workout", "completed 75 Hard day 12"). Counts/labels only. */
  verb: string;
  /** A Material icon glyph for the kind. */
  icon: string;
}

/** How many moments the compact ticker surfaces. */
const TICKER_LIMIT = 5;
/** Light polling cadence used as the LIVE fallback (there is no feed-event hub broadcast). */
const POLL_MS = 50_000;

/**
 * Shared data source for the dashboard "Pulse" ticker (desktop + mobile twin both consume this ONE store,
 * so the wire shape, gating, and refresh policy live in a single place). FE-ONLY: there is no feed-event
 * SignalR broadcast, so this stays live WITHOUT adding any backend by:
 *   - a light periodic refetch ({@link POLL_MS}), and
 *   - reacting to {@link ChatRealtime.liveNotification} — any incoming notification (a clap, a comment, a
 *     mention, a system event) is a strong hint the circle just did something, so it triggers an immediate
 *     (debounced) refetch. That gives a near-live feel off the EXISTING chat hub, no new broadcast needed.
 *
 * GATE: the ticker is only meaningful when the caller can SEE the circle feed — they hold {@link PERM.trackerSelf}
 * (the feed's perm) AND opted into VIEWING it (session.viewActivityFeed). {@link canSee} encodes that; the
 * widgets render nothing when it's false. The server still enforces visibility on every /api/feed call.
 *
 * The poll/effect only run while the store has an active consumer (ref-counted via {@link attach}); the last
 * detach clears the interval, so an unmounted dashboard leaves nothing ticking.
 */
@Injectable({ providedIn: 'root' })
export class PulseTickerStore {
  private api = inject(Api);
  private auth = inject(AuthService);
  private realtime = inject(ChatRealtime);

  /** The latest feed rows (newest-first), already projected for the chips. */
  private readonly _items = signal<PulseTickerItem[]>([]);
  readonly items = this._items.asReadonly();

  /** True only during the very first fetch (drives the skeleton — later refreshes are silent). */
  private readonly _loading = signal(false);
  readonly loading = this._loading.asReadonly();
  /** True when the first fetch failed and we have nothing to show (drives the error affordance). */
  private readonly _error = signal(false);
  readonly error = this._error.asReadonly();
  private firstLoadDone = false;

  /** Whether the caller is allowed to see the circle feed (perm + opt-in) — widgets gate render on this. */
  readonly canSee = computed(
    () =>
      this.auth.hasPermission(PERM.trackerSelf) &&
      this.auth.session()?.viewActivityFeed === true,
  );

  /** True once we have at least one moment to show. */
  readonly hasItems = computed(() => this._items().length > 0);

  /** A subtle "live" affordance: pulse the dot only while the chat hub is actually connected. */
  readonly live = computed(() => this.realtime.isConnected());

  private timer: ReturnType<typeof setInterval> | null = null;
  private consumers = 0;
  /** Coalesces a burst of notifications into one refetch. */
  private notifyDebounce: ReturnType<typeof setTimeout> | null = null;
  private lastFetchMs = 0;

  constructor() {
    // React to any live notification: it's a cheap, backend-free signal that the circle is active. Refetch
    // (debounced) so a clap/comment/mention surfaces in the ticker within ~1s without polling faster.
    effect(() => {
      this.realtime.liveNotification(); // track
      if (this.consumers === 0 || !this.canSee()) return;
      if (this.notifyDebounce) clearTimeout(this.notifyDebounce);
      this.notifyDebounce = setTimeout(() => this.refresh(), 1000);
    });
  }

  /**
   * Register a consumer (call from a widget's constructor with its DestroyRef). The FIRST consumer kicks
   * off the initial load + starts the poll; the LAST to be destroyed stops the poll. Idempotent per widget.
   */
  attach(destroyRef: DestroyRef): void {
    this.consumers++;
    if (this.consumers === 1) {
      if (this.canSee()) this.refresh();
      this.startPoll();
    } else if (this.canSee() && !this.firstLoadDone) {
      // A late consumer joining before the first load resolved still wants data flowing.
      this.refresh();
    }
    destroyRef.onDestroy(() => this.detach());
  }

  private detach(): void {
    this.consumers = Math.max(0, this.consumers - 1);
    if (this.consumers === 0) this.stopPoll();
  }

  private startPoll(): void {
    if (this.timer != null) return;
    this.timer = setInterval(() => {
      if (this.canSee()) this.refresh();
    }, POLL_MS);
  }

  private stopPoll(): void {
    if (this.timer != null) {
      clearInterval(this.timer);
      this.timer = null;
    }
    if (this.notifyDebounce) {
      clearTimeout(this.notifyDebounce);
      this.notifyDebounce = null;
    }
  }

  /** Fetch the latest few moments. Silent on refresh; only the very first load shows the skeleton. */
  refresh(): void {
    if (!this.canSee()) return;
    // Throttle: never fire two fetches within 3s (a notification burst + the poll could otherwise collide).
    const now = Date.now();
    if (now - this.lastFetchMs < 3000 && this.firstLoadDone) return;
    this.lastFetchMs = now;

    if (!this.firstLoadDone) this._loading.set(true);
    this.api
      .feed({ limit: TICKER_LIMIT })
      .pipe(catchError(() => of(null)))
      .subscribe((page) => {
        if (page) {
          this._items.set(page.items.map((i) => PulseTickerStore.project(i)));
          this._error.set(false);
        } else if (!this.firstLoadDone) {
          this._error.set(true);
        }
        this._loading.set(false);
        this.firstLoadDone = true;
      });
  }

  /** Project a raw feed row into the chip view-model (initials + verb + icon). */
  private static project(i: FeedItem): PulseTickerItem {
    return { ...i, initials: initialsOf(i.actorName), verb: verbOf(i), icon: iconOf(i.kind) };
  }
}

/** Two-letter initials for the avatar fallback (display name only — never an email). */
function initialsOf(name: string): string {
  const parts = (name || '').split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
}

/** A Material icon glyph for the kind (mirrors the /feed mapping; generic glyph for unknown kinds). */
function iconOf(kind: string): string {
  switch (kind) {
    case 'workout.logged':
      return 'fitness_center';
    case 'challenge.dayComplete':
      return 'military_tech';
    case 'challenge.started':
      return 'flag';
    case 'hydration.goalHit':
      return 'local_drink';
    default:
      return 'bolt';
  }
}

/** A short humane verb phrase (counts/labels only — never raw private content). Mirrors /feed. */
function verbOf(item: FeedItem): string {
  switch (item.kind) {
    case 'workout.logged': {
      const name = (item.label ?? '').trim();
      const mins = item.intValue;
      if (name && mins) return `logged a ${mins}-minute ${name}`;
      if (name) return `logged a workout: ${name}`;
      if (mins) return `logged a ${mins}-minute workout`;
      return 'logged a workout';
    }
    case 'challenge.dayComplete':
      return item.intValue ? `completed 75 Hard day ${item.intValue}` : 'completed a 75 Hard day';
    case 'challenge.started':
      return 'started the 75 Hard challenge';
    case 'hydration.goalHit':
      return 'hit their water goal';
    default:
      return 'shared an activity';
  }
}
