import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { CONFIG } from './config';

export type OrderStatus = 'Pendiente' | 'Confirmado' | 'Rechazado' | 'Entregado' | 'Cancelado';

export interface OrderLine {
  productId: number;
  productName: string;
  unitPriceCents: number;
  quantity: number;
}

export interface Order {
  id: string;
  status: OrderStatus;
  reason: string | null;
  totalCents: number;
  createdAt: string;
  items: OrderLine[];
  buyerName: string;
}

@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);

  /** Crea el pedido. La misma Idempotency-Key devuelve el mismo pedido (no se duplica ante un doble clic o un reintento). */
  create(items: { productId: number; quantity: number }[], idempotencyKey: string): Observable<Order> {
    return this.http.post<Order>(`${CONFIG.apiUrl}/api/v1/orders`, { items }, { headers: { 'Idempotency-Key': idempotencyKey } });
  }

  /** Pedido propio. La API responde 404 si no existe o es de otro usuario. */
  get(id: string): Observable<Order> {
    return this.http.get<Order>(`${CONFIG.apiUrl}/api/v1/orders/${encodeURIComponent(id)}`);
  }
}
