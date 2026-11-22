import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { TranslatePipe } from '../core/i18n';
import { Dashboard } from '../core/models';
import { TicketTable } from './shared';

/** NOC / client overview: KPIs, client health matrix (DSH-01), nearest SLA breaches (DSH-03). Refreshes every 30 s. */
@Component({
  selector: 'ws-dashboard',
  imports: [TranslatePipe, TicketTable],
  template: `
    <div class="page-head"><h1>{{ 'nav.dashboard' | t }}</h1><button class="btn btn-ghost btn-sm" (click)="load()">{{ 'common.refresh' | t }}</button></div>
    @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
    @if (d(); as d) {
      <section class="kpis">
        <div class="kpi"><span>{{ 'dash.open' | t }}</span><strong>{{ d.open }}</strong></div>
        <div class="kpi" [class.bad]="d.openP1 > 0"><span>{{ 'dash.p1' | t }}</span><strong>{{ d.openP1 }}</strong></div>
        <div class="kpi" [class.warn]="d.openP2 > 0"><span>{{ 'dash.p2' | t }}</span><strong>{{ d.openP2 }}</strong></div>
        <div class="kpi" [class.warn]="d.atRisk > 0"><span>{{ 'dash.atRisk' | t }}</span><strong>{{ d.atRisk }}</strong></div>
        <div class="kpi" [class.bad]="d.breached > 0"><span>{{ 'dash.breached' | t }}</span><strong>{{ d.breached }}</strong></div>
        <div class="kpi" [class.bad]="d.storms > 0"><span>{{ 'dash.storms' | t }}</span><strong>{{ d.storms }}</strong></div>
      </section>

      @if (d.clients.length > 0) {
        <section class="card">
          <h2>{{ 'dash.health' | t }}</h2>
          <div class="health">
            @for (c of d.clients; track c.client) {
              <div class="health-tile {{ c.status }}">
                <div class="health-head"><strong>{{ c.client }}</strong><span class="badge health-{{ c.status }}">{{ 'health.' + c.status | t }}</span></div>
                <div class="muted small">{{ c.name }}</div>
                <div class="health-nums">
                  <span>P1 <b>{{ c.openP1 }}</b></span><span>P2 <b>{{ c.openP2 }}</b></span>
                  <span>{{ 'dash.open' | t }} <b>{{ c.open }}</b></span><span>{{ 'dash.breached' | t }} <b>{{ c.breached }}</b></span>
                </div>
              </div>
            }
          </div>
        </section>
      }

      <div class="grid-2">
        <section class="card"><h2>{{ 'dash.urgent' | t }}</h2><ws-ticket-table [rows]="d.urgent" [showClient]="auth.isStaff()" [compact]="true" /></section>
        <section class="card"><h2>{{ 'dash.recent' | t }}</h2><ws-ticket-table [rows]="d.recent" [showClient]="auth.isStaff()" [compact]="true" /></section>
      </div>
    } @else if (!error()) { <p class="muted">{{ 'common.loading' | t }}</p> }
  `,
})
export class DashboardPage implements OnInit, OnDestroy {
  protected auth = inject(Auth);
  private api = inject(Api);
  d = signal<Dashboard | null>(null);
  error = signal(false);
  private timer?: ReturnType<typeof setInterval>;

  ngOnInit() { this.load(); this.timer = setInterval(() => this.load(), 30_000); }
  ngOnDestroy() { clearInterval(this.timer); }

  async load() {
    try { this.d.set(await this.api.dashboard()); this.error.set(false); } catch { this.error.set(true); }
  }
}
