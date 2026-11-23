import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../core/auth';
import { I18n, TranslatePipe } from '../core/i18n';

@Component({
  selector: 'ws-login',
  imports: [FormsModule, TranslatePipe],
  template: `
    <div class="login">
      <section class="login-hero" aria-hidden="true">
        <p class="overline light">WISE Group</p>
        <h1>Wise<b>Sphere</b></h1>
        <p>{{ 'app.tagline' | t }}</p>
      </section>
      <form class="card login-card" (ngSubmit)="submit()">
        <img src="brand/logo.svg" alt="WISE Group" class="login-logo" />
        <p class="overline">WiseSphere</p>
        <h2>{{ 'auth.title' | t }}</h2>
        <label>{{ 'auth.username' | t }}<input name="u" [(ngModel)]="username" autocomplete="username" required autofocus /></label>
        <label>{{ 'auth.password' | t }}<input name="p" type="password" [(ngModel)]="password" autocomplete="current-password" required /></label>
        @if (error()) { <p class="alert" role="alert">{{ error()! | t }}</p> }
        <button class="btn btn-primary btn-block" [disabled]="busy()">{{ 'auth.submit' | t }}</button>
        <p class="muted small">{{ 'auth.hint' | t }}</p>
        <button type="button" class="btn btn-ghost btn-sm" (click)="i18n.toggle()">{{ 'lang.switch' | t }}</button>
      </form>
    </div>
  `,
})
export class LoginPage {
  protected i18n = inject(I18n);
  private auth = inject(Auth);
  private router = inject(Router);
  username = '';
  password = '';
  busy = signal(false);
  error = signal<string | null>(null);

  async submit() {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.username, this.password);
      await this.router.navigateByUrl('/dashboard');
    } catch (e) {
      const st = e instanceof HttpErrorResponse ? e.status : 0;
      this.error.set(st === 429 ? 'auth.locked' : st === 401 ? 'auth.invalid' : st === 503 || st === 504 ? 'auth.unavailable' : 'common.error');
    } finally {
      this.busy.set(false);
    }
  }
}
