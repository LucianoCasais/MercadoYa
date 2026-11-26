import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, Injector, OnInit, afterNextRender, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { BehaviorSubject, catchError, combineLatest, map, of, switchMap, tap } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { CartService } from '../core/cart.service';
import { markCatalogVisible } from '../core/load-time';
import { readProblem } from '../core/problem';
import { Paged, Product, ProductsApi } from '../core/products.api';
import { MoneyPipe } from '../shared/money.pipe';

@Component({
  selector: 'app-catalog',
  imports: [MoneyPipe, RouterLink],
  templateUrl: './catalog.page.html',
  styleUrl: './catalog.page.scss',
})
export class CatalogPage implements OnInit {
  private readonly api = inject(ProductsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  protected readonly cart = inject(CartService);
  protected readonly auth = inject(AuthService);

  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly result = signal<Paged<Product> | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly notice = signal('');
  protected readonly needLogin = signal(false);

  private readonly retry$ = new BehaviorSubject(0);

  ngOnInit(): void {
    // La busqueda y la pagina viven en la URL (?q=&page=). El buscador del encabezado solo navega; esta pantalla reacciona a cada cambio y vuelve a pedir el listado (switchMap descarta la respuesta anterior).
    combineLatest([this.route.queryParamMap, this.retry$])
      .pipe(
        map(([qp]) => {
          const page = Number(qp.get('page'));
          return {
            q: qp.get('q') ?? '',
            page: Number.isInteger(page) && page > 0 ? page : 1,
          };
        }),
        tap((s) => {
          this.search.set(s.q);
          this.page.set(s.page);
          this.loading.set(true);
          this.error.set('');
        }),
        switchMap((s) =>
          this.api.list(s.q, s.page).pipe(
            catchError((e: HttpErrorResponse) => {
              // Las fallas transitorias ya se reintentaron (errorInterceptor); aca se explica que paso y se ofrece Reintentar.
              this.error.set('No pudimos cargar el catálogo. ' + readProblem(e).message);
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((r) => {
        this.result.set(r);
        this.loading.set(false);
        // Indicador del acta: la primera vez que el catalogo se ve con productos (despues de pintarse).
        if (r?.items.length) afterNextRender(() => markCatalogVisible(), { injector: this.injector });
      });
  }

  protected go(delta: number): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: { page: this.page() + delta > 1 ? this.page() + delta : null }, queryParamsHandling: 'merge' });
    window.scrollTo(0, 0);
  }

  protected get pages(): number {
    const r = this.result();
    return r ? Math.max(1, Math.ceil(r.total / r.pageSize)) : 1;
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

  /** Reintentar despues de un error: vuelve a pedir el mismo listado. */
  protected load(): void {
    this.retry$.next(this.retry$.value + 1);
  }
}
