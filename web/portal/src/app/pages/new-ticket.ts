import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { TranslatePipe } from '../core/i18n';

@Component({
  selector: 'ws-new-ticket',
  imports: [FormsModule, TranslatePipe],
  template: `
    <h1>{{ 'new.title' | t }}</h1>
    <form class="card form" (ngSubmit)="submit()">
      <label>{{ 'new.client' | t }}
        <select name="entity" [(ngModel)]="entityId" required>
          @for (e of entities(); track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
        </select>
      </label>
      <label>{{ 'new.subject' | t }}<input name="title" [(ngModel)]="title" maxlength="250" required /></label>
      <label>{{ 'new.description' | t }}<textarea name="text" [(ngModel)]="text" rows="6" maxlength="10000" required></textarea></label>
      <label>{{ 'new.urgency' | t }}
        <select name="urgency" [(ngModel)]="urgency">
          @for (u of [2, 3, 4, 5]; track u) { <option [ngValue]="u">{{ 'urgency.' + u | t }}</option> }
        </select>
      </label>
      @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
      <button class="btn btn-primary" [disabled]="busy() || !title.trim() || !text.trim() || !entityId">{{ 'new.submit' | t }}</button>
    </form>
  `,
})
export class NewTicketPage {
  private api = inject(Api);
  private auth = inject(Auth);
  private router = inject(Router);
  /** Client users see their own entity; staff may log a request for any client. */
  entities = computed(() => (this.auth.me()?.entities ?? []).filter((e) => e.clientCode));
  entityId: number | null = this.entities()[0]?.id ?? null;
  title = '';
  text = '';
  urgency = 3;
  busy = signal(false);
  error = signal(false);

  async submit() {
    if (!this.entityId) return;
    this.busy.set(true);
    this.error.set(false);
    try {
      const r = await this.api.createTicket(this.entityId, this.title, this.text, this.urgency);
      await this.router.navigate(['/tickets', r.id]);
    } catch { this.error.set(true); }
    finally { this.busy.set(false); }
  }
}
