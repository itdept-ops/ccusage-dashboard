import { Injectable, signal } from '@angular/core';

/**
 * A single coach-mark step. `anchor` is the stable `data-tour="…"` id on the element to spotlight
 * (resolved live from the DOM at render time, so a not-yet-mounted / permission-hidden target is
 * skipped gracefully). `placement` is a HINT — the overlay flips/falls back if it won't fit.
 */
export interface TourStep {
  /** The `data-tour` id of the element to spotlight. */
  anchor: string;
  /** Card heading. */
  title: string;
  /** One- or two-sentence explanation. */
  blurb: string;
  /** Preferred side for the card relative to the target (overlay flips if it won't fit). */
  placement?: 'top' | 'bottom' | 'left' | 'right';
}

/** A named tour: an ordered list of steps the {@link GuidedTour} component walks through. */
export interface TourDef {
  /** Stable id (also the localStorage "seen" key suffix). */
  id: string;
  steps: TourStep[];
}

const SEEN_PREFIX = 'usage_iq_tour_seen_';

/**
 * Owns guided-tour state app-wide: which tour (if any) is currently running, and the per-tour
 * "already seen" flag persisted to localStorage. The {@link GuidedTour} overlay (mounted once at the
 * shell root) renders whatever {@link active} points at; pages/menus drive it via {@link maybeAutoStart}
 * (first-run) and {@link replay} (the user-invoked "Take the tour" trigger).
 *
 * Storage is best-effort: every read/write is try/catch-guarded exactly like the beta hub-layout store,
 * so private-mode / quota / corrupt-value failures degrade to "treat as unseen" and never throw into the
 * render path. providedIn: 'root' — one instance for the whole app.
 */
@Injectable({ providedIn: 'root' })
export class TourService {
  /** The tour currently being shown, or null when nothing is running. */
  readonly active = signal<TourDef | null>(null);

  /** Has this tour been completed/skipped before (persisted)? Drives first-run auto-start. */
  hasSeen(id: string): boolean {
    try {
      return localStorage.getItem(SEEN_PREFIX + id) === '1';
    } catch {
      // blocked / private mode — treat as unseen (auto-start once; a no-persist env just re-offers it).
      return false;
    }
  }

  /** Mark a tour seen so it never auto-starts again (best-effort persist). */
  private markSeen(id: string): void {
    try {
      localStorage.setItem(SEEN_PREFIX + id, '1');
    } catch {
      // quota / private mode — non-fatal; the tour simply isn't suppressed next session.
    }
  }

  /** Clear the seen flag so the next eligible visit auto-starts the tour again. */
  resetSeen(id: string): void {
    try {
      localStorage.removeItem(SEEN_PREFIX + id);
    } catch {
      /* non-fatal */
    }
  }

  /**
   * First-run entry point: start the tour ONLY if it has steps, isn't already running, and the user
   * hasn't seen it before. Returns true if it actually started. Marking-seen happens on finish/skip
   * (in {@link end}) so an interrupted first run (e.g. a reload) still re-offers the tour.
   */
  maybeAutoStart(def: TourDef): boolean {
    if (!def.steps.length || this.active() || this.hasSeen(def.id)) return false;
    this.active.set(def);
    return true;
  }

  /** User-invoked replay: clear the seen flag and (re)start immediately, regardless of prior state. */
  replay(def: TourDef): void {
    if (!def.steps.length) return;
    this.resetSeen(def.id);
    this.active.set(def);
  }

  /**
   * End the active tour. `markSeen` distinguishes a finish/skip (mark the tour seen so it won't auto-start
   * again) from a transient teardown (e.g. navigating away). Always clears {@link active}.
   */
  end(opts: { markSeen?: boolean } = {}): void {
    const cur = this.active();
    if (cur && opts.markSeen) this.markSeen(cur.id);
    this.active.set(null);
  }
}
