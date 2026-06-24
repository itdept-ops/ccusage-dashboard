import { Injectable, inject } from '@angular/core';
import { catchError, firstValueFrom, of } from 'rxjs';

import { Api } from './api';
import { ClientInfoRequest } from './models';

/**
 * Best-effort web client-info capture, PRIVACY-RESPECTING.
 *
 * Gathers only device/agent characteristics the browser already exposes to every page (platform, screen
 * geometry, devicePixelRatio, languages, IANA time zone, hardwareConcurrency, deviceMemory, touch points,
 * colour depth) and POSTs them once, right after a successful sign-in, so the per-user login history +
 * Fleet machine-details can show what device a session came from. NEVER touches geolocation and carries
 * NO precise location — that is the separate, opt-in {@link LocationCapture} flow. NO permission prompt.
 *
 * Hard rules:
 *  - One-shot per login (idempotent within a session via {@link sent}); never polls.
 *  - Every probe is Try-guarded → the field is simply omitted on failure (the server leaves the stored
 *    value unchanged for an absent field).
 *  - Fully fire-and-forget: any network/serialization failure is swallowed. It can NEVER block or fail an
 *    otherwise-valid sign-in (the server side 200s even on a no-op, too).
 */
@Injectable({ providedIn: 'root' })
export class ClientInfoCapture {
  private api = inject(Api);

  /** True once we've POSTed for the current session, so a repeated start() is a no-op. */
  private sent = false;

  /**
   * Collect + POST the caller's web client info exactly once for this session. Safe to call repeatedly
   * (only the first call does work). All failures are swallowed — this is a best-effort nicety.
   */
  async capture(): Promise<void> {
    if (this.sent) return;
    this.sent = true;
    try {
      const body = ClientInfoCapture.collect();
      // Nothing useful to report (every probe failed) — don't bother the server.
      if (Object.keys(body).length === 0) return;
      await firstValueFrom(this.api.clientInfo(body).pipe(catchError(() => of(null))));
    } catch {
      // Best-effort: client-info is never blocking. Swallow and move on.
    }
  }

  /** Reset the one-shot latch (call on sign-out so the next session re-captures). */
  reset(): void {
    this.sent = false;
  }

  /**
   * Read the browser characteristics into a request body. Each field is probed independently and only
   * added when it resolves to a finite/non-empty value, so a missing API (e.g. deviceMemory off Chromium)
   * just omits that one field. Never throws.
   */
  private static collect(): ClientInfoRequest {
    const body: ClientInfoRequest = {};
    if (typeof navigator === 'undefined') return body;

    const numIf = (v: unknown): number | undefined =>
      typeof v === 'number' && Number.isFinite(v) ? v : undefined;
    const strIf = (v: unknown): string | undefined => {
      const s = typeof v === 'string' ? v.trim() : '';
      return s.length ? s : undefined;
    };

    try { const p = strIf((navigator as Navigator).platform); if (p) body.platform = p; } catch { /* ignore */ }

    try {
      const langs = (navigator as Navigator).languages;
      const joined = Array.isArray(langs) && langs.length ? langs.join(',') : strIf((navigator as Navigator).language);
      const v = strIf(joined);
      if (v) body.languages = v;
    } catch { /* ignore */ }

    try {
      const hc = numIf((navigator as Navigator).hardwareConcurrency);
      if (hc !== undefined) body.hardwareConcurrency = hc;
    } catch { /* ignore */ }

    try {
      // deviceMemory is a non-standard (Chromium) Navigator extension.
      const dm = numIf((navigator as Navigator & { deviceMemory?: number }).deviceMemory);
      if (dm !== undefined) body.deviceMemory = dm;
    } catch { /* ignore */ }

    try {
      const tp = numIf((navigator as Navigator).maxTouchPoints);
      if (tp !== undefined) body.touchPoints = tp;
    } catch { /* ignore */ }

    try {
      if (typeof screen !== 'undefined') {
        const w = numIf(screen.width); if (w !== undefined) body.screenWidth = w;
        const h = numIf(screen.height); if (h !== undefined) body.screenHeight = h;
        const cd = numIf(screen.colorDepth); if (cd !== undefined) body.colorDepth = cd;
      }
    } catch { /* ignore */ }

    try {
      const dpr = numIf(typeof window !== 'undefined' ? window.devicePixelRatio : undefined);
      if (dpr !== undefined) body.devicePixelRatio = dpr;
    } catch { /* ignore */ }

    try {
      const tz = strIf(Intl?.DateTimeFormat?.().resolvedOptions?.().timeZone);
      if (tz) body.timeZone = tz;
    } catch { /* ignore */ }

    return body;
  }
}
