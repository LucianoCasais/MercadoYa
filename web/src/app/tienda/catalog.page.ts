import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CartService } from '../core/cart.service';
import { Paged, Product, ProductsApi } from '../core/products.api';
import { MoneyPipe } from '../shared/money.pipe';

@Component({
  selector: 'app-catalog',
  imports: [FormsModule, MoneyPipe, RouterLink],
  templateUrl: './catalog.page.html',
  styleUrl: './catalog.page.scss',
})
export class CatalogPage implements OnInit, OnDestroy {
  private readonly api = inject(ProductsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly cart = inject(CartService);

  protected search = '';
  protected readonly page = signal(1);
  protected readonly result = signal<Paged<Product> | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  private timer?: ReturnType<typeof setTimeout>;

  ngOnInit(): void {
    // La busqueda y la pagina viven en la URL (?q=&page=): al volver del detalle se recupera el mismo listado.
    const qp = this.route.snapshot.queryParamMap;
    this.search = qp.get('q') ?? '';
    const page = Number(qp.get('page'));
    this.page.set(Number.isInteger(page) && page > 0 ? page : 1);
    this.load();
  }

  ngOnDestroy(): void {
    // Si el usuario sale de la pagina antes de los 300 ms, la busqueda pendiente no debe ejecutarse (navegaria de vuelta al catalogo).
    clearTimeout(this.timer);
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
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: this.search.trim() || null, page: this.page() > 1 ? this.page() : null },
      replaceUrl: true,
    });
    this.api.list(this.search, this.page()).subscribe({
      next: (r) => { this.result.set(r); this.loading.set(false); },
      error: () => { this.error.set('No pudimos cargar el catálogo. ¿Está levantada la API?'); this.loading.set(false); },
    });
  }
}
