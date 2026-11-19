import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { CONFIG } from './config';

export interface Product {
  id: number;
  name: string;
  description: string;
  priceCents: number;
  stock: number;
  categoryId: number;
  imageUrl: string | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

@Injectable({ providedIn: 'root' })
export class ProductsApi {
  private readonly http = inject(HttpClient);

  list(search: string, page: number, pageSize = 12): Observable<Paged<Product>> {
    const params: Record<string, string> = { page: String(page), pageSize: String(pageSize) };
    if (search.trim()) params['search'] = search.trim();
    return this.http.get<Paged<Product>>(`${CONFIG.apiUrl}/api/v1/products`, { params });
  }
}
