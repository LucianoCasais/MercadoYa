import { Routes } from '@angular/router';
import { roleGuard } from './core/guards';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'tienda' },
  { path: 'tienda', loadComponent: () => import('./tienda/catalog.page').then((m) => m.CatalogPage) },
  { path: 'tienda/carrito', loadComponent: () => import('./tienda/cart.page').then((m) => m.CartPage) },
  {
    // Dia 8: checkout de 5 pasos. Hoy solo valida que el guard exige sesion y rol cliente.
    path: 'tienda/checkout',
    canActivate: [roleGuard('cliente')],
    loadComponent: () => import('./tienda/checkout.page').then((m) => m.CheckoutPage),
  },
  {
    path: 'admin',
    canActivate: [roleGuard('admin')],
    loadComponent: () => import('./admin/admin.page').then((m) => m.AdminPage),
  },
  { path: '403', loadComponent: () => import('./shared/forbidden.page').then((m) => m.ForbiddenPage) },
  { path: '**', redirectTo: 'tienda' },
];
