import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, input, signal } from '@angular/core';
import { AbstractControl, NonNullableFormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminProductsApi, CategoryOption, PRODUCT_LIMITS } from '../core/admin-products.api';
import { readProblem } from '../core/problem';

/** Texto obligatorio que no sea solo espacios (la API recorta y lo rechazaria). */
const notBlank = (c: AbstractControl<string>): ValidationErrors | null => (c.value?.trim() ? null : { required: true });
/** Precio en pesos con hasta 2 decimales (se envia en centavos). */
const twoDecimals = (c: AbstractControl<number | null>): ValidationErrors | null =>
  c.value === null || Math.abs(Math.round(c.value * 100) - c.value * 100) < 1e-6 ? null : { decimals: true };

type Field = 'name' | 'description' | 'price' | 'categoryId' | 'imageUrl' | 'initialStock' | 'active';

/** Claves de error de la API -> controles del formulario. */
const FIELD_OF: Record<string, string> = {
  name: 'name',
  description: 'description',
  priceCents: 'price',
  categoryId: 'categoryId',
  imageUrl: 'imageUrl',
  initialStock: 'initialStock',
};

/** Alta (/admin/productos/nuevo) y edicion (/admin/productos/:id) de productos. */
@Component({
  selector: 'app-product-form',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './product-form.page.html',
  styleUrl: './product-form.page.scss',
})
export class ProductFormPage implements OnInit {
  private readonly api = inject(AdminProductsApi);
  private readonly router = inject(Router);
  private readonly fb = inject(NonNullableFormBuilder);

  /** Viene de la ruta :id; no existe en /nuevo. */
  readonly id = input<string>();

  protected readonly limits = PRODUCT_LIMITS;
  protected readonly categories = signal<CategoryOption[]>([]);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly notFound = signal(false);
  protected readonly error = signal('');
  /** Error de la API sobre un campo puntual (se muestra debajo del campo). */
  protected readonly serverErrors = signal<Record<string, string>>({});
  /** 409: otro cambio piso el producto mientras se editaba. */
  protected readonly conflict = signal(false);
  /** Stock actual (solo lectura en edicion: se ajusta desde el listado). */
  protected readonly stock = signal(0);

  protected readonly form = this.fb.group({
    name: ['', [notBlank, Validators.maxLength(PRODUCT_LIMITS.name)]],
    description: ['', [Validators.maxLength(PRODUCT_LIMITS.description)]],
    price: this.fb.control<number | null>(null, [Validators.required, Validators.min(0.01), Validators.max(PRODUCT_LIMITS.maxPriceCents / 100), twoDecimals]),
    categoryId: this.fb.control<number | null>(null, [Validators.required]),
    imageUrl: ['', [Validators.maxLength(PRODUCT_LIMITS.imageUrl), Validators.pattern(/^\s*(https?:\/\/\S+)?\s*$/i)]],
    initialStock: this.fb.control<number | null>(0, [Validators.required, Validators.min(0), Validators.max(PRODUCT_LIMITS.maxStock), Validators.pattern(/^\d+$/)]),
    active: [true],
  });

  protected get isEdit(): boolean {
    return !!this.id();
  }

  ngOnInit(): void {
    this.api.categories().subscribe({
      next: (c) => this.categories.set(c),
      error: (e: HttpErrorResponse) => this.error.set('No se pudieron cargar las categorías. ' + readProblem(e).message),
    });
    // En edicion el stock inicial no aplica (el stock se ajusta desde el listado y queda registrado).
    if (this.isEdit) {
      this.form.controls.initialStock.disable();
      this.loadProduct();
    }
    // Al corregir un campo se borra el error que habia mandado la API para ese campo.
    for (const [name, control] of Object.entries(this.form.controls) as [string, AbstractControl][]) {
      control.valueChanges.subscribe(() => {
        if (this.serverErrors()[name]) this.serverErrors.update(({ [name]: _, ...rest }) => rest);
      });
    }
  }

  protected loadProduct(): void {
    const id = Number(this.id());
    if (!Number.isInteger(id) || id <= 0) {
      this.notFound.set(true);
      return;
    }
    this.loading.set(true);
    this.conflict.set(false);
    this.error.set('');
    this.api.get(id).subscribe({
      next: (p) => {
        this.form.reset({
          name: p.name,
          description: p.description,
          price: p.priceCents / 100,
          categoryId: p.categoryId,
          imageUrl: p.imageUrl ?? '',
          active: p.active,
        });
        this.stock.set(p.stock);
        this.loading.set(false);
      },
      error: (e: HttpErrorResponse) => {
        this.loading.set(false);
        if (e.status === 404) this.notFound.set(true);
        else this.error.set(readProblem(e).message);
      },
    });
  }

  /** Mensaje del primer error del control (validacion local o de la API). */
  protected msg(name: Field): string {
    const c = this.form.controls[name];
    const server = this.serverErrors()[name];
    if (server) return server;
    if (!c.errors || !(c.touched || c.dirty)) return '';
    const e = c.errors;
    if (e['required']) return 'Este campo es obligatorio.';
    if (e['maxlength']) return `Máximo ${e['maxlength'].requiredLength} caracteres.`;
    if (name === 'price') {
      if (e['min']) return 'El precio debe ser mayor a 0.';
      if (e['max']) return 'El precio es demasiado alto.';
      if (e['decimals']) return 'Usá como máximo 2 decimales.';
    }
    if (name === 'initialStock') return `Ingresá un entero entre 0 y ${this.limits.maxStock.toLocaleString('es-AR')}.`;
    if (name === 'imageUrl') return 'Debe ser una URL que empiece con http:// o https://';
    return 'Valor inválido.';
  }

  protected invalid(name: Field): boolean {
    return !!this.msg(name);
  }

  protected previewUrl(): string | null {
    const v = this.form.controls.imageUrl.value.trim();
    return this.form.controls.imageUrl.valid && v ? v : null;
  }

  protected save(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set('');
    this.serverErrors.set({});
    this.conflict.set(false);

    const v = this.form.getRawValue();
    const data = {
      name: v.name.trim(),
      description: v.description.trim(),
      priceCents: Math.round(v.price! * 100),
      categoryId: v.categoryId!,
      imageUrl: v.imageUrl.trim() || null,
    };
    const req = this.isEdit
      ? this.api.update(Number(this.id()), { ...data, active: v.active })
      : this.api.create({ ...data, initialStock: v.initialStock! });

    req.subscribe({
      next: (p) =>
        void this.router.navigate(['/admin/productos'], {
          state: { notice: `"${p.name}" ${this.isEdit ? 'se guardó' : 'se creó'} correctamente.` },
        }),
      error: (e: HttpErrorResponse) => {
        this.saving.set(false);
        const problem = readProblem(e);
        const fields: Record<string, string> = {};
        for (const [k, text] of Object.entries(problem.fields)) fields[FIELD_OF[k] ?? k] = text;
        this.serverErrors.set(fields);
        this.conflict.set(e.status === 409);
        if (e.status === 404) this.notFound.set(true);
        else this.error.set(problem.message);
      },
    });
  }
}
