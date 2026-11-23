import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from './core/auth';
import { I18n, TranslatePipe } from './core/i18n';

/** Shell in the WISE Group style: white brand bar + dark navigation bar (colours/fonts from tokens.css). */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe],
  template: `
    @if (auth.me(); as me) {
      <header class="site-header no-print">
        <div class="brandbar">
          <div class="container brandbar-inner">
            <a class="brand" routerLink="/" aria-label="WiseSphere – WISE Group">
              <img src="brand/logo.svg" alt="WISE Group" class="brand-logo" />
              <span class="brand-divider" aria-hidden="true"></span>
              <span class="brand-product">Wise<b>Sphere</b></span>
            </a>
            <div class="user">
              <button class="link-btn" (click)="i18n.toggle()">{{ 'lang.switch' | t }}</button>
              <span class="sep" aria-hidden="true"></span>
              <span class="who" [title]="me.login">{{ me.name }}</span>
              <span class="sep" aria-hidden="true"></span>
              <button class="link-btn" (click)="logout()">{{ 'auth.logout' | t }}</button>
            </div>
          </div>
        </div>
        <nav class="navbar" aria-label="Main">
          <div class="container navbar-inner">
            <a routerLink="/dashboard" routerLinkActive="active">{{ 'nav.dashboard' | t }}</a>
            <a routerLink="/tickets" routerLinkActive="active">{{ 'nav.tickets' | t }}</a>
            <a routerLink="/monitoring" routerLinkActive="active">{{ 'nav.monitoring' | t }}</a>
            <a routerLink="/reports" routerLinkActive="active">{{ 'nav.reports' | t }}</a>
            <a routerLink="/new" routerLinkActive="active" class="nav-cta">{{ 'nav.newTicket' | t }}</a>
          </div>
        </nav>
      </header>
    }
    <main [class.bare]="!auth.me()"><router-outlet /></main>
    @if (auth.me()) {
      <footer class="site-footer no-print"><div class="container">© WISE Group · WiseSphere {{ 'app.tagline' | t }}</div></footer>
    }
  `,
})
export class App {
  protected auth = inject(Auth);
  protected i18n = inject(I18n);
  private router = inject(Router);

  async logout() {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
