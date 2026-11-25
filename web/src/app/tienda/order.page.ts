import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CartService } from '../core/cart.service';
import { Order, OrdersApi } from '../core/orders.api';
import { CheckoutSteps } from '../shared/checkout-steps';
import { MoneyPipe } from '../shared/money.pipe';

const REASONS: Record<string, string> = {
  SinStock: 'No había stock suficiente de alguno de los productos.',
  StockNoDisponible: 'No pudimos verificar el stock. Probá de nuevo en unos minutos.',
  PagoFallido: 'El pago fue rechazado.',
  PagoNoDisponible: 'El servicio de pagos no está disponible en este momento.',
  BrokerNoDisponible: 'No pudimos procesar el pedido en este momento. Probá de nuevo.',
};

/** Cuantas veces se consulta un pedido Pendiente (1 por segundo: ~30 s) antes de dejar que el usuario actualice a mano. */
const MAX_POLLS = 30;
/** Fallos de red seguidos que se toleran mientras el pedido esta Pendiente (se reintenta cada 2 s) antes de mostrar el error. */
const MAX_POLL_FAILURES = 3;

/**
 * Paso 5 del checkout: seguimiento del pedido (/tienda/pedido/:id).
 * En modo asincrono el pedido nace Pendiente y lo resuelve el worker: mientras siga Pendiente se consulta cada 1 s, hasta 30 veces.
 * - Un corte de red momentaneo no corta el seguimiento: se reintenta hasta 3 veces seguidas.
 * - Si pasan los 30 s sin resolverse se avisa y se ofrece "Actualizar estado" (el pedido sigue en la cola, no se pierde).
 * - Si el pedido termina Rechazado despues de haber estado Pendiente, el carrito (que se vacio al aceptarse el pedido) se restaura.
 */
@Component({
  selector: 'app-order',
  imports: [RouterLink, CheckoutSteps, MoneyPipe, DatePipe],
  templateUrl: './order.page.html',
  styleUrl: './order.page.scss',
})
export class OrderPage {
  private readonly api = inject(OrdersApi);
  private readonly cart = inject(CartService);

  readonly id = input.required<string>();

  protected readonly order = signal<Order | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly error = signal('');
  protected readonly waiting = signal(false);
  /** Paso el tiempo de espera y el pedido sigue Pendiente. */
  protected readonly gaveUp = signal(false);
  /** Se perdio la conexion durante el seguimiento y se esta reintentando. */
  protected readonly reconnecting = signal(false);

  private timer?: ReturnType<typeof setTimeout>;
  private polls = 0;
  private failures = 0;
  private sawPending = false;

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.start(id));
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected reasonText(o: Order): string {
    return (o.reason && REASONS[o.reason]) || 'No pudimos completar el pedido.';
  }

  protected start(id: string): void {
    clearTimeout(this.timer);
    this.polls = 0;
    this.failures = 0;
    this.sawPending = false;
    this.loading.set(true);
    this.notFound.set(false);
    this.error.set('');
    this.gaveUp.set(false);
    this.reconnecting.set(false);
    this.fetch(id);
  }

  /** "Actualizar estado" despues de agotar la espera: vuelve a consultar sin perder lo que ya se mostraba. */
  protected refresh(id: string): void {
    clearTimeout(this.timer);
    this.polls = 0;
    this.failures = 0;
    this.gaveUp.set(false);
    this.fetch(id);
  }

  private fetch(id: string): void {
    this.api.get(id).subscribe({
      next: (o) => {
        this.failures = 0;
        this.reconnecting.set(false);
        this.order.set(o);
        this.loading.set(false);
        this.error.set('');
        if (o.status === 'Pendiente') {
          this.sawPending = true;
          if (this.polls < MAX_POLLS) {
            this.waiting.set(true);
            this.polls++;
            this.timer = setTimeout(() => this.fetch(id), 1000);
          } else {
            this.waiting.set(false);
            this.gaveUp.set(true);
          }
          return;
        }
        this.waiting.set(false);
        this.gaveUp.set(false);
        if (o.status === 'Rechazado' && this.sawPending) this.restoreCart(o);
      },
      error: (e: HttpErrorResponse) => {
        const transient = e.status === 0 || e.status >= 500;
        if (transient && this.order()?.status === 'Pendiente' && this.failures < MAX_POLL_FAILURES) {
          this.failures++;
          this.reconnecting.set(true);
          this.timer = setTimeout(() => this.fetch(id), 2000);
          return;
        }
        this.loading.set(false);
        this.waiting.set(false);
        this.reconnecting.set(false);
        if (e.status === 404) this.notFound.set(true);
        else this.error.set('No pudimos consultar el pedido. ¿Está levantada la API?');
      },
    });
  }

  /** El carrito se vacio cuando la API acepto el pedido (202). Si despues se rechaza, el usuario recupera lo que habia elegido. */
  private restoreCart(o: Order): void {
    this.sawPending = false;
    this.cart.restore(o.items);
  }
}
