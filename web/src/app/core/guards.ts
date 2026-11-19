import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService, Role } from './auth.service';

/** Exige sesion y el rol indicado. Sin sesion -> login de Keycloak (401); con sesion pero sin rol -> /403. */
export const roleGuard = (role: Role): CanActivateFn => async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.authenticated()) {
    await auth.login(state.url);
    return false;
  }
  return auth.hasRole(role) ? true : router.parseUrl('/403');
};
