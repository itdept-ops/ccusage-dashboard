import { Routes } from '@angular/router';

import { permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * Automations "Relay" beta route — the NEW mobile-first if-this-then-that surface that re-imagines the
 * live `/automations` page as a native-app RULES screen for 390px: each rule a readable WHEN → THEN flow
 * card with a native enable switch, swipe-to-toggle/delete, and a create bottom-sheet. Lazy
 * `loadComponent` keeps the page + its subcomponents out of the initial bundle.
 *
 * Guarded by BOTH `beta.access` (the Beta section) AND `automations.use` (the feature) — both guards run
 * and either failing blocks, so a direct nav to `/beta/automations` is never less strict than the live
 * `/automations` page it mirrors. This is the "stack both, like /beta/meals" pattern.
 *
 * HARD ISOLATION: purely additive. It reuses the caller's-own Rules endpoints READ + the existing
 * create/update/delete writes — it never modifies any live page and defines its OWN Relay orange
 * (amber → red) signature accent on `:host` (never the global `--tech-*` tokens). State lives entirely in
 * the page's own signals, so no route-level provider is needed beyond the page's own ToastController.
 */
export const AUTOMATIONS_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.betaAccess), permissionGuard(PERM.automationsUse)],
    loadComponent: () => import('./automations-beta.page').then(m => m.AutomationsBetaPage),
    title: 'Usage IQ · Automations Relay',
  },
];
