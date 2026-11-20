import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
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

/** Paso 5 del checkout: seguimiento del pedido (/tienda/pedido/:id). Si esta Pendiente consulta cada 1 s, hasta 30 veces. */
@Component({
  selector: 'app-order',
  imports: [RouterLink, CheckoutSteps, MoneyPipe, DatePipe],
  templateUrl: './order.page.html',
  styleUrl: './order.page.scss',
})
export class OrderPage {
  private readonly api = inject(OrdersApi);

  readonly id = input.required<string>();

  protected readonly order = signal<Order | null>(null);
  protected readonly loading = signal(true);
  protected readonly notFound = signal(false);
  protected readonly error = signal('');
  protected readonly waiting = signal(false);

  private timer?: ReturnType<typeof setTimeout>;
  private polls = 0;

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
    this.loading.set(true);
    this.notFound.set(false);
    this.error.set('');
    this.fetch(id);
  }

  private fetch(id: string): void {
    this.api.get(id).subscribe({
      next: (o) => {
        this.order.set(o);
        this.loading.set(false);
        this.error.set('');
        if (o.status === 'Pendiente' && this.polls < 30) {
          this.waiting.set(true);
          this.polls++;
          this.timer = setTimeout(() => this.fetch(id), 1000);
        } else {
          this.waiting.set(false);
        }
      },
      error: (e: HttpErrorResponse) => {
        this.loading.set(false);
        this.waiting.set(false);
        if (e.status === 404) this.notFound.set(true);
        else this.error.set('No pudimos consultar el pedido. ¿Está levantada la API?');
      },
    });
  }
}
