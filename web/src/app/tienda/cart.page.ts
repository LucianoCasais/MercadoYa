import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { CartService } from '../core/cart.service';
import { MoneyPipe } from '../shared/money.pipe';

@Component({
  selector: 'app-cart',
  imports: [RouterLink, MoneyPipe],
  templateUrl: './cart.page.html',
  styleUrl: './cart.page.scss',
})
export class CartPage {
  protected readonly cart = inject(CartService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly notice = signal('');

  protected inc(id: number): void {
    this.notice.set(this.cart.increase(id) ? '' : 'No hay más stock disponible.');
  }

  /** Paso 1 -> 2 del checkout: pide sesion y rol cliente (el guard de la ruta lo resuelve). */
  protected next(): void {
    void this.router.navigateByUrl('/tienda/checkout');
  }
}
