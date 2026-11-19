import { Component } from '@angular/core';

/** Marcador de posicion: el ABM de productos y el panel de pedidos llegan en los dias 5 a 7 y 11 de la guia. */
@Component({
  selector: 'app-admin',
  template: `<div class="empty card">
    <h1>Administración</h1>
    <p class="muted">Acceso verificado con el rol <b>admin</b>. El ABM de productos llega en los próximos días de la guía.</p>
  </div>`,
})
export class AdminPage {}
