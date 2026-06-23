import { Routes } from '@angular/router';

import { permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * Dashboard Beta — the new mobile-first "Pulse" usage-analytics experience. Lazy `loadComponent`
 * so neither the page nor echarts land in the initial bundle. Guarded by the `beta.access`
 * permission (same gate as the hub; the brief specifies no extra per-feature perm for Pulse).
 *
 * Purely ADDITIVE: this surface re-uses the SAME `Api.summary`/`records`/`cacheEfficiency`
 * endpoints + DTOs as the live `/dashboard`, so it shows the IDENTICAL numbers for the same
 * filters (server-side dedup + sidechain semantics preserved via the shared `UsageFilter`).
 * No live page is imported or modified. State lives entirely in the page's own signals, so no
 * route-level provider is needed.
 */
export const DASHBOARD_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.betaAccess)],
    loadComponent: () => import('./dashboard-beta.page').then(m => m.DashboardBetaPage),
    title: 'Usage IQ · Dashboard Beta',
  },
];
