import { inject, Injectable } from '@angular/core';
import { SwPush } from '@angular/service-worker';
import { firstValueFrom } from 'rxjs';

import { Api } from './api';

/**
 * Web-push (PWA background notifications) bridge. This is the ALWAYS-ON background surface that
 * complements the SignalR live notifications (which require an open tab): once the user has enabled
 * "Browser notifications" AND granted OS permission, we subscribe the device with the server's VAPID
 * key and POST it to `/api/push/subscribe`. The backend then fans push out alongside in-app + Discord.
 *
 * Conservative + privacy-respecting:
 *   • We NEVER request OS permission here — that happens only in the user's click gesture in the
 *     notification preferences UI. {@link subscribe} no-ops unless permission is already 'granted'.
 *   • If web-push is unconfigured server-side (`/api/push/vapid-public` → 404) we silently skip; the
 *     SignalR/in-app surfaces are unaffected.
 *   • Everything swallows errors: push is a best-effort enhancement, never a hard dependency. A failure
 *     to subscribe must never break the toggle the user just flipped.
 *
 * The service worker's own `push` handler reads the payload as `{ title, body, url }` (the exact shape
 * the backend sender serializes) — see {@link SwUpdateService} registration in app.config.
 */
@Injectable({ providedIn: 'root' })
export class PushNotifications {
  private swPush = inject(SwPush);
  private api = inject(Api);

  /** True when the SW + Push API are available in this browser/context (false in dev/no-SW/unsupported). */
  get supported(): boolean {
    return this.swPush.isEnabled && typeof Notification !== 'undefined';
  }

  /**
   * Subscribe this device for web push and register it with the server. Safe to call repeatedly
   * (idempotent upsert by endpoint). No-ops — returning false — when:
   *   • the SW/Push API isn't available,
   *   • OS notification permission isn't already 'granted' (we don't prompt here), or
   *   • web-push is unconfigured server-side (vapid-public 404).
   * Never throws.
   */
  async subscribe(): Promise<boolean> {
    if (!this.supported || Notification.permission !== 'granted') return false;

    let publicKey: string;
    try {
      const res = await firstValueFrom(this.api.vapidPublicKey());
      publicKey = res.publicKey;
    } catch {
      // 404 (unconfigured) or transient — push simply stays off; the live surfaces still work.
      return false;
    }
    if (!publicKey) return false;

    let sub: PushSubscription;
    try {
      // requestSubscription reuses the existing browser subscription if one already exists for this key.
      sub = await this.swPush.requestSubscription({ serverPublicKey: publicKey });
    } catch {
      return false;
    }

    try {
      const json = sub.toJSON();
      const p256dh = json.keys?.['p256dh'];
      const auth = json.keys?.['auth'];
      if (!sub.endpoint || !p256dh || !auth) return false;
      await firstValueFrom(this.api.pushSubscribe({ endpoint: sub.endpoint, keys: { p256dh, auth } }));
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Unsubscribe this device: tell the server to drop the row (caller-scoped, idempotent) and tear down
   * the browser subscription. Never throws. Called when the user turns "Browser notifications" off.
   */
  async unsubscribe(): Promise<void> {
    if (!this.supported) return;

    // Best-effort: read the current endpoint BEFORE unsubscribing so we can prune the server row.
    let endpoint: string | null = null;
    try {
      const current = await firstValueFrom(this.swPush.subscription);
      endpoint = current?.endpoint ?? null;
    } catch {
      endpoint = null;
    }

    if (endpoint) {
      try {
        await firstValueFrom(this.api.pushUnsubscribe(endpoint));
      } catch {
        /* idempotent server-side; ignore */
      }
    }

    try {
      await this.swPush.unsubscribe();
    } catch {
      /* no active subscription — fine */
    }
  }
}
