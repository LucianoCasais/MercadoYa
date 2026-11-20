import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/** Layout del panel de administracion: menu lateral (Productos / Pedidos) y la pantalla elegida a la derecha. */
@Component({
  selector: 'app-admin',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <div class="adm">
      <nav class="side" aria-label="Administración">
        <p class="ttl">Administración</p>
        <a routerLink="productos" routerLinkActive="on">Productos</a>
        <a routerLink="pedidos" routerLinkActive="on">Pedidos</a>
      </nav>
      <section class="body"><router-outlet /></section>
    </div>
  `,
  styles: `
    .adm { display: grid; grid-template-columns: 180px minmax(0, 1fr); gap: 24px; align-items: start; }
    .side { display: flex; flex-direction: column; gap: 4px; background: #fff; border: 1px solid var(--line); border-radius: 10px; padding: 12px; }
    .ttl { margin: 0 0 8px; font-size: .8rem; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
    .side a { padding: 8px 10px; border-radius: 6px; color: var(--ink); text-decoration: none; }
    .side a:hover { background: #eaf3ef; }
    .side a.on { background: var(--brand); color: #fff; }
    @media (max-width: 700px) {
      .adm { grid-template-columns: 1fr; }
      .side { flex-direction: row; flex-wrap: wrap; align-items: center; }
      .ttl { margin: 0 8px 0 0; }
    }
  `,
})
export class AdminPage {}
