import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth';

/**
 * Bounces to /login on a 401. The app JWT is no longer attached as an Authorization header: it lives in
 * an HttpOnly, SameSite=Lax cookie the API sets at login, which the browser sends automatically on these
 * same-origin /api calls (dev goes through the Angular proxy; prod is same-origin). Keeping the token out
 * of JS closes the XSS token-theft path. The API still ALSO accepts a Bearer header (used by the test
 * suite and any non-browser client), so this is purely a client-side change.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const isApi = req.url.startsWith('/api');

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      if (err.status === 401 && isApi && !req.url.includes('/api/auth/')) {
        auth.logout();
        router.navigate(['/login']);
      }
      return throwError(() => err);
    }),
  );
};
