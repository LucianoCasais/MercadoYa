import { Component, input } from '@angular/core';

/** Indicador de los 5 pasos del checkout (boceto v2): revisar carrito, datos del comprador, pago simulado, confirmar y seguimiento. */
@Component({
  selector: 'app-checkout-steps',
  template: `
    <ol class="steps" aria-label="Pasos de la compra">
      @for (s of steps; track s; let i = $index) {
        <li [class.on]="current() === i + 1" [class.done]="current() > i + 1" [attr.aria-current]="current() === i + 1 ? 'step' : null">
          {{ i + 1 }} {{ s }}
        </li>
      }
    </ol>
  `,
  styles: `
    .steps { display: flex; gap: 8px; flex-wrap: wrap; list-style: none; padding: 0; margin: 0 0 18px; }
    li { padding: 6px 14px; border-radius: 999px; background: var(--ph); color: var(--muted); font-size: 14px; }
    .done { background: var(--tint); color: var(--rt); }
    .on { background: var(--red); color: #fff; font-weight: 700; }
  `,
})
export class CheckoutSteps {
  readonly current = input.required<number>();
  protected readonly steps = ['Revisar carrito', 'Datos del comprador', 'Pago simulado', 'Confirmar', 'Seguimiento'];
}
