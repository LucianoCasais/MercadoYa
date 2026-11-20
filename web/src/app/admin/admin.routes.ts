import { Routes } from '@angular/router';
import { AdminPage } from './admin.page';

/** Rutas del panel (/admin). Se cargan en forma diferida desde app.routes y ya estan protegidas por roleGuard('admin'). */
export const ADMIN_ROUTES: Routes = [
  {
    path: '',
    component: AdminPage,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'productos' },
      { path: 'productos', loadComponent: () => import('./products.page').then((m) => m.AdminProductsPage) },
      { path: 'productos/nuevo', loadComponent: () => import('./product-form.page').then((m) => m.ProductFormPage) },
      { path: 'productos/:id', loadComponent: () => import('./product-form.page').then((m) => m.ProductFormPage) },
      // Pedidos (Integrante B, dia 7): reemplazar este marcador por la pantalla real.
      { path: 'pedidos', loadComponent: () => import('./orders.page').then((m) => m.AdminOrdersPage) },
    ],
  },
];
