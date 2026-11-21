import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AdminOrderSummary, AdminOrdersApi, AdminTargetStatus } from '../core/admin-orders.api';
import { Order, OrderStatus } from '../core/orders.api';
import { Paged } from '../core/products.api';
import { readProblem } from '../core/problem';
import { MoneyPipe } from '../shared/money.pipe';

const STATUSES: OrderStatus[] = ['Pendiente', 'Confirmado', 'Rechazado', 'Entregado', 'Cancelado'];

const REASONS: Record<string, string> = {
  SinStock: 'Sin stock suficiente',
  StockNoDisponible: 'Servicio de stock no disponible',
  PagoFallido: 'Pago rechazado',
  PagoNoDisponible: 'Servicio de pagos no disponible',
  BrokerNoDisponible: 'Cola de mensajes no disponible',
};

/** Admin · Pedidos: listado paginado con filtro por estado, detalle de cada pedido y paso a Entregado / Cancelado. */
@Component({
  selector: 'app-admin-orders',
  imports: [FormsModule, MoneyPipe, DatePipe],
  templateUrl: './orders.page.html',
  styleUrl: './orders.page.scss',
})
export class AdminOrdersPage implements OnInit {
  private readonly api = inject(AdminOrdersApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly statuses = STATUSES;
  protected status: OrderStatus | '' = '';
  protected readonly page = signal(1);
  protected readonly result = signal<Paged<AdminOrderSummary> | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  /** Mensaje de la ultima accion (exito o error), visible arriba de la tabla. */
  protected readonly notice = signal<{ ok: boolean; text: string } | null>(null);
  /** Pedido con una accion en curso (deshabilita sus botones). */
  protected readonly busyId = signal<string | null>(null);

  /** Pedido con el detalle abierto (uno a la vez). */
  protected readonly openId = signal<string | null>(null);
  protected readonly detail = signal<Order | null>(null);
  protected readonly detailError = signal('');

  ngOnInit(): void {
    // Filtro y pagina en la URL: al recargar o volver se recupera el mismo listado.
    const qp = this.route.snapshot.queryParamMap;
    const s = qp.get('status') as OrderStatus | null;
    this.status = s && STATUSES.includes(s) ? s : '';
    const page = Number(qp.get('page'));
    this.page.set(Number.isInteger(page) && page > 0 ? page : 1);
    this.load();
  }

  protected get pages(): number {
    const r = this.result();
    return r ? Math.max(1, Math.ceil(r.total / r.pageSize)) : 1;
  }

  protected onFilter(): void {
    this.page.set(1);
    this.closeDetail();
    this.load();
  }

  protected go(delta: number): void {
    this.page.update((p) => p + delta);
    this.closeDetail();
    this.load();
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set('');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { status: this.status || null, page: this.page() > 1 ? this.page() : null },
      replaceUrl: true,
    });
    this.api.list(this.status, this.page()).subscribe({
      next: (r) => {
        // Si la URL traia una pagina que ya no existe se vuelve a la ultima valida.
        const last = Math.max(1, Math.ceil(r.total / r.pageSize));
        if (this.page() > last) {
          this.page.set(last);
          this.load();
          return;
        }
        this.result.set(r);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => {
        this.error.set(readProblem(e).message);
        this.loading.set(false);
      },
    });
  }

  // ---- Detalle ----

  protected toggle(o: AdminOrderSummary): void {
    if (this.openId() === o.id) return this.closeDetail();
    this.openId.set(o.id);
    this.detail.set(null);
    this.detailError.set('');
    this.api.get(o.id).subscribe({
      next: (d) => {
        if (this.openId() === o.id) this.detail.set(d);
      },
      error: (e: HttpErrorResponse) => {
        if (this.openId() === o.id) this.detailError.set(readProblem(e).message);
      },
    });
  }

  private closeDetail(): void {
    this.openId.set(null);
    this.detail.set(null);
    this.detailError.set('');
  }

  // ---- Cambio de estado ----

  protected change(o: AdminOrderSummary, to: AdminTargetStatus): void {
    const question =
      to === 'Cancelado'
        ? `¿Cancelar el pedido de ${o.buyerName}? Se libera el stock reservado y no se puede deshacer.`
        : `¿Marcar como entregado el pedido de ${o.buyerName}?`;
    if (!confirm(question)) return;
    this.busyId.set(o.id);
    this.api.changeStatus(o.id, to).subscribe({
      next: (updated) => {
        this.busyId.set(null);
        this.notice.set({ ok: true, text: `Pedido de ${o.buyerName}: ${to === 'Cancelado' ? 'cancelado, stock liberado' : 'marcado como entregado'}.` });
        if (this.status && this.status !== updated.status) {
          // Con un filtro activo el pedido ya no pertenece a la lista: se vuelve a pedir.
          this.closeDetail();
          this.load();
          return;
        }
        this.result.update((r) =>
          r ? { ...r, items: r.items.map((x) => (x.id === o.id ? { ...x, status: updated.status, reason: updated.reason } : x)) } : r,
        );
        if (this.openId() === o.id) this.detail.set(updated);
      },
      error: (e: HttpErrorResponse) => {
        this.busyId.set(null);
        this.notice.set({ ok: false, text: readProblem(e).message });
        // 409 / 404: el pedido cambio mientras se miraba la lista; se recarga para mostrar el estado real.
        if (e.status === 409 || e.status === 404) this.load();
      },
    });
  }

  protected reasonText(reason: string | null): string {
    return reason ? (REASONS[reason] ?? reason) : '';
  }

  protected shortId(id: string): string {
    return id.slice(0, 8);
  }
}
