import { HttpClient, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, firstValueFrom, throwError } from 'rxjs';
import { ActiveBlock, Dashboard, Me, MonthlyReport, Problem, TicketDetail, TicketRow } from './models';

/** Adds the anti-CSRF header the backend requires on writes, and sends users to /login on 401. */
export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router);
  const r = req.method === 'GET' ? req : req.clone({ setHeaders: { 'X-WS-Portal': '1' } });
  return next(r).pipe(
    catchError((e: HttpErrorResponse) => {
      if (e.status === 401 && !req.url.endsWith('/auth/login')) router.navigateByUrl('/login');
      return throwError(() => e);
    }),
  );
};

/** Typed access to the portal backend. */
@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);

  login(username: string, password: string) { return firstValueFrom(this.http.post<Me>('/api/auth/login', { username, password })); }
  logout() { return firstValueFrom(this.http.post<void>('/api/auth/logout', {})); }
  me() { return firstValueFrom(this.http.get<Me>('/api/me')); }
  dashboard() { return firstValueFrom(this.http.get<Dashboard>('/api/dashboard')); }
  tickets(scope: 'open' | 'solved' | 'all', page = 0) { return firstValueFrom(this.http.get<TicketRow[]>('/api/tickets', { params: { scope, page } })); }
  ticket(id: number) { return firstValueFrom(this.http.get<TicketDetail>(`/api/tickets/${id}`)); }
  addFollowup(id: number, text: string) { return firstValueFrom(this.http.post<{ id: number }>(`/api/tickets/${id}/followups`, { text })); }
  createTicket(entityId: number, title: string, text: string, urgency: number) {
    return firstValueFrom(this.http.post<{ id: number }>('/api/tickets', { entityId, title, text, urgency }));
  }
  monthlyReport(client: string, month: string) {
    return firstValueFrom(this.http.get<MonthlyReport>('/api/reports/monthly', { params: { client, month } }));
  }
  containment(ticketId: number) { return firstValueFrom(this.http.get<{ allowed: boolean; blocks: ActiveBlock[] | null }>(`/api/tickets/${ticketId}/containment`)); }
  approveBlock(ticketId: number, ip: string, hours: number, reason: string) {
    return firstValueFrom(this.http.post<{ ok: boolean; errors: string[]; block: ActiveBlock }>(`/api/tickets/${ticketId}/containment`, { ip, hours, reason }));
  }
  removeBlock(id: string) { return firstValueFrom(this.http.delete<void>(`/api/containment/${id}`)); }
  problems() { return firstValueFrom(this.http.get<{ enabled: boolean; problems: Problem[] }>('/api/monitoring/problems')); }
}
