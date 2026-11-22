import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Auth } from '../core/auth';
import { I18n, TranslatePipe } from '../core/i18n';
import { ActiveBlock, STATUS, TicketDetail, priorityLabel } from '../core/models';

/** Ticket with its history. Ticket text is HTML from GLPI; Angular's [innerHTML] sanitises it. */
@Component({
  selector: 'ws-ticket-detail',
  imports: [FormsModule, RouterLink, TranslatePipe],
  template: `
    <a routerLink="/tickets" class="back">← {{ 'ticket.back' | t }}</a>
    @if (notFound()) { <p class="alert">{{ 'ticket.notFound' | t }}</p> }
    @if (d(); as d) {
      <div class="page-head">
        <h1>#{{ d.ticket.id }} {{ d.ticket.title }}</h1>
        @if (auth.isStaff()) { <a class="btn btn-ghost btn-sm" [href]="d.glpiUrl" target="_blank" rel="noopener">{{ 'ticket.openGlpi' | t }}</a> }
      </div>
      <div class="meta">
        <span class="badge prio-{{ prio(d.ticket.priority) }}">{{ prio(d.ticket.priority) }}</span>
        <span class="badge status-{{ d.ticket.status }}">{{ status(d.ticket.status) | t }}</span>
        <span>{{ d.ticket.entity }}</span>
        @if (d.ticket.source) { <span class="muted">{{ 'tickets.source' | t }}: {{ d.ticket.source }}</span> }
        <span class="muted">{{ 'tickets.due' | t }}: {{ i18n.date(d.ticket.dueAt) }}</span>
      </div>
      <section class="card"><h2>{{ 'ticket.description' | t }}</h2><div class="rich" [innerHTML]="d.content"></div></section>
      <section class="card">
        <h2>{{ 'ticket.history' | t }}</h2>
        <ol class="timeline">
          @for (i of d.timeline; track $index) {
            <li class="tl-{{ i.kind }}">
              <div class="tl-head">
                <strong>{{ 'ticket.' + i.kind | t }}</strong>
                <span class="muted">{{ i.author }} · {{ i18n.date(i.at) }}</span>
                @if (i.isPrivate) { <span class="badge">{{ 'ticket.private' | t }}</span> }
              </div>
              <div class="rich" [innerHTML]="i.content"></div>
            </li>
          } @empty { <li class="muted">{{ 'dash.none' | t }}</li> }
        </ol>
      </section>
      @if (canContain()) {
        <section class="card containment">
          <h2>{{ 'cont.title' | t }}</h2>
          <p class="muted small">{{ 'cont.help' | t }}</p>
          @for (b of blocks(); track b.id) {
            <div class="block-row">
              <code>{{ b.ip }}</code>
              <span>{{ 'cont.until' | t }} {{ i18n.date(b.expiresAt) }} · {{ 'cont.by' | t }} {{ b.approvedBy }} · {{ b.firewallType }}</span>
              <button class="btn btn-ghost btn-sm" (click)="unblock(b.id)">{{ 'cont.remove' | t }}</button>
            </div>
          }
          <form class="inline-form" (ngSubmit)="block()">
            <label>{{ 'cont.ip' | t }}<input name="ip" [(ngModel)]="ip" required /></label>
            <label>{{ 'cont.hours' | t }}
              <select name="hours" [(ngModel)]="hours">@for (h of [1, 4, 8, 24]; track h) { <option [ngValue]="h">{{ h }} {{ 'cont.h' | t }}</option> }</select>
            </label>
            <label class="grow">{{ 'cont.reason' | t }}<input name="reason" [(ngModel)]="reason" required /></label>
            <button class="btn btn-danger" [disabled]="busy() || !ip.trim() || !reason.trim()">{{ 'cont.approve' | t }}</button>
          </form>
          @for (e of blockErrors(); track e) { <p class="alert">{{ e }}</p> }
          @if (blockedMsg()) { <p class="ok">{{ 'cont.done' | t }}</p> }
        </section>
      }
      @if (d.ticket.status < 6) {
        <section class="card">
          <h2>{{ 'ticket.reply' | t }}</h2>
          <form (ngSubmit)="reply()">
            <textarea name="text" [(ngModel)]="text" rows="4" maxlength="10000" required></textarea>
            @if (error()) { <p class="alert">{{ 'common.error' | t }}</p> }
            <button class="btn btn-primary" [disabled]="busy() || !text.trim()">{{ 'ticket.send' | t }}</button>
          </form>
        </section>
      }
    }
  `,
})
export class TicketDetailPage implements OnInit {
  protected auth = inject(Auth);
  protected i18n = inject(I18n);
  private api = inject(Api);
  id = input.required<string>();
  d = signal<TicketDetail | null>(null);
  notFound = signal(false);
  busy = signal(false);
  error = signal(false);
  text = '';
  ip = '';
  hours = 24;
  reason = '';
  blocks = signal<ActiveBlock[]>([]);
  blockErrors = signal<string[]>([]);
  blockedMsg = signal(false);
  canContain = signal(false);
  prio = priorityLabel;
  status = (s: number) => STATUS[s] ?? String(s);

  ngOnInit() { this.load(); }

  async load() {
    try {
      const d = await this.api.ticket(Number(this.id()));
      this.d.set(d);
      // SOC-04: containment panel only on Wazuh security tickets, for members of the SOC group
      if (this.auth.me()?.isSoc && d.ticket.title.startsWith('[WZH:')) {
        const c = await this.api.containment(d.ticket.id);
        this.canContain.set(c.allowed);
        this.blocks.set(c.blocks ?? []);
        if (!this.ip) this.ip = /Source IP:<\/b>\s*([0-9a-fA-F:.]+)/.exec(d.content)?.[1] ?? '';
      }
    } catch { this.notFound.set(true); }
  }

  async block() {
    if (!confirm(this.i18n.t('cont.confirm'))) return;
    this.busy.set(true);
    this.blockErrors.set([]);
    this.blockedMsg.set(false);
    try {
      await this.api.approveBlock(Number(this.id()), this.ip.trim(), this.hours, this.reason.trim());
      this.blockedMsg.set(true);
      this.reason = '';
      await this.load();
    } catch (e: unknown) {
      const errs = (e as { error?: { errors?: string[] } })?.error?.errors;
      this.blockErrors.set(errs?.length ? errs : [this.i18n.t('common.error')]);
    } finally { this.busy.set(false); }
  }

  async unblock(id: string) {
    await this.api.removeBlock(id);
    await this.load();
  }

  async reply() {
    this.busy.set(true);
    this.error.set(false);
    try { await this.api.addFollowup(Number(this.id()), this.text); this.text = ''; await this.load(); }
    catch { this.error.set(true); }
    finally { this.busy.set(false); }
  }
}
