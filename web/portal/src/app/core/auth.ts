import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Api } from './api';
import { Me } from './models';

/** Who is signed in. The session itself is an HttpOnly cookie the browser cannot read. */
@Injectable({ providedIn: 'root' })
export class Auth {
  private api = inject(Api);
  readonly me = signal<Me | null>(null);
  readonly isStaff = computed(() => this.me()?.isStaff ?? false);
  private checked = false;

  async ensure(): Promise<boolean> {
    if (this.me()) return true;
    if (this.checked) return false;
    this.checked = true;
    try { this.me.set(await this.api.me()); return true; } catch { return false; }
  }
  async login(u: string, p: string) { this.me.set(await this.api.login(u, p)); this.checked = true; }
  async logout() { try { await this.api.logout(); } finally { this.me.set(null); } }
}

export const authGuard: CanActivateFn = async () => {
  const auth = inject(Auth);
  return (await auth.ensure()) ? true : inject(Router).parseUrl('/login');
};
