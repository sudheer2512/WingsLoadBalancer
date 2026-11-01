import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { TabDef, Tabs } from '../shared/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, isConflict } from '../core/auth.service';
import { AuditRow, CompanyDetail, CompanyRequest, CompanySession, CompanyUser } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { AuditTable } from '../shared/audit-table';
import { AgoPipe, HumanizePipe, StatusChip } from '../shared/display';
import { AssignCompanyDialog, CompanyStatusDialog, SharedPoolDialog, TransferDialog } from './company-dialogs';
import { CompanyForm, cleanCompany, companyToRequest } from './company-form';

@Component({
  selector: 'app-company-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tabs, DatePipe, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, MatProgressBarModule,
    MatFormFieldModule, MatInputModule, MatSelectModule, StatusChip, AgoPipe, HumanizePipe, CompanyForm, AuditTable],
  template: `
    <div class="page">
      <div class="page-head">
        <a class="crumb" routerLink="/companies">Companies</a><span class="muted">/</span>
        <h1>{{ company()?.code ?? '…' }}</h1>
        @if (company(); as c) {
          <span>{{ c.name }}</span><app-status [value]="c.status" /><span class="tag">{{ c.routingMode | humanize }}</span>
          @if (c.servicePlan) { <span class="tag">{{ c.servicePlan }}</span> }
        }
        <span class="spacer"></span>
        @if (company(); as c) {
          @if (auth.can(P.routingSimulate)) {
            <a mat-stroked-button [routerLink]="['/routing/simulator']" [queryParams]="{ company: c.code, environment: c.defaultEnvironment }"><mat-icon>alt_route</mat-icon>Simulate routing</a>
          }
          @if (auth.can(P.assignmentsWrite)) { <button mat-stroked-button (click)="transfer()"><mat-icon>move_up</mat-icon>Transfer…</button> }
          @if (auth.can(P.companiesWrite)) {
            <button mat-flat-button [class.danger]="c.status === 'Active'" (click)="setStatus()">
              <mat-icon>{{ c.status === 'Active' ? 'block' : 'check_circle' }}</mat-icon>{{ c.status === 'Active' ? 'Disable access…' : 'Enable access…' }}</button>
          }
        }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (company(); as c) {
        <app-tabs [tabs]="tabs()" [(active)]="tab" (changed)="selectTab($event)">
          @switch (tab()) {
          @case ('overview') {
            <div class="overview">
              <div class="panel">
                <h2>Routing summary</h2>
                <dl class="kv">
                  <dt>Routing mode</dt><dd>{{ c.routingMode | humanize }}</dd>
                  <dt>Routing policy</dt><dd>{{ c.routingPolicy ?? 'Default' }}</dd>
                  <dt>Default environment</dt><dd>{{ c.defaultEnvironment }}</dd>
                  @for (a of currentAssignments(); track a.id) {
                    <dt>{{ a.environment }}{{ a.application ? ' · ' + a.application : '' }}</dt>
                    <dd>Primary <b>{{ a.primaryServer }}</b>{{ a.secondaryServer ? ', secondary ' + a.secondaryServer : '' }}
                      {{ a.clusterPool ? ', cluster ' + a.clusterPool + ' (' + a.clusterMode + ')' : '' }}<div class="muted small">Failover: {{ a.failoverPolicy ?? 'none' }}</div></dd>
                  }
                  @for (a of currentPools(); track a.id) {
                    <dt>{{ a.environment }} shared pool</dt><dd><b>{{ a.pool }}</b> (priority {{ a.priority }})</dd>
                  }
                  <dt>Authorised servers</dt><dd>{{ c.authorizedServerIds.length }}</dd>
                  <dt>Created</dt><dd>{{ c.createdAt | date: 'medium' }}</dd>
                  <dt>Modified</dt><dd>{{ c.modifiedAt | date: 'medium' }} by {{ c.modifiedBy ?? '—' }}</dd>
                </dl>
              </div>
              <div class="panel">
                <div class="panel-head"><h2>Company details</h2><span class="spacer"></span>
                  @if (auth.can(P.companiesWrite)) {
                    <button mat-button (click)="resetForm()">Discard changes</button>
                    <button mat-flat-button [disabled]="busy()" (click)="save()"><mat-icon>save</mat-icon>Save</button>
                  }</div>
                @if (formErrors().length) { <div class="callout bad"><ul>@for (e of formErrors(); track e) { <li>{{ e }}</li> }</ul></div> }
                @if (form(); as f) { <app-company-form [model]="f" [readonly]="!auth.can(P.companiesWrite)" /> }
              </div>
            </div>
          }

          @case ('assignments') {
            <div class="panel">
              <div class="panel-head"><h2>Server assignments</h2><span class="muted small">Effective-dated; changes close the previous row</span><span class="spacer"></span>
                @if (auth.can(P.assignmentsWrite)) {
                  <button mat-stroked-button (click)="assignPool()"><mat-icon>hub</mat-icon>Assign shared pool…</button>
                  <button mat-flat-button (click)="assign()"><mat-icon>dns</mat-icon>Assign / change server…</button>
                }</div>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>State</th><th>Environment</th><th>Application</th><th>Primary</th><th>Secondary</th><th>Cluster</th><th>Failover policy</th>
                  <th>From</th><th>To</th><th>Reason</th><th>By</th><th></th></tr></thead>
                <tbody>@for (a of c.companyAssignments; track a.id) {
                  <tr><td><app-status [value]="a.state" /></td><td>{{ a.environment }}</td><td>{{ a.application ?? 'All' }}</td>
                    <td><a [routerLink]="['/servers', a.primaryServerId]">{{ a.primaryServer }}</a></td>
                    <td>@if (a.secondaryServerId) { <a [routerLink]="['/servers', a.secondaryServerId]">{{ a.secondaryServer }}</a> } @else { — }</td>
                    <td>{{ a.clusterPool ? a.clusterPool + ' · ' + (a.clusterMode | humanize) : '—' }}</td><td class="small">{{ a.failoverPolicy ?? '—' }}</td>
                    <td class="nowrap">{{ a.effectiveFrom | date: 'short' }}</td><td class="nowrap">{{ (a.effectiveTo | date: 'short') ?? 'open' }}</td>
                    <td class="small">{{ a.reason }}</td><td class="small">{{ a.createdBy }}</td>
                    <td>@if (auth.can(P.assignmentsWrite) && (a.state === 'Current' || a.state === 'Scheduled')) { <button mat-button (click)="end(a.id, 'server assignment')">End</button> }</td></tr>
                } @empty { <tr><td colspan="12" class="empty">No dedicated or cluster assignments.</td></tr> }</tbody>
              </table></div>
              <h3 class="gap">Shared pool assignments</h3>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>State</th><th>Environment</th><th>Application</th><th>Pool</th><th class="num">Priority</th><th>From</th><th>To</th><th>Reason</th><th>By</th><th></th></tr></thead>
                <tbody>@for (a of c.poolAssignments; track a.id) {
                  <tr><td><app-status [value]="a.state" /></td><td>{{ a.environment }}</td><td>{{ a.application ?? 'All' }}</td><td><a routerLink="/pools">{{ a.pool }}</a></td>
                    <td class="num">{{ a.priority }}</td><td class="nowrap">{{ a.effectiveFrom | date: 'short' }}</td><td class="nowrap">{{ (a.effectiveTo | date: 'short') ?? 'open' }}</td>
                    <td class="small">{{ a.reason }}</td><td class="small">{{ a.createdBy }}</td>
                    <td>@if (auth.can(P.assignmentsWrite) && (a.state === 'Current' || a.state === 'Scheduled')) { <button mat-button (click)="end(a.id, 'pool assignment')">End</button> }</td></tr>
                } @empty { <tr><td colspan="10" class="empty">No shared pool assignments.</td></tr> }</tbody>
              </table></div>
            </div>
          }

          @case ('auth') {
            <div class="panel">
              <h2>Servers authorised for this company's data</h2>
              <p class="muted small">User-level assignments may only use authorised servers. Servers named by the company's own assignments, cluster pools, failover pools and shared pools are authorised implicitly; other servers need an explicit grant.</p>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Server</th><th>Grant</th><th>Granted</th><th>By</th><th>Notes</th><th></th></tr></thead>
                <tbody>
                  @for (g of c.explicitAuthorizations; track g.id) {
                    <tr><td><a [routerLink]="['/servers', g.serverId]">{{ g.server }}</a></td><td><span class="tag">Explicit</span></td><td>{{ g.grantedAt | date: 'short' }}</td>
                      <td>{{ g.grantedBy }}</td><td>{{ g.notes }}</td>
                      <td>@if (auth.can(P.assignmentsWrite)) { <button mat-button class="danger" (click)="revokeGrant(g.serverId, g.server)">Revoke</button> }</td></tr>
                  }
                  @for (id of implicitServers(); track id) {
                    <tr><td><a [routerLink]="['/servers', id]">{{ lookups.serverName(id) }}</a></td><td><span class="tag">Implicit (assignment / pool)</span></td><td colspan="4" class="muted small">Granted through the company's routing configuration</td></tr>
                  }
                  @if (!c.explicitAuthorizations.length && !implicitServers().length) { <tr><td colspan="6" class="empty">No servers are authorised.</td></tr> }
                </tbody>
              </table></div>
              @if (auth.can(P.assignmentsWrite)) {
                <div class="row grant">
                  <mat-form-field class="wide"><mat-label>Grant server</mat-label>
                    <mat-select [(ngModel)]="grantServer">@for (s of grantable(); track s.id) { <mat-option [value]="s.id">{{ s.name }} · {{ s.serverType }} · {{ s.environment }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field class="wide"><mat-label>Notes / reason</mat-label><input matInput [(ngModel)]="grantNotes" maxlength="500" /></mat-form-field>
                  <button mat-flat-button [disabled]="!grantServer" (click)="grant()"><mat-icon>add_moderator</mat-icon>Grant</button>
                </div>
              }
            </div>
          }

          @case ('users') {
            <div class="panel">
              <div class="panel-head"><h2>Users ({{ users().length }})</h2><span class="spacer"></span>
                <a mat-stroked-button routerLink="/users" [queryParams]="{ companyId: c.id }"><mat-icon>manage_accounts</mat-icon>Manage user assignments</a></div>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Username</th><th>Display name</th><th>E-mail</th><th>Membership</th><th>Account</th><th>MFA</th><th>Last sign-in</th></tr></thead>
                <tbody>@for (u of users(); track u.userId) {
                  <tr class="clickable" (click)="openUser(u)"><td><b>{{ u.username }}</b></td><td>{{ u.displayName }}</td><td>{{ u.email }}</td>
                    <td><app-status [value]="u.status" /></td><td><app-status [value]="u.userStatus" /></td><td>{{ u.mfaEnabled ? 'On' : 'Off' }}</td><td>{{ u.lastLoginAt | ago }}</td></tr>
                } @empty { <tr><td colspan="7" class="empty">No users.</td></tr> }</tbody>
              </table></div>
            </div>
          }

            @case ('sessions') {
              <div class="panel">
                <div class="panel-head"><h2>Active sessions ({{ sessions().length }})</h2><span class="spacer"></span>
                  <button mat-button (click)="loadSessions()"><mat-icon>refresh</mat-icon>Refresh</button></div>
                <div class="table-wrap"><table class="data">
                  <thead><tr><th>User</th><th>Application</th><th>Server</th><th>Started</th><th>Last seen</th><th>Gateway</th><th></th></tr></thead>
                  <tbody>@for (s of sessions(); track s.sessionKey + s.applicationId) {
                    <tr><td>{{ s.username }}</td><td>{{ lookups.appCode(s.applicationId) }}</td>
                      <td>@if (s.serverId) { <a [routerLink]="['/servers', s.serverId]">{{ lookups.serverName(s.serverId) }}</a> }</td>
                      <td>{{ s.startedAt | date: 'short' }}</td><td>{{ s.lastSeenAt | ago }}</td><td>{{ s.gatewayInstance }}</td>
                      <td>@if (auth.can(P.sessionsRevoke)) { <button mat-button class="danger" (click)="revoke(s)">Revoke</button> }</td></tr>
                  } @empty { <tr><td colspan="7" class="empty">No active sessions.</td></tr> }</tbody>
                </table></div>
              </div>
            }
            @case ('history') {<div class="panel"><app-audit-table [rows]="history()" /></div>}
        }
        </app-tabs>
      }
    </div>`,
  styles: `
    .overview { display: grid; grid-template-columns: minmax(280px, 1fr) minmax(320px, 2fr); gap: 12px; }
    @media (max-width: 1000px) { .overview { grid-template-columns: 1fr; } }
    .gap { margin: 14px 0 4px; } .nowrap { white-space: nowrap; } .grant { margin-top: 8px; flex-wrap: wrap; } .wide { width: 280px; }
  `,
})
export class CompanyDetailPage implements OnInit {
  readonly P = P;
  id = input.required<string>();
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  lookups = inject(LookupService);
  auth = inject(AuthService);

  company = signal<CompanyDetail | null>(null);
  form = signal<CompanyRequest | null>(null);
  formErrors = signal<string[]>([]);
  busy = signal(false);
  users = signal<CompanyUser[]>([]);
  sessions = signal<CompanySession[]>([]);
  history = signal<AuditRow[]>([]);
  grantServer = '';
  grantNotes = '';
  tab = signal('overview');
  tabs = computed<TabDef[]>(() => [
    { key: 'overview', label: 'Overview' }, { key: 'assignments', label: 'Assignments' }, { key: 'auth', label: 'Data authorisations' },
    { key: 'users', label: 'Users' }, ...(this.auth.can(P.sessionsRead) ? [{ key: 'sessions', label: 'Sessions' }] : []),
    ...(this.auth.can(P.auditRead) ? [{ key: 'history', label: 'History' }] : []),
  ]);

  currentAssignments = computed(() => this.company()?.companyAssignments.filter(a => a.state === 'Current') ?? []);
  currentPools = computed(() => this.company()?.poolAssignments.filter(a => a.state === 'Current') ?? []);
  implicitServers = computed(() => {
    const c = this.company();
    if (!c) return [];
    const explicit = new Set(c.explicitAuthorizations.map(g => g.serverId));
    return c.authorizedServerIds.filter(id => !explicit.has(id));
  });
  grantable = computed(() => {
    const c = this.company();
    const have = new Set(c?.authorizedServerIds ?? []);
    return this.lookups.servers().filter(s => !have.has(s.id) && s.adminState !== 'Retired');
  });

  ngOnInit() {
    this.load(true);
    this.lookups.ensure(['servers', 'applications']);
  }

  async load(resetForm = false) {
    try {
      const c = await firstValueFrom(this.api.company(this.id()));
      this.company.set(c);
      if (resetForm || !this.form()) this.form.set(companyToRequest(c));
      else this.form.update(f => f ? { ...f, concurrencyStamp: c.concurrencyStamp } : f);
    } catch (e) { this.ui.error(e); }
  }

  selectTab(key: string) {
    switch (key) {
      case 'users': this.api.companyUsers(this.id()).subscribe({ next: u => this.users.set(u), error: e => this.ui.error(e) }); break;
      case 'sessions': this.loadSessions(); break;
      case 'history': this.api.companyHistory(this.id()).subscribe({ next: h => this.history.set(h), error: e => this.ui.error(e) }); break;
    }
  }

  loadSessions() { this.api.companySessions(this.id()).subscribe({ next: s => this.sessions.set(s), error: e => this.ui.error(e) }); }
  resetForm() { const c = this.company(); if (c) { this.form.set(companyToRequest(c)); this.formErrors.set([]); } }

  async save() {
    const f = this.form();
    if (!f) return;
    this.busy.set(true); this.formErrors.set([]);
    try {
      await firstValueFrom(this.api.updateCompany(this.id(), cleanCompany(f)));
      this.ui.ok('Company saved');
      this.lookups.invalidate('companies');
      await this.load(true);
    } catch (e) {
      const l = errorList(e);
      if (l.length) this.formErrors.set(l);
      else if (isConflict(e)) this.formErrors.set(['Someone else changed this company. Discard your changes (or reload) and try again.']);
      else this.ui.error(e);
    } finally { this.busy.set(false); }
  }

  private async afterDialog(ref: { afterClosed(): import('rxjs').Observable<unknown> }, msg: string) {
    const r = await firstValueFrom(ref.afterClosed());
    if (r) { this.ui.ok(msg); this.lookups.invalidate('companies'); await this.load(); }
  }

  async assign() {
    await this.lookups.ensure(['failoverPolicies', 'pools']);
    await this.afterDialog(this.dialog.open(AssignCompanyDialog, { data: this.company(), width: '900px', maxWidth: '96vw' }), 'Assignment saved');
  }
  assignPool() { this.afterDialog(this.dialog.open(SharedPoolDialog, { data: this.company(), width: '760px', maxWidth: '96vw' }), 'Shared pool assigned'); }
  transfer() { this.afterDialog(this.dialog.open(TransferDialog, { data: this.company(), width: '860px', maxWidth: '96vw' }), 'Transfer applied'); }

  async setStatus() {
    const r = await firstValueFrom(this.dialog.open(CompanyStatusDialog, { data: this.company(), width: '460px' }).afterClosed());
    if (r) { this.ui.ok(`Company is now ${r.status}${r.revokedSessions ? `; ${r.revokedSessions} session(s) revoked` : ''}`); await this.load(true); }
  }

  async end(assignmentId: string, what: string) {
    const ok = await this.ui.confirm({ title: `End ${what}`, message: 'The assignment ends now (a scheduled one is cancelled). Routing falls back to the next applicable rule.', confirmText: 'End assignment', danger: true, reason: true });
    if (!ok) return;
    if (await this.ui.run(this.api.endCompanyAssignment(this.id(), assignmentId, ok.reason), 'Assignment ended') !== undefined) this.load();
  }

  async grant() {
    if (await this.ui.run(this.api.authorizeServer(this.id(), this.grantServer, this.grantNotes.trim()), 'Server authorised') !== undefined) {
      this.grantServer = ''; this.grantNotes = ''; this.load();
    }
  }

  async revokeGrant(serverId: string, name: string) {
    const ok = await this.ui.confirm({ title: 'Revoke data authorisation', message: `${name} will no longer be allowed to hold this company's data. Users assigned to it must be moved first.`, confirmText: 'Revoke', danger: true, reason: true });
    if (!ok) return;
    if (await this.ui.run(this.api.revokeAuthorization(this.id(), serverId, ok.reason), 'Authorisation revoked') !== undefined) this.load();
  }

  async revoke(s: CompanySession) {
    const ok = await this.ui.confirm({ title: 'Revoke session', message: `Sign ${s.username} out of this session?`, confirmText: 'Revoke', danger: true, reason: true });
    if (!ok) return;
    if (await this.ui.run(this.api.revokeSession(s.sessionKey, ok.reason), 'Session revoked') !== undefined) this.loadSessions();
  }

  openUser(u: CompanyUser) { this.router.navigate(['/users'], { queryParams: { companyId: this.id(), search: u.username, open: u.userId } }); }
}
