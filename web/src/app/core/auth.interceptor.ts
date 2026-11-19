import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthService } from './auth.service';
import { CONFIG } from './config';

/** Agrega `Authorization: Bearer` solo a las llamadas hacia nuestra API (nunca a otros dominios). */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(CONFIG.apiUrl)) return next(req);
  const auth = inject(AuthService);
  return from(auth.getToken()).pipe(
    switchMap((token) => next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req)),
  );
};
