import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CartService } from '../core/cart.service';
import { Paged, Product, ProductsApi } from '../core/products.api';
import { MoneyPipe } from '../shared/money.pipe';

@Component({
  selector: 'app-catalog',
  imports: [FormsModule, MoneyPipe],
  templateUrl: './catalog.page.html',
  styleUrl: './catalog.page.scss',
})
export class CatalogPage implements OnInit {
  private readonly api = inject(ProductsApi);
  protected readonly cart = inject(CartService);

  protected search = '';
  protected readonly page = signal(1);
  protected readonly result = signal<Paged<Product> | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  private timer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    this.load();
  }

  protected onSearch(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.page.set(1); this.load(); }, 300);
  }

  protected go(delta: number): void {
    this.page.update((p) => p + delta);
    this.load();
    window.scrollTo(0, 0);
  }

  protected get pages(): number {
    const r = this.result();
    return r ? Math.max(1, Math.ceil(r.total / r.pageSize)) : 1;
  }

  protected add(p: Product): void {
    this.notice.set(this.cart.add(p) ? '' : 'No hay más stock disponible de ' + p.name);
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set('');
    this.api.list(this.search, this.page()).subscribe({
      next: (r) => { this.result.set(r); this.loading.set(false); },
      error: () => { this.error.set('No pudimos cargar el catálogo. ¿Está levantada la API?'); this.loading.set(false); },
    });
  }
}
