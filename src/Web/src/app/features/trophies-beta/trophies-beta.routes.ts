import { Routes } from '@angular/router';

import { permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * Beta Trophies — the mobile-first, premium "Achievements" surface over the caller's OWN milestone
 * badges (the same data the live /trophies wall renders). Lazy `loadComponent` so the celebratory page
 * stays out of the initial bundle.
 *
 * GATE — mirrors the live `/trophies` route VERBATIM plus the Beta-section gate:
 *   - `platform.mobile`  → the Beta section (the hub link + the dropdown + a direct nav all re-check it),
 *   - `tracker.self` → the SAME permission the live `/trophies` route carries (and the `GET /api/trophies`
 *     endpoint enforces server-side). Stacked so a direct nav to `/beta/trophies` re-checks both.
 *
 * Purely additive + read-only: reuses the existing `Api.trophies()` method + `TrophiesResponse`/`TrophyBadgeDto`
 * types and touches NO live page. The response carries the caller's display NAME + userId only — never an email.
 */
export const TROPHIES_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.platformMobile), permissionGuard(PERM.trackerSelf)],
    loadComponent: () => import('./trophies-beta.page').then(m => m.TrophiesBetaPage),
    title: 'Usage IQ · Trophies',
  },
];
