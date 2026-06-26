import { Routes } from '@angular/router';

import { anyPermissionGuard, permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';

/**
 * People "Circle" beta route — the NEW mobile-first SOCIAL roster that re-imagines the live `/people`
 * grid into a native-app, online-first list for 390px. Lazy `loadComponent` keeps the page + its
 * subcomponents out of the initial bundle.
 *
 * Gating MIRRORS the live `/people` route exactly, but additionally stacks the Beta section gate:
 *   - `beta.access`            — the Beta section page-gate (a hard `permissionGuard`, must hold)
 *   - any-of `chat.read | family.use` — the SAME aggregation gate as GET /api/people / the live `/people`
 *     route (`anyPermissionGuard(PERM.chatRead, PERM.familyUse)`), so a chat-only caller still sees just
 *     their contacts and a family-only caller still sees just their household.
 * BOTH guards run and either failing blocks, so a direct nav to `/beta/people` is never less strict than
 * the live `/people` page it mirrors (the "stack both" pattern, like /beta/meals + /beta/family).
 *
 * HARD ISOLATION: purely additive + read-only over the existing endpoints. It reuses GET /api/people
 * (the contacts ∪ household aggregation, de-duplicated over the single AppUser spine + live presence),
 * POST /api/chat/direct (the DM deep-link) and POST /api/nudge (the canned nudge) — exactly the writes
 * the live page already performs. It never modifies any live page and defines its OWN rose signature
 * accent on `:host` (never the global `--tech-*` tokens). State lives in the page's own signals; the
 * only route-level provider is its own ToastController.
 */
export const PEOPLE_BETA_ROUTES: Routes = [
  {
    path: '',
    canActivate: [
      permissionGuard(PERM.betaAccess),
      anyPermissionGuard(PERM.chatRead, PERM.familyUse),
    ],
    loadComponent: () => import('./people-beta.page').then(m => m.PeopleBetaPage),
    title: 'Usage IQ · People Circle',
  },
];
