import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { CartService } from '../core/cart.service';
import { OrdersApi } from '../core/orders.api';
import { CheckoutSteps } from '../shared/checkout-steps';
import { MoneyPipe } from '../shared/money.pipe';

/** Una clave por intento de compra (crypto.randomUUID solo existe en contextos seguros: https o localhost). */
const newKey = () => (globalThis.crypto?.randomUUID?.() ?? `k-${Date.now()}-${Math.random().toString(36).slice(2)}`);

/** Checkout, pasos 1 a 4: carrito, comprador, pago simulado y confirmacion. El paso 5 (seguimiento) es /tienda/pedido/:id. */
@Component({
  selector: 'app-checkout',
  imports: [RouterLink, CheckoutSteps, MoneyPipe],
  templateUrl: './checkout.page.html',
  styleUrl: './checkout.page.scss',
})
export class CheckoutPage {
  protected readonly cart = inject(CartService);
  protected readonly auth = inject(AuthService);
  private readonly api = inject(OrdersApi);
  private readonly router = inject(Router);

  protected readonly step = signal(1);
  protected readonly sending = signal(false);
  protected readonly error = signal('');

  /** Si el POST se repite con esta misma clave (doble clic, reintento tras un corte de red) la API devuelve el mismo pedido. */
  private readonly idempotencyKey = newKey();

  protected go(delta: number): void {
    this.error.set('');
    this.step.update((s) => Math.min(4, Math.max(1, s + delta)));
  }

  protected confirm(): void {
    if (this.sending() || !this.cart.count()) return;
    this.sending.set(true);
    this.error.set('');
    const items = this.cart.lines().map((l) => ({ productId: l.productId, quantity: l.quantity }));
    this.api.create(items, this.idempotencyKey).subscribe({
      next: (order) => {
        // Si el pedido fue rechazado el carrito se conserva para poder reintentar; si no, ya se compro.
        if (order.status !== 'Rechazado') this.cart.clear();
        void this.router.navigate(['/tienda/pedido', order.id]);
      },
      error: (e: HttpErrorResponse) => {
        this.error.set(this.message(e));
        this.sending.set(false);
      },
    });
  }

  private message(e: HttpErrorResponse): string {
    if (e.status === 0) return 'No pudimos conectar con el servidor. Revisá que la API esté levantada.';
    if (e.status === 400) return e.error?.detail ?? 'El pedido no es válido.';
    if (e.status === 401 || e.status === 403) return 'Tu sesión venció o no tenés permiso para comprar. Volvé a iniciar sesión.';
    return 'Ocurrió un error inesperado. Probá de nuevo en unos minutos.';
  }
}
