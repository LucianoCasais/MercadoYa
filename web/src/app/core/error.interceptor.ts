import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, retry, throwError, timer } from 'rxjs';
import { CONFIG } from './config';
import { isTransient } from './problem';
import { SessionAlert } from './session-alert';

/** Lecturas que no se reintentan solas (por ejemplo, un sondeo que ya maneja sus propios reintentos). */
export const NO_RETRY = new HttpContextToken<boolean>(() => false);

/** Reintentos automaticos de una lectura ante una falla transitoria: a 1 s y a 2 s. */
const RETRIES = 2;

/**
 * Manejo comun de errores de nuestra API (las pantallas siguen mostrando su propio estado de error):
 * - Sin conexion o servicio no disponible (0, 502, 503, 504): las lecturas (GET) se reintentan dos veces antes de fallar.
 *   Las escrituras nunca se reintentan aca: la compra ya tiene su Idempotency-Key y la pantalla decide.
 * - 401: la sesion vencio o el token no sirve; se avisa arriba con un boton para volver a iniciar sesion.
 * - 403: el usuario no tiene el rol; se muestra la pantalla 403 (la URL no cambia, asi Atras sigue funcionando).
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(CONFIG.apiUrl)) return next(req);
  const session = inject(SessionAlert);
  const router = inject(Router);
  const canRetry = req.method === 'GET' && !req.context.get(NO_RETRY);

  return next(req).pipe(
    retry({
      count: canRetry ? RETRIES : 0,
      delay: (e, attempt) => (e instanceof HttpErrorResponse && isTransient(e.status) ? timer(attempt * 1000) : throwError(() => e)),
    }),
    catchError((e: unknown) => {
      if (e instanceof HttpErrorResponse) {
        if (e.status === 401) session.show();
        else if (e.status === 403) void router.navigateByUrl('/403', { skipLocationChange: true });
      }
      return throwError(() => e);
    }),
  );
};
