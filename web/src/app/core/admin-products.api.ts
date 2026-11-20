import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map, shareReplay } from 'rxjs';
import { CONFIG } from './config';
import { Paged } from './products.api';

/** Producto tal como lo ve el admin (incluye los inactivos). */
export interface AdminProduct {
  id: number;
  name: string;
  description: string;
  priceCents: number;
  stock: number;
  categoryId: number;
  imageUrl: string | null;
  active: boolean;
}

export interface CreateProduct {
  name: string;
  description: string;
  priceCents: number;
  categoryId: number;
  imageUrl: string | null;
  initialStock: number;
}

/** El stock no se edita aca: solo con adjustStock, que deja registro en StockMovement. */
export interface UpdateProduct {
  name: string;
  description: string;
  priceCents: number;
  categoryId: number;
  imageUrl: string | null;
  active: boolean;
}

export interface Category {
  id: number;
  name: string;
  parentId: number | null;
}

/** Categoria con su ruta completa ("Almacén › Yerba y Café › Yerba mate") para mostrar y elegir en el formulario. */
export interface CategoryOption {
  id: number;
  path: string;
}

/** Mismos limites que ProductRules / StockService del backend (la API vuelve a validar todo). */
export const PRODUCT_LIMITS = {
  name: 150,
  description: 2000,
  imageUrl: 500,
  maxPriceCents: 100_000_000_00,
  maxStock: 100_000,
} as const;

/** /api/v1/admin/products/** (rol admin). El interceptor agrega el Bearer. */
@Injectable({ providedIn: 'root' })
export class AdminProductsApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${CONFIG.apiUrl}/api/v1/admin/products`;

  /** Las categorias casi no cambian: se piden una vez por sesion de la app. */
  private readonly categories$ = this.http.get<Category[]>(`${CONFIG.apiUrl}/api/v1/categories`).pipe(
    map(toOptions),
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  list(search: string, page: number, pageSize = 20): Observable<Paged<AdminProduct>> {
    const params: Record<string, string> = { page: String(page), pageSize: String(pageSize) };
    if (search.trim()) params['search'] = search.trim();
    return this.http.get<Paged<AdminProduct>>(this.base, { params });
  }

  get(id: number): Observable<AdminProduct> {
    return this.http.get<AdminProduct>(`${this.base}/${id}`);
  }

  create(body: CreateProduct): Observable<AdminProduct> {
    return this.http.post<AdminProduct>(this.base, body);
  }

  update(id: number, body: UpdateProduct): Observable<AdminProduct> {
    return this.http.put<AdminProduct>(`${this.base}/${id}`, body);
  }

  /** Baja logica (Active = false). */
  deactivate(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  /** Delta positivo = ingreso, negativo = baja. Devuelve el stock resultante. */
  adjustStock(id: number, delta: number): Observable<{ productId: number; stock: number }> {
    return this.http.patch<{ productId: number; stock: number }>(`${this.base}/${id}/stock`, { delta });
  }

  categories(): Observable<CategoryOption[]> {
    return this.categories$;
  }
}

/** Arma la ruta de cada categoria a partir de la lista plana con ParentId, ordenada alfabeticamente por ruta. */
export function toOptions(all: Category[]): CategoryOption[] {
  const byId = new Map(all.map((c) => [c.id, c]));
  const pathOf = (c: Category): string => {
    const parts: string[] = [];
    const seen = new Set<number>();
    for (let cur: Category | undefined = c; cur && !seen.has(cur.id); cur = cur.parentId ? byId.get(cur.parentId) : undefined) {
      seen.add(cur.id);
      parts.unshift(cur.name);
    }
    return parts.join(' › ');
  };
  return all.map((c) => ({ id: c.id, path: pathOf(c) })).sort((a, b) => a.path.localeCompare(b.path, 'es'));
}
