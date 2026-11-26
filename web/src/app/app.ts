import { Component, effect, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';
import { CartService } from './core/cart.service';
import { NavState } from './core/nav-state';

@Component({
  imports: [RouterOutlet, RouterLink],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly cart = inject(CartService);
  protected readonly nav = inject(NavState);
  private readonly router = inject(Router);

  /** Texto del buscador del encabezado. La URL (?q=) es la fuente de verdad; esto es solo lo que se esta escribiendo. */
  protected readonly term = signal('');
  private pushed = '';
  private timer?: ReturnType<typeof setTimeout>;

  constructor() {
    effect(() => {
      const q = this.nav.q();
      if (q !== this.pushed) { this.term.set(q); this.pushed = q; }   // cambio de URL (logo, volver atras)
    });
  }

  protected onInput(value: string): void {
    this.term.set(value);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.search(), 300);
  }

  /** Busca por texto y vuelve a la primera pagina. */
  protected search(): void {
    clearTimeout(this.timer);
    const q = this.term().trim();
    this.pushed = q;
    void this.router.navigate(['/tienda'], { queryParams: { q: q || null } });
  }
}
