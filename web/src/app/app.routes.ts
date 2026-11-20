import { Routes } from '@angular/router';
import { roleGuard } from './core/guards';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'tienda' },
  { path: 'tienda', loadComponent: () => import('./tienda/catalog.page').then((m) => m.CatalogPage) },
  { path: 'tienda/producto/:id', loadComponent: () => import('./tienda/product.page').then((m) => m.ProductPage) },
  { path: 'tienda/carrito',loadComponent: () => import('./tienda/cart.page').then((m) => m.CartPage) },
  {
    // Checkout de 5 pasos (los 4 primeros viven en CheckoutPage; el 5 es el seguimiento en /tienda/pedido/:id). Exige sesion y rol cliente.
    path: 'tienda/checkout',
    canActivate: [roleGuard('cliente')],
    loadComponent: () => import('./tienda/checkout.page').then((m) => m.CheckoutPage),
  },
  {
    path: 'tienda/pedido/:id',
    canActivate: [roleGuard('cliente')],
    loadComponent: () => import('./tienda/order.page').then((m) => m.OrderPage),
  },
  {
    path: 'admin',
    canActivate: [roleGuard('admin')],
    loadComponent: () => import('./admin/admin.page').then((m) => m.AdminPage),
  },
  { path: '403', loadComponent: () => import('./shared/forbidden.page').then((m) => m.ForbiddenPage) },
  { path: '**', redirectTo: 'tienda' },
];
