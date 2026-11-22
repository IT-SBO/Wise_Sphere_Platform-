import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { I18n, TranslatePipe } from '../core/i18n';
import { Problem } from '../core/models';

/** Current Zabbix problems for the user's clients (DSH-02), refreshed every 30 s. */
@Component({
  selector: 'ws-monitoring',
  imports: [TranslatePipe],
  template: `
    <div class="page-head"><h1>{{ 'mon.title' | t }}</h1><button class="btn btn-ghost btn-sm" (click)="load()">{{ 'common.refresh' | t }}</button></div>
    @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
    @if (!enabled()) { <p class="muted">{{ 'mon.disabled' | t }}</p> }
    @else {
      <section class="card">
        @if (problems().length === 0) { <p class="muted">{{ 'mon.none' | t }}</p> }
        @else {
          <div class="table-wrap"><table>
            <thead><tr>
              <th>{{ 'mon.severity' | t }}</th>@if (auth.isStaff()) { <th>{{ 'tickets.client' | t }}</th> }
              <th>{{ 'mon.host' | t }}</th><th>{{ 'mon.problem' | t }}</th><th>{{ 'mon.since' | t }}</th><th>{{ 'mon.ack' | t }}</th>
            </tr></thead>
            <tbody>
              @for (p of problems(); track p.eventId) {
                <tr>
                  <td><span class="badge sev-{{ p.severity }}">{{ 'sev.' + p.severity | t }}</span></td>
                  @if (auth.isStaff()) { <td>{{ p.client || '–' }}</td> }
                  <td>{{ p.host }}</td><td>{{ p.name }}</td><td>{{ i18n.date(p.since) }}</td>
                  <td>{{ (p.acknowledged ? 'common.yes' : 'common.no') | t }}</td>
                </tr>
              }
            </tbody>
          </table></div>
        }
      </section>
    }
  `,
})
export class MonitoringPage implements OnInit, OnDestroy {
  protected auth = inject(Auth);
  protected i18n = inject(I18n);
  private api = inject(Api);
  problems = signal<Problem[]>([]);
  enabled = signal(true);
  error = signal(false);
  private timer?: ReturnType<typeof setInterval>;

  ngOnInit() { this.load(); this.timer = setInterval(() => this.load(), 30_000); }
  ngOnDestroy() { clearInterval(this.timer); }

  async load() {
    try {
      const r = await this.api.problems();
      this.enabled.set(r.enabled);
      this.problems.set(r.problems.sort((a, b) => b.severity - a.severity));
      this.error.set(false);
    } catch { this.error.set(true); }
  }
}
