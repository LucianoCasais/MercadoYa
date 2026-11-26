import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-forbidden',
  imports: [RouterLink],
  template: `<div class="empty">
    <h1>403 · Sin permisos</h1>
    <p>Esta sección requiere un rol que tu usuario no tiene. Ingresaste como <b>{{ auth.email() || auth.username() }}</b>.</p>
    <a class="btn" routerLink="/tienda">Volver al catálogo</a>
  </div>`,
})
export class ForbiddenPage {
  protected readonly auth = inject(AuthService);
}
