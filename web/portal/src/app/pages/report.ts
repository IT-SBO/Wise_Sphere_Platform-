import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { I18n, TranslatePipe } from '../core/i18n';
import { MonthlyReport, priorityLabel } from '../core/models';

/** RPT-01 monthly client report. Printable: the browser's "Save as PDF" produces the PDF sent to the client. */
@Component({
  selector: 'ws-report',
  imports: [FormsModule, RouterLink, TranslatePipe],
  template: `
    <div class="page-head no-print">
      <h1>{{ 'rep.title' | t }}</h1>
      <form class="inline-form" (ngSubmit)="load()">
        <label>{{ 'rep.client' | t }}
          <select name="client" [(ngModel)]="client">
            @for (c of clients(); track c) { <option [value]="c">{{ c }}</option> }
          </select>
        </label>
        <label>{{ 'rep.month' | t }}<input name="month" type="month" [(ngModel)]="month" required /></label>
        <button class="btn btn-primary" [disabled]="busy()">{{ 'rep.show' | t }}</button>
        @if (r()) { <button type="button" class="btn btn-ghost" (click)="print()">{{ 'rep.print' | t }}</button> }
      </form>
    </div>
    @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
    @if (r(); as r) {
      <article class="report">
        <header class="report-head">
          <div class="report-brand"><img src="brand/logo.svg" alt="WISE Group" /><span>Wise<b>Sphere</b></span></div>
          <div>
            <h1>{{ 'rep.title' | t }}</h1>
            <p><strong>{{ r.clientName }}</strong> ({{ r.client }}) · {{ 'rep.period' | t }}: {{ monthLabel(r.month) }}</p>
            <p class="muted small">{{ 'rep.generated' | t }}: {{ i18n.date(r.generatedAt) }}</p>
          </div>
        </header>

        <h2>{{ 'rep.summary' | t }}</h2>
        <section class="kpis">
          <div class="kpi"><span>{{ 'rep.opened' | t }}</span><strong>{{ r.ticketsOpened }}</strong></div>
          <div class="kpi"><span>{{ 'rep.solved' | t }}</span><strong>{{ r.ticketsSolved }}</strong></div>
          <div class="kpi" [class.warn]="r.stillOpen > 0"><span>{{ 'rep.stillOpen' | t }}</span><strong>{{ r.stillOpen }}</strong></div>
          <div class="kpi" [class.bad]="r.slaMetPercent !== null && r.slaMetPercent < 95">
            <span>{{ 'rep.sla' | t }}</span><strong>{{ r.slaMetPercent === null ? '–' : r.slaMetPercent + '%' }}</strong>
            <small class="muted">{{ r.slaMeasured }} {{ 'rep.slaOf' | t }}</small>
          </div>
          <div class="kpi"><span>{{ 'rep.avgResolve' | t }}</span><strong>{{ r.avgResolveHours ?? '–' }}</strong> <small>{{ 'rep.hours' | t }}</small></div>
          <div class="kpi" [class.warn]="r.csat !== null && r.csat < 4.3">
            <span>{{ 'rep.csat' | t }}</span><strong>{{ r.csat ?? '–' }}</strong><small class="muted"> / 5 · {{ r.csatAnswers }} {{ 'rep.answers' | t }}</small>
          </div>
        </section>

        <div class="grid-2">
          <section class="card"><h2>{{ 'rep.byPriority' | t }}</h2>
            @for (c of r.byPriority; track c.label) { <div class="bar-row"><span class="badge prio-{{ c.label }}">{{ c.label }}</span><span class="bar" [style.width.%]="pct(c.count, r.ticketsOpened)"></span><b>{{ c.count }}</b></div> }
          </section>
          <section class="card"><h2>{{ 'rep.bySource' | t }}</h2>
            @for (c of r.bySource; track c.label) { <div class="bar-row"><span class="bar-label">{{ c.label }}</span><span class="bar" [style.width.%]="pct(c.count, r.ticketsOpened)"></span><b>{{ c.count }}</b></div> }
            @empty { <p class="muted">{{ 'rep.none' | t }}</p> }
          </section>
          <section class="card"><h2>{{ 'rep.monitoring' | t }}</h2>
            <table><tbody>
              <tr><td>{{ 'rep.problems' | t }}</td><td class="num">{{ r.monitoringProblems }}</td></tr>
              <tr><td>{{ 'rep.problemHours' | t }}</td><td class="num">{{ r.monitoringProblemHours }} {{ 'rep.hours' | t }}</td></tr>
              <tr><td>{{ 'rep.storms' | t }}</td><td class="num">{{ r.alertStorms }}</td></tr>
              <tr><td>{{ 'rep.security' | t }}</td><td class="num">{{ r.securityIncidents }}</td></tr>
            </tbody></table>
          </section>
          <section class="card"><h2>{{ 'rep.topAssets' | t }}</h2>
            @for (c of r.topAssets; track c.label) { <div class="bar-row"><span class="bar-label">{{ c.label }}</span><span class="bar" [style.width.%]="pct(c.count, r.topAssets[0].count)"></span><b>{{ c.count }}</b></div> }
            @empty { <p class="muted">{{ 'rep.none' | t }}</p> }
          </section>
        </div>

        <section class="card"><h2>{{ 'rep.major' | t }}</h2>
          @if (r.majorIncidents.length === 0) { <p class="muted">{{ 'rep.none' | t }}</p> }
          @else {
            <table><thead><tr><th>#</th><th>{{ 'tickets.subject' | t }}</th><th>{{ 'tickets.priority' | t }}</th><th>{{ 'rep.openedAt' | t }}</th></tr></thead>
              <tbody>@for (t of r.majorIncidents; track t.id) {
                <tr><td class="num"><a [routerLink]="['/tickets', t.id]">{{ t.id }}</a></td><td>{{ t.title }}</td><td>{{ prio(t.priority) }}</td><td>{{ i18n.date(t.openedAt) }}</td></tr>
              }</tbody></table>
          }
        </section>
        <p class="muted small">{{ 'rep.footer' | t }}</p>
      </article>
    }
  `,
})
export class ReportPage {
  protected i18n = inject(I18n);
  private api = inject(Api);
  private auth = inject(Auth);
  clients = computed(() => [...new Set((this.auth.me()?.entities ?? []).map((e) => e.clientCode).filter((c) => !!c))].sort());
  client = this.clients()[0] ?? '';
  month = lastMonth();
  r = signal<MonthlyReport | null>(null);
  busy = signal(false);
  error = signal(false);
  prio = priorityLabel;

  async load() {
    this.busy.set(true);
    this.error.set(false);
    try { this.r.set(await this.api.monthlyReport(this.client, this.month)); } catch { this.error.set(true); this.r.set(null); }
    finally { this.busy.set(false); }
  }
  print() { window.print(); }
  pct(n: number, total: number) { return total > 0 ? Math.max(2, (100 * n) / total) : 0; }
  monthLabel(m: string) {
    return new Intl.DateTimeFormat(this.i18n.lang() === 'ar' ? 'ar-KW' : 'en-GB', { month: 'long', year: 'numeric' }).format(new Date(m + '-01T00:00:00'));
  }
}

function lastMonth(): string {
  const d = new Date();
  d.setDate(1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}
