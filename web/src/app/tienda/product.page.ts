import { HttpErrorResponse } from '@angular/common/http';
import { Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { CartService } from '../core/cart.service';
import { Product, ProductsApi } from '../core/products.api';
import { MoneyPipe } from '../shared/money.pipe';

/** Detalle de producto: /tienda/producto/:id (publico). El id llega por withComponentInputBinding. */
@Component({
  selector: 'app-product',
  imports: [RouterLink, MoneyPipe],
  templateUrl: './product.page.html',
  styleUrl: './product.page.scss',
})
export class ProductPage {
  private readonly api = inject(ProductsApi);
  protected readonly cart = inject(CartService);
  protected readonly auth = inject(AuthService);

  readonly id = input.required<string>();

  protected readonly product = signal<Product | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly notFound = signal(false);
  protected readonly notice = signal('');
  protected readonly needLogin = signal(false);

  constructor() {
    // Se vuelve a cargar si cambia el :id de la ruta sin destruir el componente.
    effect(() => this.load(this.id()));
  }

  protected load(rawId: string): void {
    this.loading.set(true);
    this.error.set('');
    this.notFound.set(false);
    this.notice.set('');
    const id = Number(rawId);
    if (!Number.isInteger(id) || id <= 0) {
      this.notFound.set(true);
      this.loading.set(false);
      return;
    }
    this.api.get(id).subscribe({
      next: (p) => {
        this.product.set(p);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => {
        if (e.status === 404) this.notFound.set(true);
        else this.error.set('No pudimos cargar el producto. ¿Está levantada la API?');
        this.loading.set(false);
      },
    });
  }

  protected add(p: Product): void {
    // El carrito es por usuario: sin sesion no se puede agregar.
    if (!this.auth.authenticated()) {
      this.needLogin.set(true);
      this.notice.set('Tenés que iniciar sesión para agregar productos al carrito.');
      return;
    }
    this.needLogin.set(false);
    this.notice.set(this.cart.add(p) ? '' : 'No hay más stock disponible de ' + p.name);
  }
}
