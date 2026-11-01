import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ActiveSession, Administrator, Alert, Application, AppBinding, AuditRow, BackendConfig, Channel, CompanyAssignmentRequest,
  CompanyDetail, CompanyListItem, CompanyRequest, CompanySession, CompanyUser, ConnectivityResult, DashboardFilter, DashboardSummary,
  E2eResult, FailoverPolicy, HealthCurrent, HealthHistoryRow, ImportResult, MaintenanceWindow, Me, Paged, PermissionInfo, Pool,
  PoolMemberRequest, PoolRequest, PreviewResult, ReportDefinition, ReportTable, Role, RoutingConfig, RoutingHistoryRow, RoutingPolicy,
  Server, ServerAssignments, ServerMetric, ServerRequest, ServerSession, ServerUtilization, Setting, SharedPoolAssignmentRequest,
  SimulationRequest, SimulationResult, TimeSeriesBucket, TopologyNode, TransferRequest, TransferResult, UserAssignment,
  UserAssignmentRequest, UserAssignmentView, UserRow,
} from './models';

/** Drops empty values so filters serialise to clean query strings. */
export function toParams(obj: object): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(obj)) {
    if (v === undefined || v === null || v === '') continue;
    p = p.set(k, String(v));
  }
  return p;
}

/** Builds a same-origin URL with query string (used for download links, which rely on the session cookie). */
export function urlWith(path: string, obj: object): string {
  const q = toParams(obj).toString();
  return q ? `${path}?${q}` : path;
}

/** Optional audit reason, recorded by the backend from the X-Audit-Reason header. */
function reasonHeaders(reason?: string | null) {
  const r = reason?.trim();
  // Header values must be ISO-8859-1; strip anything else rather than failing the request.
  return r ? { headers: new HttpHeaders({ 'X-Audit-Reason': r.replace(/[^\x20-\x7e\xa0-\xff]/g, '?').slice(0, 500) }) } : {};
}

const enc = encodeURIComponent;

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  // ---- auth
  csrf() { return this.http.get<void>('/api/auth/csrf'); }
  login(username: string, password: string, mfaCode?: string) { return this.http.post<Me>('/api/auth/login', { username, password, mfaCode: mfaCode || null }); }
  me() { return this.http.get<Me>('/api/auth/me'); }
  logout() { return this.http.post<void>('/api/auth/logout', {}); }
  mfaSetup(currentCode: string | null = null) { return this.http.post<{ provisioningUri: string }>('/api/auth/mfa/setup', { currentCode }); }
  mfaEnable(code: string) { return this.http.post<void>('/api/auth/mfa/enable', { code }); }
  changePassword(currentPassword: string, newPassword: string) { return this.http.post<void>('/api/auth/password', { currentPassword, newPassword }); }

  // ---- dashboard
  summary() { return this.http.get<DashboardSummary>('/api/dashboard/summary'); }
  utilization(hours: number) { return this.http.get<ServerUtilization[]>('/api/dashboard/utilization', { params: toParams({ hours }) }); }
  timeseries(f: DashboardFilter, bucketMinutes: number) { return this.http.get<TimeSeriesBucket[]>('/api/dashboard/timeseries', { params: toParams({ ...f, bucketMinutes }) }); }
  topology() { return this.http.get<TopologyNode[]>('/api/dashboard/topology'); }

  // ---- servers
  servers(q: { environment?: string; search?: string; includeRetired?: boolean } = {}) { return this.http.get<Server[]>('/api/servers', { params: toParams(q) }); }
  server(id: string) { return this.http.get<Server>(`/api/servers/${id}`); }
  createServer(body: ServerRequest) { return this.http.post<{ id: string }>('/api/servers', body); }
  updateServer(id: string, body: ServerRequest) { return this.http.put<void>(`/api/servers/${id}`, body); }
  serverAction(id: string, action: 'enable' | 'disable' | 'maintenance' | 'restore' | 'retire', reason?: string) {
    return this.http.post<{ id: string; previous: string; state: string }>(`/api/servers/${id}/${action}`, {}, reasonHeaders(reason));
  }
  drainServer(id: string, timeoutMinutes: number | null, reason?: string) {
    return this.http.post<{ id: string; previous: string; state: string }>(`/api/servers/${id}/drain`, { timeoutMinutes, reason: reason || null }, reasonHeaders(reason));
  }
  deleteServer(id: string, reason?: string) { return this.http.delete<void>(`/api/servers/${id}`, reasonHeaders(reason)); }
  testServer(id: string) { return this.http.post<ConnectivityResult>(`/api/servers/${id}/test`, {}); }
  serverHealth(id: string, days = 7) { return this.http.get<{ current: HealthCurrent | null; history: HealthHistoryRow[] }>(`/api/servers/${id}/health`, { params: toParams({ days }) }); }
  serverMetrics(id: string, hours = 24) { return this.http.get<ServerMetric[]>(`/api/servers/${id}/metrics`, { params: toParams({ hours }) }); }
  serverAssignments(id: string) { return this.http.get<ServerAssignments>(`/api/servers/${id}/assignments`); }
  serverSessions(id: string) { return this.http.get<ServerSession[]>(`/api/servers/${id}/sessions`); }
  serverHistory(id: string) { return this.http.get<AuditRow[]>(`/api/servers/${id}/history`); }
  setReservation(id: string, companyId: string, reservedSessions: number) { return this.http.put<void>(`/api/servers/${id}/reservations`, { companyId, reservedSessions }); }
  backendConfig(id: string) { return this.http.get<BackendConfig>(`/api/servers/${id}/backend-config`); }

  // ---- companies
  companies(q: { search?: string; status?: string } = {}) { return this.http.get<CompanyListItem[]>('/api/companies', { params: toParams(q) }); }
  company(id: string) { return this.http.get<CompanyDetail>(`/api/companies/${id}`); }
  createCompany(body: CompanyRequest) { return this.http.post<{ id: string }>('/api/companies', body); }
  updateCompany(id: string, body: CompanyRequest) { return this.http.put<void>(`/api/companies/${id}`, body); }
  setCompanyStatus(id: string, status: string, revokeSessions: boolean, reason?: string) {
    return this.http.post<{ status: string; revokedSessions: number }>(`/api/companies/${id}/status`, { status, revokeSessions, reason: reason || null }, reasonHeaders(reason));
  }
  assignCompany(id: string, body: CompanyAssignmentRequest) { return this.http.post<{ id: string; effectiveFrom: string }>(`/api/companies/${id}/assignment`, body, reasonHeaders(body.reason)); }
  previewCompanyAssignment(id: string, assignment: CompanyAssignmentRequest, sampleUsers = 10) {
    return this.http.post<PreviewResult>(`/api/companies/${id}/assignment/preview`, { assignment, sampleUsers });
  }
  assignSharedPool(id: string, body: SharedPoolAssignmentRequest) { return this.http.post<{ id: string }>(`/api/companies/${id}/shared-pool`, body, reasonHeaders(body.reason)); }
  endCompanyAssignment(id: string, assignmentId: string, reason?: string) { return this.http.post<void>(`/api/companies/${id}/assignments/${assignmentId}/end`, {}, reasonHeaders(reason)); }
  transferCompany(id: string, body: TransferRequest) { return this.http.post<TransferResult>(`/api/companies/${id}/transfer`, body, reasonHeaders(body.reason)); }
  authorizeServer(id: string, serverId: string, notes?: string) { return this.http.post<void>(`/api/companies/${id}/authorizations`, { serverId, notes: notes || null }, reasonHeaders(notes)); }
  revokeAuthorization(id: string, serverId: string, reason?: string) { return this.http.delete<void>(`/api/companies/${id}/authorizations/${serverId}`, reasonHeaders(reason)); }
  companyUsers(id: string) { return this.http.get<CompanyUser[]>(`/api/companies/${id}/users`); }
  companySessions(id: string) { return this.http.get<CompanySession[]>(`/api/companies/${id}/sessions`); }
  companyHistory(id: string) { return this.http.get<AuditRow[]>(`/api/companies/${id}/history`); }

  // ---- users
  users(q: { companyId?: string; search?: string; dedicatedOnly?: boolean; take?: number }) { return this.http.get<UserRow[]>('/api/users', { params: toParams(q) }); }
  createUser(body: { companyId: string; username: string; displayName: string; email?: string | null; password?: string | null; externalSubject?: string | null }) {
    return this.http.post<{ id: string }>('/api/users', body);
  }
  setMembershipStatus(userId: string, companyId: string, status: string, reason?: string) {
    return this.http.put<void>(`/api/users/${userId}/status`, { companyId, status, reason: reason || null }, reasonHeaders(reason));
  }
  resetUserPassword(userId: string, password: string) { return this.http.put<void>(`/api/users/${userId}/password`, { password }); }
  userAssignment(userId: string, companyId: string) { return this.http.get<UserAssignmentView>(`/api/users/${userId}/assignment`, { params: toParams({ companyId }) }); }
  setUserAssignment(userId: string, body: UserAssignmentRequest) { return this.http.put<{ id: string; effectiveFrom: string }>(`/api/users/${userId}/assignment`, body, reasonHeaders(body.reason)); }
  removeUserAssignment(userId: string, q: { companyId: string; environment: string; applicationId?: string | null; expectedStamp?: string; reason?: string }) {
    return this.http.delete<void>(`/api/users/${userId}/assignment`, { params: toParams(q), ...reasonHeaders(q.reason) });
  }
  previewUserAssignment(userId: string, assignment: UserAssignmentRequest) { return this.http.post<PreviewResult>(`/api/users/${userId}/assignment/preview`, { assignment }); }
  userAssignmentHistory(userId: string, companyId: string) { return this.http.get<UserAssignment[]>(`/api/users/${userId}/assignment/history`, { params: toParams({ companyId }) }); }
  rollbackUserAssignment(userId: string, assignmentId: string, reason?: string) {
    return this.http.post<{ id: string }>(`/api/users/${userId}/assignment/rollback`, { assignmentId, reason: reason || null }, reasonHeaders(reason));
  }
  bulkImport(file: File, dryRun: boolean) {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ImportResult>('/api/users/bulk-import', form, { params: toParams({ dryRun }) });
  }
  readonly bulkTemplateUrl = '/api/users/bulk-import/template';

  // ---- pools
  pools() { return this.http.get<Pool[]>('/api/pools'); }
  createPool(body: PoolRequest) { return this.http.post<{ id: string }>('/api/pools', body); }
  updatePool(id: string, body: PoolRequest) { return this.http.put<void>(`/api/pools/${id}`, body); }
  deletePool(id: string) { return this.http.delete<void>(`/api/pools/${id}`); }
  upsertPoolMember(id: string, body: PoolMemberRequest) { return this.http.put<void>(`/api/pools/${id}/members`, body); }
  removePoolMember(id: string, serverId: string) { return this.http.delete<void>(`/api/pools/${id}/members/${serverId}`); }

  // ---- policies & applications
  failoverPolicies() { return this.http.get<FailoverPolicy[]>('/api/failover-policies'); }
  saveFailoverPolicy(id: string | null, body: Partial<FailoverPolicy>): Observable<unknown> {
    return id ? this.http.put<void>(`/api/failover-policies/${id}`, body) : this.http.post<{ id: string }>('/api/failover-policies', body);
  }
  routingPolicies() { return this.http.get<RoutingPolicy[]>('/api/routing-policies'); }
  saveRoutingPolicy(id: string | null, body: Partial<RoutingPolicy>): Observable<unknown> {
    return id ? this.http.put<void>(`/api/routing-policies/${id}`, body) : this.http.post<{ id: string }>('/api/routing-policies', body);
  }
  applications() { return this.http.get<Application[]>('/api/applications'); }
  saveApplication(id: string | null, body: Partial<Application>): Observable<unknown> {
    return id ? this.http.put<void>(`/api/applications/${id}`, body) : this.http.post<{ id: string }>('/api/applications', body);
  }
  saveBinding(appId: string, bindingId: string | null, body: Partial<AppBinding>): Observable<unknown> {
    return bindingId ? this.http.put<void>(`/api/applications/${appId}/environments/${bindingId}`, body)
      : this.http.post<{ id: string }>(`/api/applications/${appId}/environments`, body);
  }
  deleteBinding(appId: string, bindingId: string) { return this.http.delete<void>(`/api/applications/${appId}/environments/${bindingId}`); }

  // ---- routing
  simulate(body: SimulationRequest) { return this.http.post<SimulationResult>('/api/routing/simulate', body); }
  e2e(body: { company: string; username: string; password: string; path?: string | null; host?: string | null }) { return this.http.post<E2eResult>('/api/routing/e2e', body); }
  routingHistory(f: DashboardFilter & { outcome?: string; eventKind?: string; skip: number; take: number }) {
    return this.http.get<Paged<RoutingHistoryRow>>('/api/routing/history', { params: toParams(f) });
  }
  routingConfig() { return this.http.get<RoutingConfig>('/api/routing/config'); }

  // ---- operations
  maintenance(includeCompleted: boolean) { return this.http.get<MaintenanceWindow[]>('/api/maintenance', { params: toParams({ includeCompleted }) }); }
  scheduleMaintenance(body: { serverId: string; startsAt: string; endsAt: string; drainTimeoutMinutes: number; autoRestore: boolean; reason?: string | null }) {
    return this.http.post<{ id: string }>('/api/maintenance', body, reasonHeaders(body.reason));
  }
  cancelMaintenance(id: string) { return this.http.post<void>(`/api/maintenance/${id}/cancel`, {}); }
  sessions(q: { companyId?: string; serverId?: string; username?: string; take?: number }) { return this.http.get<ActiveSession[]>('/api/sessions', { params: toParams(q) }); }
  revokeSession(sessionKey: string, reason?: string) { return this.http.post<void>(`/api/sessions/${enc(sessionKey)}/revoke`, { reason: reason || null }, reasonHeaders(reason)); }
  audit(q: { eventType?: string; actor?: string; companyId?: string; serverId?: string; userId?: string; from?: string; to?: string; search?: string; skip?: number; take?: number }) {
    return this.http.get<Paged<AuditRow>>('/api/audit', { params: toParams(q) });
  }
  auditEventTypes() { return this.http.get<string[]>('/api/audit/event-types'); }
  alerts(q: { status?: string; severity?: string; take?: number }) { return this.http.get<Alert[]>('/api/alerts', { params: toParams(q) }); }
  acknowledgeAlert(id: string) { return this.http.post<void>(`/api/alerts/${id}/acknowledge`, {}); }
  resolveAlert(id: string, note?: string) { return this.http.post<void>(`/api/alerts/${id}/resolve`, { note: note || null }, reasonHeaders(note)); }
  channels() { return this.http.get<Channel[]>('/api/alerts/channels'); }
  saveChannel(id: string | null, body: Omit<Channel, 'id'>): Observable<unknown> {
    return id ? this.http.put<void>(`/api/alerts/channels/${id}`, body) : this.http.post<{ id: string }>('/api/alerts/channels', body);
  }
  deleteChannel(id: string) { return this.http.delete<void>(`/api/alerts/channels/${id}`); }
  testChannel(id: string) { return this.http.post<{ success: boolean; error?: string }>(`/api/alerts/channels/${id}/test`, {}); }
  reports() { return this.http.get<ReportDefinition[]>('/api/reports'); }
  runReport(name: string, from?: string, to?: string) { return this.http.get<ReportTable>(`/api/reports/${enc(name)}`, { params: toParams({ from, to }) }); }
  settings() { return this.http.get<Setting[]>('/api/settings'); }
  updateSettings(updates: { key: string; value: string }[]) { return this.http.put<void>('/api/settings', updates); }
  admins() { return this.http.get<Administrator[]>('/api/admins'); }
  roles() { return this.http.get<Role[]>('/api/admins/roles'); }
  permissionCatalog() { return this.http.get<PermissionInfo[]>('/api/admins/permissions'); }
  saveAdmin(id: string | null, body: { username: string; displayName: string; email?: string | null; password?: string | null; status: string; roles: string[] }): Observable<unknown> {
    return id ? this.http.put<void>(`/api/admins/${id}`, body) : this.http.post<{ id: string }>('/api/admins', body);
  }
}

/** Saves a blob response as a file in the browser. */
export function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
