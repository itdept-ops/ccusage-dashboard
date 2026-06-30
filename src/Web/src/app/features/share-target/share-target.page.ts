import { Component, OnInit, inject, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../core/auth';
import { normalizeHome } from '../../core/nav-model';
import { PERM } from '../../core/models';
import { toLocalDate } from '../../core/tracker-store';
import { AddFoodDialog, AddFoodData } from '../tracker/add-food-dialog';

/** Cache name + request keys the service worker stashes a POST share into (see CLIENT↔SW protocol). */
const SHARED_MEDIA_CACHE = 'shared-media';
const SHARED_PHOTO_KEY = '/__shared/photo';
const SHARED_TEXT_KEY = '/__shared/text';

/** What a POST share yielded once read (and cleared) from the SW cache. Either part may be absent. */
interface SharedPayload {
  photo: File | null;
  text: string;
}

/**
 * Web Share Target landing page.
 *
 * When the app is installed as a PWA it registers a `share_target` in the manifest pointing at `/share`.
 * There are two ways content reaches here:
 *
 *   1. POST share (a PHOTO and/or text): the manifest's `share_target` is `method: POST`,
 *      `enctype: multipart/form-data`. The OS POSTs the share to `/share`; the service worker intercepts
 *      it, stashes the photo Blob at cache `shared-media` / `"/__shared/photo"` and the joined text at
 *      `"/__shared/text"`, then 303-redirects here to `/share?shared=1`. On `shared=1` we read (and DELETE)
 *      both keys: a PHOTO → open Add-food in PHOTO mode with the File pre-loaded so its AI photo-parse runs
 *      on it; otherwise the TEXT → the existing Describe-prefill.
 *   2. GET fallback (`?title=&text=&url=`): some shares/links still arrive as a GET. We assemble the
 *      title/text/url into one string and use the Describe-prefill, exactly as before.
 *
 * Either way the content lands in the food tracker's {@link AddFoodDialog}; the user edits/confirms and
 * NOTHING auto-logs.
 *
 * Guards (it must never strand or surprise the user):
 *   - Unauthenticated → replace into the app's resolved home/login (the auth/permission guards take over).
 *   - No usable shared content → just go home (nothing to do).
 *   - No tracker access → go home (we don't open a dialog the user can't use).
 *
 * Every browser API here (Cache Storage especially) is feature-detected and guarded: a missing/blocked
 * cache just degrades to "no photo" and falls through to the text/GET path or a quiet redirect home.
 */
@Component({
  selector: 'app-share-target',
  standalone: true,
  imports: [MatProgressSpinnerModule],
  template: `
    <div class="st-splash" aria-live="polite" aria-label="Processing your share…">
      <span class="st-orb" aria-hidden="true">
        <mat-spinner diameter="32" />
      </span>
      <p class="st-label">Opening tracker…</p>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100dvh;
      background: var(--tech-surface, var(--tech-bg, #0d1117));
    }
    .st-splash {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 16px;
      padding: 40px 24px;
    }
    .st-orb {
      display: grid;
      place-items: center;
      width: 64px;
      height: 64px;
      border-radius: 50%;
      background: color-mix(in srgb, var(--tech-accent, #7c8cff) 14%, transparent);
      mat-spinner { --mdc-circular-progress-active-indicator-color: var(--tech-accent, #7c8cff); }
    }
    .st-label {
      margin: 0;
      font-family: var(--tech-font-display, inherit);
      font-size: 0.9rem;
      font-weight: 600;
      letter-spacing: 0.04em;
      color: var(--tech-text-secondary, #9ba9bd);
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ShareTargetPage implements OnInit {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private auth = inject(AuthService);
  private dialog = inject(MatDialog);

  ngOnInit(): void {
    // All async work funnels through handleShare; any surprise resolves to a quiet redirect home.
    void this.handleShare().catch(() => this.goHome());
  }

  /**
   * Drive the whole share landing: auth/permission guards, read any POST-share payload from the SW cache,
   * fall back to the GET query, then route a photo or text into Add-food (or home when there's nothing).
   */
  private async handleShare(): Promise<void> {
    // Unauthenticated (or expired session) → hand off to the normal landing; the route guards bounce an
    // unauthenticated caller to /login. replaceUrl so /share never sits in history.
    if (!this.auth.isAuthenticated()) {
      this.goHome();
      return;
    }

    // A POST share is signalled by the SW's 303 → /share?shared=1; read (and clear) the stashed media.
    const sharedFlag = this.route.snapshot.queryParamMap.get('shared') === '1';
    const payload = sharedFlag ? await this.readSharedMedia() : { photo: null, text: '' };

    // Prefer the POST text; else fall back to the GET ?title=/?text=/?url= query (some shares/links).
    const text = payload.text || this.assembleSharedText();
    const photo = payload.photo;

    // Nothing usable, or no tracker access → nothing meaningful to do; just land home.
    if ((!photo && !text) || !this.auth.hasPermission(PERM.trackerSelf)) {
      this.goHome();
      return;
    }

    // Land on the tracker (replaceUrl drops /share from history), then open Add-food. A photo opens it in
    // PHOTO mode (its AI parse runs on the File); text-only keeps the Describe-prefill. We open it ourselves
    // rather than touching the tracker page. The dialog logs nothing unless the user confirms.
    await this.router
      .navigate(['/tracker'], { replaceUrl: true })
      .then(() => this.openAddFood(photo, text))
      .catch(() => {
        /* navigation cancelled (e.g. a guard) — nothing to clean up */
      });
  }

  /**
   * Read a POST-shared photo + text out of the SW's `shared-media` cache, DELETING both keys after (a
   * one-shot handoff). Fully guarded: no Cache Storage / no match / a read error all degrade to empty so
   * the caller falls through to the text/GET path. Never throws.
   */
  private async readSharedMedia(): Promise<SharedPayload> {
    const empty: SharedPayload = { photo: null, text: '' };
    try {
      if (typeof caches === 'undefined') return empty; // Cache Storage unsupported/blocked.
      const cache = await caches.open(SHARED_MEDIA_CACHE);

      let photo: File | null = null;
      try {
        const photoRes = await cache.match(SHARED_PHOTO_KEY);
        if (photoRes) {
          const blob = await photoRes.blob();
          if (blob && blob.size > 0) {
            const type = blob.type || 'image/jpeg';
            photo = new File([blob], this.fileNameFor(type), { type });
          }
        }
      } catch {
        /* unreadable photo entry → treat as no photo */
      }

      let text = '';
      try {
        const textRes = await cache.match(SHARED_TEXT_KEY);
        if (textRes) text = (await textRes.text()).trim();
      } catch {
        /* unreadable text entry → treat as no text */
      }

      // One-shot handoff: drop both keys so a refresh / re-open doesn't replay this share.
      try {
        await cache.delete(SHARED_PHOTO_KEY);
        await cache.delete(SHARED_TEXT_KEY);
      } catch {
        /* best-effort cleanup */
      }

      return { photo, text };
    } catch {
      return empty;
    }
  }

  /** Pick a sensible filename + extension for the shared photo from its mime type (defaults to .jpg). */
  private fileNameFor(type: string): string {
    const ext = type === 'image/png' ? 'png' : type === 'image/webp' ? 'webp' : 'jpg';
    return `shared.${ext}`;
  }

  /**
   * Fold the (all-optional) GET title/text/url params into a single, de-duplicated text string. A share
   * may carry any subset (e.g. a browser "share page" sends title+url; a notes app sends text). We keep the
   * order title → text → url and drop a url that's already echoed inside the text so we don't seed dupes.
   */
  private assembleSharedText(): string {
    const params = this.route.snapshot.queryParamMap;
    const title = (params.get('title') ?? '').trim();
    const text = (params.get('text') ?? '').trim();
    const url = (params.get('url') ?? '').trim();

    const parts: string[] = [];
    if (title) parts.push(title);
    if (text) parts.push(text);
    // Only append the url if neither the title nor the text already contains it (common for page shares).
    if (url && !title.includes(url) && !text.includes(url)) parts.push(url);

    return parts.join('\n').trim();
  }

  /**
   * Open the existing Add-food dialog: a shared PHOTO pre-loads it in Photo mode (its AI parse runs on the
   * File); otherwise the shared text seeds Describe mode (mirrors Tracker.openAddFood). Both are prefills
   * the user edits/confirms — nothing auto-logs.
   */
  private openAddFood(photo: File | null, text: string): void {
    const data: AddFoodData = {
      date: toLocalDate(new Date()),
      meal: this.defaultMeal(),
      ...(photo ? { prefillPhoto: photo } : text ? { prefillQuery: text } : {}),
    };
    this.dialog.open(AddFoodDialog, {
      data,
      width: '500px',
      maxWidth: '95vw',
      panelClass: 'tracker-dialog',
      autoFocus: false,
    });
  }

  /** A sensible default meal slot by local time of day (matches the tracker's quick-add heuristic). */
  private defaultMeal(): AddFoodData['meal'] {
    const h = new Date().getHours();
    if (h < 11) return 'breakfast';
    if (h < 15) return 'lunch';
    if (h < 21) return 'dinner';
    return 'snack';
  }

  /** Quiet redirect to the resolved home (never strand the user on a blank /share page). */
  private goHome(): void {
    try {
      void this.router.navigateByUrl(normalizeHome(this.auth.homeRoute()), { replaceUrl: true });
    } catch {
      try {
        void this.router.navigateByUrl('/', { replaceUrl: true });
      } catch {
        /* give up silently */
      }
    }
  }
}
