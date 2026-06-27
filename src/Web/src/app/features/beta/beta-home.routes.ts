import { Routes } from '@angular/router';

import { permissionGuard } from '../../core/permission.guard';
import { PERM } from '../../core/models';
import { AtriumLayoutStore } from './widgets/layout-store';

/**
 * Home "Atrium" beta route — the NEW cross-domain glance surface. Lazy `loadComponent` so the page and
 * its widgets stay out of the initial bundle. Guarded independently by `platform.mobile` so a direct nav to
 * `/beta/home` is protected even though the hub also links to it.
 *
 * {@link AtriumLayoutStore} is provided HERE (route level, not root) so the saved widget layout lives and
 * dies with the Atrium and never leaks into the rest of the app. The data stores (TrackerStore /
 * ChallengeStore) and Api are all `providedIn: 'root'`, so no other route provider is needed.
 */
export const BETA_HOME_ROUTES: Routes = [
  {
    path: '',
    canActivate: [permissionGuard(PERM.platformMobile)],
    providers: [AtriumLayoutStore],
    loadComponent: () => import('./beta-home.page').then(m => m.BetaHomePage),
    title: 'Usage IQ · Home',
  },
];
