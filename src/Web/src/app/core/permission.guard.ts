import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth';

/**
 * Guard factory: requires authentication + a specific permission. Unauthenticated visitors go to
 * login; authenticated-but-unauthorized users are sent to '/welcome' (NOT '/', which would loop for
 * a user lacking dashboard.view).
 */
export function permissionGuard(permission: string): CanActivateFn {
  return (_route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (!auth.isAuthenticated()) {
      return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    }
    return auth.hasPermission(permission) ? true : router.createUrlTree(['/welcome']);
  };
}
