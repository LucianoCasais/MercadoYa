import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Marcador de posicion: el checkout de 5 pasos se construye el dia 8 de la guia. */
@Component({
  selector: 'app-checkout',
  imports: [RouterLink],
  template: `<div class="empty card">
    <h1>Checkout</h1>
    <p class="muted">Iniciaste sesión como cliente. Los pasos del checkout llegan en el día 8 de la guía.</p>
    <a class="btn" routerLink="/tienda/carrito">Volver al carrito</a>
  </div>`,
})
export class CheckoutPage {}
