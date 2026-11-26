import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';

/** Layout del panel de administracion (boceto v2): menu lateral con el logo y la pantalla elegida a la derecha. */
@Component({
  selector: 'app-admin',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="adm">
      <aside class="side">
        <a class="logo" routerLink="/tienda">MercadoYa</a>
        <p>Administración</p>
        <nav aria-label="Administración">
          <a class="n" routerLink="productos" routerLinkActive="on">Productos</a>
          <a class="n" routerLink="pedidos" routerLinkActive="on">Pedidos</a>
        </nav>
        <div class="foot">
          <span class="who">{{ auth.email() || auth.username() }}</span>
          <a class="n" routerLink="/tienda">Ver la tienda</a>
          <button class="n" type="button" (click)="auth.logout()">Salir</button>
        </div>
      </aside>
      <main class="body"><router-outlet /></main>
    </div>
  `,
  styles: `
    .adm { display: grid; grid-template-columns: 220px minmax(0, 1fr); min-height: 100vh; }
    .side { background: var(--card); border-right: 1px solid var(--line); padding: 20px 16px; display: flex; flex-direction: column; }
    .logo { font-weight: 800; font-size: 24px; letter-spacing: -.5px; color: var(--rt); text-decoration: none; }
    .side p { margin: 2px 0 16px; color: var(--muted); font-size: 14px; }
    nav { display: flex; flex-direction: column; }
    .n { display: block; width: 100%; text-align: left; background: none; border: 0; padding: 12px; border-radius: 8px; font-weight: 500; color: var(--ink); text-decoration: none; font: inherit; }
    .n:hover { background: var(--tint); }
    .n.on { background: var(--tint); color: var(--rt); font-weight: 700; }
    .foot { margin-top: auto; padding-top: 16px; border-top: 1px solid var(--line); }
    .who { display: block; padding: 0 12px 8px; color: var(--muted); font-size: 13px; overflow-wrap: anywhere; }
    .body { padding: 24px; max-width: none; width: 100%; }
    @media (max-width: 760px) {
      .adm { grid-template-columns: 1fr; min-height: 0; }
      .side { border-right: 0; border-bottom: 1px solid var(--line); }
      nav { flex-direction: row; flex-wrap: wrap; }
      .n { width: auto; }
      .foot { margin-top: 12px; display: flex; flex-wrap: wrap; align-items: center; }
      .who { padding: 0 12px 0 0; }
    }
  `,
})
export class AdminPage {
  protected readonly auth = inject(AuthService);
}
