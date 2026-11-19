import { Pipe, PipeTransform } from '@angular/core';

/** Los importes viajan en centavos (long): 480000 -> "$ 4.800,00". */
@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(cents: number): string {
    return '$ ' + (cents / 100).toLocaleString('es-AR', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }
}
