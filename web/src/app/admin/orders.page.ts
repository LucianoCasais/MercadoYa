import { Component } from '@angular/core';

/** Marcador de posicion: la gestion de pedidos en Angular la hace el Integrante B (dia 7). La API ya existe en /api/v1/admin/orders. */
@Component({
  selector: 'app-admin-orders',
  template: `<div class="empty card">
    <h1>Pedidos</h1>
    <p class="muted">La gestión de pedidos se incorpora en los próximos días.</p>
  </div>`,
})
export class AdminOrdersPage {}
