import { Routes } from '@angular/router';

import { anyPermissionGuard, permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * BETA FLEET route — the NEW mobile-first "Fleet" surface: a premium, native-app view of the caller
 * machines + reporters over the SAME fleet rollup the live `/fleet` page shows. Lazy `loadComponent`
 * keeps the page + its subcomponents out of the initial bundle.
 *
 * Guarded by `platform.mobile` (the Beta section gate) AND any-of [`fleet.view`, `reporter.manage`] — this
 * MIRRORS the live `/fleet` gate (`anyPermissionGuard(fleet.view, reporter.manage)`) and stacks the beta
 * gate on top, so a direct nav to `/beta/fleet` is never less strict than the live page it mirrors. Both
 * canActivate entries must pass.
 *
 * HARD ISOLATION: purely additive. It reuses the read-only `Api.fleet(filter)` endpoint + the `Fleet`/
 * `FleetMachine`/`FleetUser` DTOs — it never modifies any live page and defines its OWN cyan signature
 * accent on `:host` (never the global `--tech-*` tokens). State lives entirely in the page's own signals,
 * so no route-level provider is needed beyond the page's own ToastController.
 */
export const FLEET_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.platformMobile), anyPermissionGuard(PERM.fleetView, PERM.reporterManage)],
    loadComponent: () => import('./fleet-beta.page').then(m => m.FleetBetaPage),
    title: 'Usage IQ · Fleet Beta',
  },
];
