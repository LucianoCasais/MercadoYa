import { Component, input } from '@angular/core';

/** Indicador de los 5 pasos del checkout: carrito, comprador, pago, confirmar y seguimiento. */
@Component({
  selector: 'app-checkout-steps',
  template: `
    <ol class="steps" aria-label="Pasos de la compra">
      @for (s of steps; track s; let i = $index) {
        <li [class.cur]="current() === i + 1" [class.done]="current() > i + 1" [attr.aria-current]="current() === i + 1 ? 'step' : null">
          <span class="n">{{ current() > i + 1 ? '✓' : i + 1 }}</span><span class="t">{{ s }}</span>
        </li>
      }
    </ol>
  `,
  styles: `
    .steps { display: flex; gap: 8px; list-style: none; margin: 0 0 16px; padding: 0; flex-wrap: wrap; }
    li { display: flex; align-items: center; gap: 6px; color: var(--muted); font-size: .9rem; }
    li + li::before { content: ''; width: 18px; height: 1px; background: var(--line); margin-right: 2px; }
    .n { display: inline-grid; place-items: center; width: 24px; height: 24px; border-radius: 50%; border: 1px solid var(--line); background: #fff; font-size: .8rem; }
    .cur { color: var(--ink); font-weight: 600; }
    .cur .n { background: var(--brand); border-color: var(--brand); color: #fff; }
    .done .n { background: #eaf3ef; border-color: var(--brand); color: var(--brand); }
    @media (max-width: 640px) { li:not(.cur) .t { display: none; } }
  `,
})
export class CheckoutSteps {
  readonly current = input.required<number>();
  protected readonly steps = ['Carrito', 'Comprador', 'Pago', 'Confirmar', 'Seguimiento'];
}
