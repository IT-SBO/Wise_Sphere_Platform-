import { Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { TranslatePipe } from '../core/i18n';
import { TicketRow } from '../core/models';
import { TicketTable } from './shared';

@Component({
  selector: 'ws-tickets',
  imports: [TranslatePipe, TicketTable],
  template: `
    <div class="page-head">
      <h1>{{ 'tickets.title' | t }}</h1>
      <div class="tabs" role="tablist">
        @for (s of scopes; track s) {
          <button role="tab" [class.active]="scope() === s" [attr.aria-selected]="scope() === s" (click)="setScope(s)">{{ 'tickets.' + s | t }}</button>
        }
      </div>
    </div>
    @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
    <section class="card">
      <ws-ticket-table [rows]="rows()" [showClient]="auth.isStaff()" />
      @if (more()) { <button class="btn btn-ghost" (click)="next()">{{ 'tickets.more' | t }}</button> }
    </section>
  `,
})
export class TicketsPage implements OnInit {
  protected auth = inject(Auth);
  private api = inject(Api);
  readonly scopes = ['open', 'solved', 'all'] as const;
  scope = signal<'open' | 'solved' | 'all'>('open');
  rows = signal<TicketRow[]>([]);
  more = signal(false);
  error = signal(false);
  private page = 0;

  ngOnInit() { this.load(); }
  setScope(s: 'open' | 'solved' | 'all') { this.scope.set(s); this.page = 0; this.rows.set([]); this.load(); }
  next() { this.page++; this.load(); }

  private async load() {
    try {
      const r = await this.api.tickets(this.scope(), this.page);
      this.rows.update((x) => [...x, ...r]);
      this.more.set(r.length === 25);
      this.error.set(false);
    } catch { this.error.set(true); }
  }
}
