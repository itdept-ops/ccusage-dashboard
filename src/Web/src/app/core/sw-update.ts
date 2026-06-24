import { inject, Injectable } from '@angular/core';
import { ApplicationRef } from '@angular/core';
import { SwUpdate, VersionReadyEvent } from '@angular/service-worker';
import { MatSnackBar } from '@angular/material/snack-bar';
import { concat, filter, first, interval } from 'rxjs';

/**
 * Service-worker update + lifecycle helper. Two responsibilities:
 *
 *  1. PROMPT (never force) on a new deployed version. When the SW reports a {@link VersionReadyEvent}
 *     we pop a sticky snackbar — "New version available — Reload" — and reload ONLY if the user clicks.
 *     We never auto-reload, so the user is never interrupted mid-task. (nginx serves index.html
 *     no-cache, so a reload always lands on the freshest shell; the SW just controls hashed assets.)
 *
 *  2. SUPPORT escape hatch: {@link disable} unregisters every service worker for this origin (used by
 *     the "Offline mode" DISABLE toggle in /preferences), so a stuck client can fully opt out without
 *     clearing site data by hand.
 *
 * Registration itself is wired in app.config via provideServiceWorker (enabled only in prod builds).
 * Here we only react to it. {@link init} is called once from the app shell.
 */
@Injectable({ providedIn: 'root' })
export class SwUpdateService {
  private updates = inject(SwUpdate);
  private appRef = inject(ApplicationRef);
  private snack = inject(MatSnackBar);

  private started = false;

  /** Wire the version-ready prompt and a periodic update check. Idempotent + safe when SW is disabled. */
  init(): void {
    if (this.started || !this.updates.isEnabled) return;
    this.started = true;

    // Prompt the user whenever a new version is ready (download already done by the SW).
    this.updates.versionUpdates
      .pipe(filter((e): e is VersionReadyEvent => e.type === 'VERSION_READY'))
      .subscribe(() => this.promptReload());

    // Poll for updates only after the app stabilizes, then every 6h — cheap, and avoids fighting
    // initial bootstrap. The SW also checks on navigation; this just keeps long-lived tabs current.
    const stable$ = this.appRef.isStable.pipe(first((s) => s === true));
    const every6h$ = interval(6 * 60 * 60 * 1000);
    concat(stable$, every6h$).subscribe(() => {
      this.updates.checkForUpdate().catch(() => {
        /* offline / transient — try again next tick */
      });
    });
  }

  private promptReload(): void {
    const ref = this.snack.open('New version available', 'Reload', { duration: 0 });
    ref.onAction().subscribe(() => {
      // activateUpdate swaps the SW to the new version; reload to pick up the fresh shell.
      this.updates.activateUpdate().then(() => document.location.reload());
    });
  }

  /**
   * Fully unregister every service worker for this origin (the "disable offline mode" support action).
   * Returns true if at least one registration was removed. After this the app behaves as a plain SPA
   * until the next prod load re-registers (or never, if the user keeps it off — the toggle is sticky
   * via {@link OFFLINE_DISABLED_KEY}).
   */
  async disable(): Promise<boolean> {
    if (!('serviceWorker' in navigator)) return false;
    const regs = await navigator.serviceWorker.getRegistrations();
    let removed = false;
    for (const reg of regs) {
      removed = (await reg.unregister()) || removed;
    }
    return removed;
  }
}

/** localStorage flag: when 'true', the user has opted OUT of the installable/offline service worker. */
export const OFFLINE_DISABLED_KEY = 'usageiq.offline.disabled';
