import { Routes } from '@angular/router';
import { authGuard } from './core/auth';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login').then((m) => m.LoginPage) },
  {
    path: '',
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./pages/dashboard').then((m) => m.DashboardPage) },
      { path: 'tickets', loadComponent: () => import('./pages/tickets').then((m) => m.TicketsPage) },
      { path: 'tickets/:id', loadComponent: () => import('./pages/ticket-detail').then((m) => m.TicketDetailPage) },
      { path: 'new', loadComponent: () => import('./pages/new-ticket').then((m) => m.NewTicketPage) },
      { path: 'reports', loadComponent: () => import('./pages/report').then((m) => m.ReportPage) },
      { path: 'monitoring', loadComponent: () => import('./pages/monitoring').then((m) => m.MonitoringPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
