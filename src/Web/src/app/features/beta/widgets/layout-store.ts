import { Injectable, computed, signal } from '@angular/core';

/**
 * The stable id of each Atrium widget. The id (not the array index) is what's persisted, so reordering
 * the {@link DEFAULT_ORDER} array in code never corrupts a user's saved layout.
 */
export type AtriumWidgetId = 'rings' | 'hard' | 'event' | 'presence' | 'spend' | 'activity';

/** The factory-default top-to-bottom order, used on first run and as the merge anchor for new widgets. */
export const DEFAULT_ORDER: readonly AtriumWidgetId[] = ['rings', 'hard', 'event', 'presence', 'spend', 'activity'];

interface PersistedLayout {
  /** The user's widget order (subset/superset of the defaults; unknown ids are dropped on read). */
  order: AtriumWidgetId[];
  /** Widgets the user explicitly turned OFF. Default-on; absence means on. */
  hidden: AtriumWidgetId[];
}

const STORAGE_KEY = 'beta.home.layout';

/**
 * Persists the Atrium widget ORDER + on/off state to localStorage under `beta.home.layout`, and drives
 * the long-press "reorder mode". This is a NEW, beta-only store — it touches no live state and no live
 * component. It is intentionally NOT `providedIn: 'root'`: the page provides it so the layout lives and
 * dies with the Atrium and never leaks into the rest of the app.
 *
 * Resilience: every localStorage read/write is wrapped so a private-mode / quota / corrupt-JSON failure
 * silently falls back to the in-memory defaults — a broken storage layer must never blank the page.
 */
@Injectable()
export class AtriumLayoutStore {
  /** Current widget order (always a permutation of the known ids, with new ids appended). */
  readonly order = signal<AtriumWidgetId[]>(DEFAULT_ORDER.slice());

  /** The set of widgets the user turned off. */
  readonly hidden = signal<Set<AtriumWidgetId>>(new Set<AtriumWidgetId>());

  /** True while the long-press reorder UI is active (move up/down + on/off toggles shown per card). */
  readonly reordering = signal(false);

  constructor() {
    this.restore();
  }

  /** Is a given widget enabled (visible) per the user's saved layout? Default-on. */
  isOn(id: AtriumWidgetId): boolean {
    return !this.hidden().has(id);
  }

  /** A reactive view: the ordered, enabled widget ids the page should render. */
  readonly visibleOrder = computed<AtriumWidgetId[]>(() => {
    const hidden = this.hidden();
    return this.order().filter(id => !hidden.has(id));
  });

  /** Enter/leave the long-press reorder mode. */
  toggleReorder(): void {
    this.reordering.update(v => !v);
  }

  setReorder(on: boolean): void {
    this.reordering.set(on);
  }

  /** Move a widget one slot toward the top (no-op at the top). */
  moveUp(id: AtriumWidgetId): void {
    this.move(id, -1);
  }

  /** Move a widget one slot toward the bottom (no-op at the bottom). */
  moveDown(id: AtriumWidgetId): void {
    this.move(id, +1);
  }

  /** Turn a widget on/off. Turning all of them off is allowed (the page shows its own empty state). */
  toggle(id: AtriumWidgetId): void {
    this.hidden.update(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
    this.persist();
  }

  /** Reset to the factory order + all-on, and persist. */
  reset(): void {
    this.order.set(DEFAULT_ORDER.slice());
    this.hidden.set(new Set<AtriumWidgetId>());
    this.persist();
  }

  private move(id: AtriumWidgetId, delta: number): void {
    const arr = this.order().slice();
    const i = arr.indexOf(id);
    const j = i + delta;
    if (i < 0 || j < 0 || j >= arr.length) return;
    [arr[i], arr[j]] = [arr[j], arr[i]];
    this.order.set(arr);
    this.persist();
  }

  /** Best-effort load; any failure leaves the in-memory defaults intact. */
  private restore(): void {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw) as Partial<PersistedLayout> | null;
      if (!parsed) return;
      const known = new Set<AtriumWidgetId>(DEFAULT_ORDER);
      // Keep only known ids, in the saved order, then append any defaults the save didn't know about
      // (so a newly-shipped widget appears at the bottom rather than vanishing).
      const savedOrder = (parsed.order ?? []).filter((id): id is AtriumWidgetId => known.has(id));
      const merged = [...savedOrder, ...DEFAULT_ORDER.filter(id => !savedOrder.includes(id))];
      this.order.set(merged);
      const hidden = (parsed.hidden ?? []).filter((id): id is AtriumWidgetId => known.has(id));
      this.hidden.set(new Set(hidden));
    } catch {
      // corrupt JSON / blocked storage — keep defaults
    }
  }

  private persist(): void {
    try {
      const payload: PersistedLayout = { order: this.order(), hidden: [...this.hidden()] };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
    } catch {
      // quota / private mode — non-fatal, layout just won't survive a reload
    }
  }
}
