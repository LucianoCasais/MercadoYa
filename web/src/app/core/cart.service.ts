import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { AuthService } from './auth.service';

export interface CartLine {
  productId: number;
  name: string;
  priceCents: number;
  quantity: number;
  stock: number;
}

/** Carrito de quien no inicio sesion (es la misma clave que usaba la version anterior). */
const ANON_KEY = 'mercadoya.cart';
const userKey = (userId: string) => `mercadoya.cart.${userId}`;
export const MAX_PER_LINE = 99;

const limitOf = (l: CartLine) => Math.min(l.stock, MAX_PER_LINE);

/**
 * Carrito con signals, guardado en localStorage **por usuario**:
 * - sin sesion: clave anonima;
 * - con sesion: clave propia del usuario (`sub`). Al cerrar sesion no se borra, y otro usuario nunca lo ve;
 * - al iniciar sesion, lo que se habia agregado como anonimo se suma al carrito del usuario.
 */
@Injectable({ providedIn: 'root' })
export class CartService {
  private readonly auth = inject(AuthService);
  private readonly _lines = signal<CartLine[]>(this.loadFor(this.auth.userId()));

  readonly lines = this._lines.asReadonly();
  readonly count = computed(() => this._lines().reduce((n, l) => n + l.quantity, 0));
  readonly totalCents = computed(() => this._lines().reduce((n, l) => n + l.priceCents * l.quantity, 0));

  constructor() {
    // Si cambia el usuario (login/logout/sesion vencida) se muestra el carrito que le corresponde.
    effect(() => {
      const userId = this.auth.userId();
      untracked(() => this._lines.set(this.loadFor(userId)));
    });
  }

  /** Agrega una unidad. Devuelve false si ya no hay mas stock disponible. */
  add(p: { id: number; name: string; priceCents: number; stock: number }): boolean {
    const current = this._lines().find((l) => l.productId === p.id);
    const limit = Math.min(p.stock, MAX_PER_LINE);
    if ((current?.quantity ?? 0) >= limit) return false;
    this.update((lines) =>
      current
        ? lines.map((l) => (l.productId === p.id ? { ...l, quantity: l.quantity + 1, stock: p.stock } : l))
        : [...lines, { productId: p.id, name: p.name, priceCents: p.priceCents, quantity: 1, stock: p.stock }],
    );
    return true;
  }

  increase(productId: number): boolean {
    const line = this._lines().find((l) => l.productId === productId);
    if (!line || line.quantity >= limitOf(line)) return false;
    this.update((lines) => lines.map((l) => (l.productId === productId ? { ...l, quantity: l.quantity + 1 } : l)));
    return true;
  }

  decrease(productId: number): void {
    this.update((lines) =>
      lines.flatMap((l) => (l.productId !== productId ? [l] : l.quantity > 1 ? [{ ...l, quantity: l.quantity - 1 }] : [])),
    );
  }

  remove(productId: number): void {
    this.update((lines) => lines.filter((l) => l.productId !== productId));
  }

  clear(): void {
    this.update(() => []);
  }

  quantityOf(productId: number): number {
    return this._lines().find((l) => l.productId === productId)?.quantity ?? 0;
  }

  private update(fn: (lines: CartLine[]) => CartLine[]): void {
    this._lines.update(fn);
    this.write(this.keyFor(this.auth.userId()), this._lines());
  }

  private keyFor(userId: string): string {
    return userId ? userKey(userId) : ANON_KEY;
  }

  /** Carrito de ese usuario; si hay sesion, absorbe (y borra) el carrito anonimo pendiente. */
  private loadFor(userId: string): CartLine[] {
    if (!userId) return this.read(ANON_KEY);
    const mine = this.read(userKey(userId));
    const anon = this.read(ANON_KEY);
    if (!anon.length) return mine;
    const merged = [...mine];
    for (const a of anon) {
      const i = merged.findIndex((l) => l.productId === a.productId);
      if (i < 0) merged.push(a);
      else merged[i] = { ...merged[i], quantity: Math.min(merged[i].quantity + a.quantity, limitOf(a)), stock: a.stock };
    }
    this.write(userKey(userId), merged);
    try {
      localStorage.removeItem(ANON_KEY);
    } catch {
      /* sin almacenamiento */
    }
    return merged;
  }

  private write(key: string, lines: CartLine[]): void {
    try {
      localStorage.setItem(key, JSON.stringify(lines));
    } catch {
      /* sin almacenamiento: el carrito vive solo en memoria */
    }
  }

  private read(key: string): CartLine[] {
    try {
      const raw = JSON.parse(localStorage.getItem(key) ?? '[]');
      return Array.isArray(raw) ? raw.filter((l) => l?.productId > 0 && l?.quantity > 0) : [];
    } catch {
      return [];
    }
  }
}
