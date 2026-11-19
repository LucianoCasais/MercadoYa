import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-forbidden',
  imports: [RouterLink],
  template: `<div class="empty card">
    <h1>403 · Sin permisos</h1>
    <p class="muted">Tu usuario no tiene el rol necesario para ver esta sección.</p>
    <a class="btn" routerLink="/tienda">Volver al catálogo</a>
  </div>`,
})
export class ForbiddenPage {}
