import { Routes } from '@angular/router';

import { permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * Hub Wrapped — the mobile-first "year in the Hub" highlight-reel of the caller's OWN data over a chosen
 * period (month / year / all-time). Lazy `loadComponent` so the celebratory page stays out of the initial
 * bundle. Guarded by `beta.access` (the Beta section); the underlying `/api/wrapped` endpoint is itself
 * gated server-side by `tracker.self` and only ever returns the caller's own derived numbers — no email,
 * no secret. Purely additive + read-only: reuses the existing `Api.wrapped()` method and touches NO live page.
 */
export const WRAPPED_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.betaAccess)],
    loadComponent: () => import('./wrapped-beta.page').then(m => m.WrappedBetaPage),
    title: 'Usage IQ · Wrapped',
  },
];
