import { Injectable, inject, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';

/**
 * Estado de la navegacion que comparten el encabezado, el catalogo: la ruta actual y la busqueda
 * (?q=). La URL es la unica fuente de verdad; este servicio solo la expone como signals.
 */
@Injectable({ providedIn: 'root' })
export class NavState {
  private readonly router = inject(Router);
  readonly url = signal(this.router.url);
  readonly q = signal('');

  constructor() {
    this.sync();
    this.router.events.subscribe((e) => {
      if (e instanceof NavigationEnd) this.sync();
    });
  }

  /** El panel de administracion tiene su propio encabezado (como en el boceto). */
  isAdmin(): boolean {
    return this.url().startsWith('/admin');
  }

  private sync(): void {
    const tree = this.router.parseUrl(this.router.url);
    this.url.set('/' + tree.root.children['primary']?.segments.map((s) => s.path).join('/'));
    const onCatalog = this.url() === '/tienda';
    this.q.set(onCatalog ? (tree.queryParams['q'] ?? '') : '');
  }
}
