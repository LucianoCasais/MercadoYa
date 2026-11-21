import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { CONFIG } from './config';
import { Order, OrderStatus } from './orders.api';
import { Paged } from './products.api';

/** Fila del listado de pedidos del admin (sin los items: el detalle se pide aparte). */
export interface AdminOrderSummary {
  id: string;
  buyerName: string;
  status: OrderStatus;
  reason: string | null;
  totalCents: number;
  createdAt: string;
  updatedAt: string;
  itemCount: number;
}

/** Estados a los que el admin puede pasar un pedido Confirmado. */
export type AdminTargetStatus = 'Entregado' | 'Cancelado';

/** /api/v1/admin/orders/** (rol admin). El interceptor agrega el Bearer. */
@Injectable({ providedIn: 'root' })
export class AdminOrdersApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${CONFIG.apiUrl}/api/v1/admin/orders`;

  /** Mas nuevos primero; `status` vacio = todos. */
  list(status: OrderStatus | '', page: number, pageSize = 20): Observable<Paged<AdminOrderSummary>> {
    const params: Record<string, string> = { page: String(page), pageSize: String(pageSize) };
    if (status) params['status'] = status;
    return this.http.get<Paged<AdminOrderSummary>>(this.base, { params });
  }

  get(id: string): Observable<Order> {
    return this.http.get<Order>(`${this.base}/${encodeURIComponent(id)}`);
  }

  /** Confirmado -> Entregado | Cancelado. Cancelar libera el stock; 409 si el pedido ya no esta Confirmado. */
  changeStatus(id: string, status: AdminTargetStatus): Observable<Order> {
    return this.http.patch<Order>(`${this.base}/${encodeURIComponent(id)}/status`, { status });
  }
}
