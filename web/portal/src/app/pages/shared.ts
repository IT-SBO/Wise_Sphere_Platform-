import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { I18n, TranslatePipe } from '../core/i18n';
import { STATUS, TicketRow, priorityLabel } from '../core/models';
import { inject } from '@angular/core';

/** Reusable ticket table (dashboard + tickets page). */
@Component({
  selector: 'ws-ticket-table',
  imports: [RouterLink, TranslatePipe],
  template: `
    @if (rows().length === 0) { <p class="muted">{{ 'dash.none' | t }}</p> }
    @else {
      <div class="table-wrap">
        <table>
          <thead><tr>
            <th>{{ 'tickets.id' | t }}</th><th>{{ 'tickets.subject' | t }}</th><th>{{ 'tickets.priority' | t }}</th>
            @if (!compact()) { <th>{{ 'tickets.status' | t }}</th> }@if (showClient()) { <th>{{ 'tickets.client' | t }}</th> }
            <th>{{ 'tickets.due' | t }}</th>@if (!compact()) { <th>{{ 'tickets.updated' | t }}</th> }
          </tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr>
                <td class="num">{{ r.id }}</td>
                <td class="subject"><a [routerLink]="['/tickets', r.id]">{{ r.title }}</a></td>
                <td><span class="badge prio-{{ prio(r.priority) }}">{{ prio(r.priority) }}</span></td>
                @if (!compact()) { <td><span class="badge status-{{ r.status }}">{{ status(r.status) | t }}</span></td> }
                @if (showClient()) { <td>{{ r.client || '–' }}</td> }
                <td [class.overdue]="overdue(r)">{{ i18n.date(r.dueAt) }}</td>
                @if (!compact()) { <td>{{ i18n.date(r.updatedAt) }}</td> }
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class TicketTable {
  protected i18n = inject(I18n);
  rows = input.required<TicketRow[]>();
  showClient = input(true);
  /** Dashboard cards: fewer columns so half-width tables fit. */
  compact = input(false);
  prio = priorityLabel;
  status = (s: number) => STATUS[s] ?? String(s);
  overdue = (r: TicketRow) => r.status < 5 && !!r.dueAt && new Date(r.dueAt) < new Date();
}
