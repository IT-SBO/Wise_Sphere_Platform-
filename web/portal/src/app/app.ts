import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from './core/auth';
import { I18n, TranslatePipe } from './core/i18n';

/** Shell: brand bar + navigation. The layout and colours come from styles.css / tokens.css (branding). */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe],
  template: `
    @if (auth.me(); as me) {
      <header class="topbar no-print">
        <a class="brand" routerLink="/"><span class="logo" aria-hidden="true"></span><span>WiseSphere</span></a>
        <nav>
          <a routerLink="/dashboard" routerLinkActive="active">{{ 'nav.dashboard' | t }}</a>
          <a routerLink="/tickets" routerLinkActive="active">{{ 'nav.tickets' | t }}</a>
          <a routerLink="/monitoring" routerLinkActive="active">{{ 'nav.monitoring' | t }}</a>
          <a routerLink="/reports" routerLinkActive="active">{{ 'nav.reports' | t }}</a>
          <a routerLink="/new" routerLinkActive="active" class="btn btn-primary btn-sm">{{ 'nav.newTicket' | t }}</a>
        </nav>
        <div class="user">
          <button class="btn btn-ghost btn-sm" (click)="i18n.toggle()">{{ 'lang.switch' | t }}</button>
          <span class="who" [title]="me.login">{{ me.name }}</span>
          <button class="btn btn-ghost btn-sm" (click)="logout()">{{ 'auth.logout' | t }}</button>
        </div>
      </header>
    }
    <main [class.bare]="!auth.me()"><router-outlet /></main>
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
