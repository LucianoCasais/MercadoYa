import { Injectable, computed, signal } from '@angular/core';
import Keycloak from 'keycloak-js';
import { CONFIG } from './config';

export type Role = 'admin' | 'cliente';

/** Login con Keycloak (Authorization Code + PKCE). El estado de sesion se expone con signals. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly kc = new Keycloak(CONFIG.keycloak);
  private readonly _ready = signal(false);
  private readonly _authenticated = signal(false);
  private readonly _roles = signal<string[]>([]);
  private readonly _username = signal('');
  private readonly _userId = signal('');
  private readonly _fullName = signal('');
  private readonly _email = signal('');

  readonly ready = this._ready.asReadonly();
  readonly authenticated = this._authenticated.asReadonly();
  readonly roles = this._roles.asReadonly();
  readonly username = this._username.asReadonly();
  /** Identificador estable del usuario (claim `sub`); vacio si no hay sesion. */
  readonly userId = this._userId.asReadonly();
  /** Nombre completo y email de la cuenta (vienen en el token de Keycloak); vacios si no hay sesion. */
  readonly fullName = this._fullName.asReadonly();
  readonly email = this._email.asReadonly();
  readonly isAdmin = computed(() => this._roles().includes('admin'));
  readonly isCliente = computed(() => this._roles().includes('cliente'));

  /** Se llama al arrancar la app. Si Keycloak no responde, la tienda sigue funcionando como anonima. */
  async init(): Promise<void> {
    try {
      await this.kc.init({ onLoad: 'check-sso', pkceMethod: 'S256', checkLoginIframe: false });
      this.refreshState();
      this.kc.onAuthSuccess = () => this.refreshState();
      this.kc.onAuthLogout = () => this.refreshState();
    } catch (e) {
      console.warn('Keycloak no disponible: la sesion queda deshabilitada', e);
    } finally {
      this._ready.set(true);
    }
  }

  hasRole(role: Role): boolean {
    return this._roles().includes(role);
  }

  login(redirectPath?: string): Promise<void> {
    return this.kc.login({ redirectUri: window.location.origin + (redirectPath ?? window.location.pathname + window.location.search) });
  }

  logout(): Promise<void> {
    return this.kc.logout({ redirectUri: window.location.origin + '/tienda' });
  }

  /** Token vigente (lo renueva si le quedan menos de 30 s), o null si no hay sesion. */
  async getToken(): Promise<string | null> {
    if (!this.kc.authenticated) return null;
    try {
      await this.kc.updateToken(30);
    } catch {
      this.refreshState();
      return null;
    }
    return this.kc.token ?? null;
  }

  private refreshState(): void {
    this._authenticated.set(!!this.kc.authenticated);
    this._roles.set(this.kc.tokenParsed?.realm_access?.roles ?? []);
    this._userId.set(this.kc.authenticated ? (this.kc.tokenParsed?.sub ?? '') : '');
    const t = this.kc.tokenParsed as Record<string, unknown> | undefined;
    const full = (t?.['name'] as string | undefined) || `${t?.['given_name'] ?? ''} ${t?.['family_name'] ?? ''}`.trim();
    this._fullName.set(this.kc.authenticated ? full : '');
    this._email.set(this.kc.authenticated ? ((t?.['email'] as string | undefined) ?? '') : '');
    this._username.set((this.kc.tokenParsed?.['preferred_username'] as string | undefined) ?? '');
  }
}
