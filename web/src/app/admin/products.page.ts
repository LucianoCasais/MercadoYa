import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AdminProduct, AdminProductsApi, PRODUCT_LIMITS } from '../core/admin-products.api';
import { Paged } from '../core/products.api';
import { readProblem } from '../core/problem';
import { MoneyPipe } from '../shared/money.pipe';

/** Admin · Productos: listado (incluye inactivos), busqueda, ajuste de stock, baja logica y reactivacion. */
@Component({
  selector: 'app-admin-products',
  imports: [FormsModule, RouterLink, MoneyPipe],
  templateUrl: './products.page.html',
  styleUrl: './products.page.scss',
})
export class AdminProductsPage implements OnInit {
  private readonly api = inject(AdminProductsApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected search = '';
  protected readonly page = signal(1);
  protected readonly result = signal<Paged<AdminProduct> | null>(null);
  protected readonly categoryPath = signal(new Map<number, string>());
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  /** Mensaje de la ultima accion (exito o error), visible arriba de la tabla. */
  protected readonly notice = signal<{ ok: boolean; text: string } | null>(null);
  /** Producto con una accion en curso (deshabilita sus botones). */
  protected readonly busyId = signal<number | null>(null);

  /** Ajuste de stock abierto (una fila a la vez). */
  protected readonly adjustingId = signal<number | null>(null);
  protected delta: number | null = null;
  protected readonly deltaError = signal('');

  private timer?: ReturnType<typeof setTimeout>;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  ngOnInit(): void {
    // Mensaje que deja el formulario al guardar (router state; no queda en la URL).
    const saved = (history.state as { notice?: string } | null)?.notice;
    if (saved) this.notice.set({ ok: true, text: saved });
    // Busqueda y pagina en la URL: al volver del formulario se recupera el mismo listado.
    const qp = this.route.snapshot.queryParamMap;
    this.search = qp.get('q') ?? '';
    const page = Number(qp.get('page'));
    this.page.set(Number.isInteger(page) && page > 0 ? page : 1);
    this.api.categories().subscribe({
      next: (cats) => this.categoryPath.set(new Map(cats.map((c) => [c.id, c.path]))),
      error: () => {
        /* sin categorias se muestra el id: no bloquea el panel */
      },
    });
    this.load();
  }

  protected get pages(): number {
    const r = this.result();
    return r ? Math.max(1, Math.ceil(r.total / r.pageSize)) : 1;
  }

  protected onSearch(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.page.set(1);
      this.load();
    }, 300);
  }

  protected go(delta: number): void {
    this.page.update((p) => p + delta);
    this.load();
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
      next: (r) => {
        // Si se borro el ultimo de la pagina (o la URL traia una pagina que ya no existe) se vuelve a la ultima valida.
        const last = Math.max(1, Math.ceil(r.total / r.pageSize));
        if (this.page() > last) {
          this.page.set(last);
          this.load();
          return;
        }
        this.result.set(r);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => {
        this.error.set(readProblem(e).message);
        this.loading.set(false);
      },
    });
  }

  // ---- Ajuste de stock ----

  protected openAdjust(p: AdminProduct): void {
    this.adjustingId.set(this.adjustingId() === p.id ? null : p.id);
    this.delta = null;
    this.deltaError.set('');
  }

  /** Misma regla que la API: entero distinto de 0, hasta 100.000 unidades y sin dejar el stock en negativo. */
  protected validateDelta(p: AdminProduct): string {
    const d = this.delta;
    if (d === null || !Number.isInteger(d) || d === 0) return 'Ingresá un número entero distinto de 0 (negativo para descontar).';
    if (Math.abs(d) > PRODUCT_LIMITS.maxStock) return `El ajuste puede ser de hasta ${PRODUCT_LIMITS.maxStock.toLocaleString('es-AR')} unidades.`;
    if (p.stock + d < 0) return `No podés descontar más de ${p.stock} unidades.`;
    return '';
  }

  protected applyAdjust(p: AdminProduct): void {
    const err = this.validateDelta(p);
    this.deltaError.set(err);
    if (err) return;
    const delta = this.delta!;
    this.busyId.set(p.id);
    this.api.adjustStock(p.id, delta).subscribe({
      next: (r) => {
        this.patch(p.id, { stock: r.stock });
        this.adjustingId.set(null);
        this.busyId.set(null);
        this.notice.set({ ok: true, text: `Stock de "${p.name}" actualizado: ${r.stock} unidades (${delta > 0 ? '+' : ''}${delta}).` });
      },
      error: (e: HttpErrorResponse) => {
        this.busyId.set(null);
        // 409: entre que se cargo la tabla y el ajuste hubo ventas; se muestra el error y se recarga el stock real.
        this.deltaError.set(readProblem(e).fields['delta'] ?? readProblem(e).message);
        if (e.status === 409) this.refresh(p.id);
      },
    });
  }

  // ---- Baja logica y reactivacion ----

  protected deactivate(p: AdminProduct): void {
    if (!confirm(`¿Dar de baja "${p.name}"? Deja de mostrarse en la tienda; los pedidos existentes no se modifican.`)) return;
    this.busyId.set(p.id);
    this.api.deactivate(p.id).subscribe({
      next: () => {
        this.patch(p.id, { active: false });
        this.busyId.set(null);
        this.notice.set({ ok: true, text: `"${p.name}" quedó inactivo.` });
      },
      error: (e: HttpErrorResponse) => this.fail(e),
    });
  }

  protected reactivate(p: AdminProduct): void {
    this.busyId.set(p.id);
    const { name, description, priceCents, categoryId, imageUrl } = p;
    this.api.update(p.id, { name, description, priceCents, categoryId, imageUrl, active: true }).subscribe({
      next: (updated) => {
        this.patch(p.id, updated);
        this.busyId.set(null);
        this.notice.set({ ok: true, text: `"${p.name}" vuelve a estar a la venta.` });
      },
      error: (e: HttpErrorResponse) => this.fail(e),
    });
  }

  private fail(e: HttpErrorResponse): void {
    this.busyId.set(null);
    this.notice.set({ ok: false, text: readProblem(e).message });
  }

  /** Vuelve a leer un producto (por ejemplo, despues de un 409 de stock). */
  private refresh(id: number): void {
    this.api.get(id).subscribe({ next: (p) => this.patch(id, p), error: () => undefined });
  }

  private patch(id: number, changes: Partial<AdminProduct>): void {
    this.result.update((r) => (r ? { ...r, items: r.items.map((x) => (x.id === id ? { ...x, ...changes } : x)) } : r));
  }
}
