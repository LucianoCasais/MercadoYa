import { Injectable, signal } from '@angular/core';

/**
 * Aviso global de sesion vencida: lo activa el interceptor de errores ante un 401 de la API y lo muestra el encabezado
 * de la app con un boton para volver a iniciar sesion (sin perder la pantalla en la que estaba el usuario).
 */
@Injectable({ providedIn: 'root' })
export class SessionAlert {
  readonly expired = signal(false);

  show(): void {
    this.expired.set(true);
  }

  dismiss(): void {
    this.expired.set(false);
  }
}
