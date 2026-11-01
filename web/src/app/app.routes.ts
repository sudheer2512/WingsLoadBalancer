import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { AuthService, P, authGuard, permissionGuard } from './core/auth.service';
import { Forbidden, Login, Shell } from './shell';

/** First screen the signed-in administrator may open. */
function home() {
  const auth = inject(AuthService);
  // Redirects run before the shell's auth guard: signed-out visitors must reach the login page without a
  // misleading returnUrl, so send them to the dashboard and let the guard take over.
  if (!auth.authenticated()) return 'dashboard';
  const order: [string, string][] = [
    [P.monitoringRead, 'dashboard'], [P.serversRead, 'servers'], [P.companiesRead, 'companies'], [P.auditRead, 'audit'],
    [P.reportsRead, 'reports'], [P.adminsManage, 'admins'],
  ];
  return order.find(([p]) => auth.can(p))?.[1] ?? 'forbidden';
}

const guarded = (perm: string | string[]) => ({ canActivate: [permissionGuard], data: { perm } });

export const routes: Routes = [
  { path: 'login', component: Login, title: 'Sign in · Wings Load Balancer' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: home },
      { path: 'forbidden', component: Forbidden, title: 'No permission' },
      { path: 'dashboard', ...guarded(P.monitoringRead), loadComponent: () => import('./dashboard/dashboard').then(m => m.Dashboard), title: 'Dashboard' },
      { path: 'servers', ...guarded(P.serversRead), loadComponent: () => import('./servers/servers').then(m => m.Servers), title: 'Servers' },
      { path: 'servers/:id', ...guarded(P.serversRead), loadComponent: () => import('./servers/server-detail').then(m => m.ServerDetail), title: 'Server' },
      { path: 'companies', ...guarded(P.companiesRead), loadComponent: () => import('./companies/companies').then(m => m.Companies), title: 'Companies' },
      { path: 'companies/:id', ...guarded(P.companiesRead), loadComponent: () => import('./companies/company-detail').then(m => m.CompanyDetailPage), title: 'Company' },
      { path: 'users', ...guarded(P.companiesRead), loadComponent: () => import('./users/users').then(m => m.Users), title: 'User assignments' },
      { path: 'users/bulk', ...guarded(P.assignmentsWrite), loadComponent: () => import('./users/bulk-import').then(m => m.BulkImport), title: 'Bulk upload' },
      { path: 'pools', ...guarded(P.poolsRead), loadComponent: () => import('./pools/pools').then(m => m.Pools), title: 'Server pools' },
      { path: 'policies', ...guarded(P.poolsRead), loadComponent: () => import('./policies/policies').then(m => m.Policies), title: 'Policies & applications' },
      { path: 'routing/simulator', ...guarded(P.routingSimulate), loadComponent: () => import('./routing/simulator').then(m => m.Simulator), title: 'Routing simulator' },
      { path: 'routing/history', ...guarded(P.monitoringRead), loadComponent: () => import('./routing/history').then(m => m.RoutingHistory), title: 'Routing history' },
      { path: 'routing/config', ...guarded(P.monitoringRead), loadComponent: () => import('./routing/config').then(m => m.RoutingConfigPage), title: 'Configuration' },
      { path: 'maintenance', ...guarded(P.serversRead), loadComponent: () => import('./ops/maintenance').then(m => m.Maintenance), title: 'Maintenance windows' },
      { path: 'sessions', ...guarded(P.sessionsRead), loadComponent: () => import('./ops/sessions').then(m => m.Sessions), title: 'Active sessions' },
      { path: 'alerts', ...guarded(P.alertsRead), loadComponent: () => import('./ops/alerts').then(m => m.Alerts), title: 'Alerts' },
      { path: 'audit', ...guarded(P.auditRead), loadComponent: () => import('./ops/audit').then(m => m.Audit), title: 'Audit log' },
      { path: 'reports', ...guarded(P.reportsRead), loadComponent: () => import('./ops/reports').then(m => m.Reports), title: 'Reports' },
      { path: 'settings', ...guarded(P.monitoringRead), loadComponent: () => import('./ops/settings').then(m => m.Settings), title: 'Settings' },
      { path: 'admins', ...guarded(P.adminsManage), loadComponent: () => import('./ops/admins').then(m => m.Admins), title: 'Administrators' },
    ],
  },
  { path: '**', redirectTo: '' },
];
