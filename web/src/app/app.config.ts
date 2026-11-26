import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { AuthService } from './core/auth.service';
import { authInterceptor } from './core/auth.interceptor';
import { errorInterceptor } from './core/error.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    // El de errores va primero: asi ve la respuesta final y, al reintentar, cada intento vuelve a pasar por el de auth (token vigente).
    provideHttpClient(withInterceptors([errorInterceptor, authInterceptor])),
    provideAppInitializer(() => inject(AuthService).init()),
  ],
};
