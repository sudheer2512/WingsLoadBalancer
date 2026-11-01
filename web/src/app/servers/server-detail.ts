import { DatePipe, JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
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
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import {
  AuditRow, ConnectivityResult, HealthCurrent, HealthHistoryRow, Server, ServerAssignments, ServerMetric, ServerRequest, ServerSession,
} from '../core/models';
import { MonitoringService } from '../core/monitoring.service';
import { LookupService, UiService } from '../core/ui.service';
import { ChartSeries, TimeChart } from '../shared/charts';
import { SecretDialog } from '../shared/dialogs';
import { AgoPipe, Meter, NumPipe, StatusChip } from '../shared/display';
import { AuditTable } from '../shared/audit-table';
import { ServerForm, cleanServer, serverToRequest } from './server-form';

@Component({
  selector: 'app-server-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tabs, DatePipe, JsonPipe, FormsModule, RouterLink, MatButtonModule, MatButtonToggleModule, MatIconModule, MatMenuModule,
    MatTooltipModule, MatProgressBarModule, MatDividerModule, MatFormFieldModule, MatInputModule, MatSelectModule, StatusChip, Meter,
    NumPipe, AgoPipe, TimeChart, ServerForm, AuditTable],
  template: `
    <div class="page">
      <div class="page-head">
        <a class="crumb" routerLink="/servers">Servers</a><span class="muted">/</span>
        <h1>{{ server()?.name ?? '…' }}</h1>
        @if (server(); as s) {
          <app-status [value]="s.status" /><app-status [value]="s.health" [title]="s.lastReason" />
          <span class="tag">{{ s.environment }}</span><span class="tag">{{ s.serverType }}</span>
          @if (s.awaitingManualRestore) { <app-status value="Warning" label="Awaiting manual restore" /> }
        }
        <span class="spacer"></span>
        @if (server(); as s) {
          @if (auth.can(P.serversOperate)) {
            <button mat-stroked-button (click)="test()" [disabled]="busy()"><mat-icon>network_check</mat-icon>Test connectivity</button>
            @if (s.adminState !== 'Retired') {
              <button mat-flat-button [matMenuTriggerFor]="ops" [disabled]="busy()"><mat-icon>settings_power</mat-icon>Operate</button>
            }
          }
          @if (auth.can(P.serversWrite) || auth.can(P.secretsExport)) {
            <button mat-icon-button [matMenuTriggerFor]="more" aria-label="More actions"><mat-icon>more_vert</mat-icon></button>
          }
          <mat-menu #ops>
            <button mat-menu-item (click)="act('enable')" [disabled]="s.adminState === 'Active'"><mat-icon>play_circle</mat-icon>Enable</button>
            <button mat-menu-item (click)="drain()" [disabled]="s.adminState === 'Draining'"><mat-icon>hourglass_bottom</mat-icon>Drain…</button>
            <button mat-menu-item (click)="act('maintenance')" [disabled]="s.adminState === 'Maintenance'"><mat-icon>build</mat-icon>Maintenance</button>
            <button mat-menu-item (click)="act('restore')"><mat-icon>restore</mat-icon>Restore to service</button>
            <button mat-menu-item (click)="act('disable')" [disabled]="s.adminState === 'Disabled'"><mat-icon>block</mat-icon>Disable</button>
          </mat-menu>
          <mat-menu #more>
            @if (auth.can(P.secretsExport)) { <button mat-menu-item (click)="backendConfig()"><mat-icon>key</mat-icon>Backend configuration…</button> }
            @if (auth.can(P.serversWrite)) {
              @if (s.adminState !== 'Retired') { <button mat-menu-item (click)="retire()"><mat-icon>archive</mat-icon>Retire…</button> }
              <button mat-menu-item (click)="remove()"><mat-icon class="error">delete</mat-icon>Delete…</button>
            }
          </mat-menu>
        }
      </div>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (blockers().length) {
        <div class="callout bad"><strong>{{ blockerTitle() }}</strong><ul>@for (b of blockers(); track b) { <li>{{ b }}</li> }</ul>
          <button mat-button (click)="blockers.set([])">Dismiss</button></div>
      }
      @if (testResult(); as t) {
        <div class="callout" [class.good]="t.success" [class.bad]="!t.success">
          <strong>Connectivity test {{ t.success ? 'passed' : 'failed' }}</strong>
          @if (t.responseTimeMs != null) { — {{ t.responseTimeMs | n: 1 : ' ms' }} }
          @if (t.failure) { <div>{{ t.failure }}</div> }
          @if (t.metrics) { <details><summary>Reported metrics</summary><pre class="mono">{{ t.metrics | json }}</pre></details> }
          <button mat-button (click)="testResult.set(null)">Dismiss</button>
        </div>
      }

      @if (server(); as s) {
        <app-tabs [tabs]="tabs()" [(active)]="tab" (changed)="selectTab($event)">
          @switch (tab()) {
          @case ('overview') {
            <div class="overview">
              <div class="panel">
                <h2>Live status</h2>
                <dl class="kv">
                  <dt>Status</dt><dd><app-status [value]="s.status" /> · admin state {{ s.adminState }}</dd>
                  <dt>Health</dt><dd><app-status [value]="s.health" /> {{ s.lastReason }}</dd>
                  <dt>Last check</dt><dd>{{ s.lastCheckAt | date: 'medium' }} ({{ s.lastCheckAt | ago }})</dd>
                  <dt>CPU</dt><dd><app-meter [value]="s.cpuPercent" label="CPU" /></dd>
                  <dt>Memory</dt><dd><app-meter [value]="s.memoryPercent" label="Memory" /></dd>
                  <dt>Response time</dt><dd>{{ s.responseTimeMs | n: 1 : ' ms' }}</dd>
                  <dt>Active requests</dt><dd>{{ s.activeRequests | n }}</dd>
                  <dt>Active sessions</dt><dd>{{ s.activeSessions | n }} of {{ s.maxCapacity || 'unlimited' }}</dd>
                  <dt>Error rate</dt><dd>{{ s.errorRatePercent | n: 2 : '%' }}</dd>
                  <dt>Reported version</dt><dd>{{ s.reportedVersion ?? '—' }}</dd>
                  @if (s.drainStartedAt) { <dt>Drain started</dt><dd>{{ s.drainStartedAt | date: 'medium' }} (timeout {{ s.drainTimeoutMinutes }} min)</dd> }
                  <dt>Pools</dt><dd>@for (p of s.pools; track p) { <span class="tag">{{ p }}</span> } @empty { — }</dd>
                  <dt>Modified</dt><dd>{{ s.modifiedAt | date: 'medium' }} by {{ s.modifiedBy ?? '—' }}</dd>
                  @if (s.retiredAt) { <dt>Retired</dt><dd>{{ s.retiredAt | date: 'medium' }}</dd> }
                </dl>
              </div>
              <div class="panel">
                <div class="panel-head"><h2>Configuration</h2><span class="spacer"></span>
                  @if (canEdit()) { <button mat-button (click)="resetForm()" [disabled]="busy()">Discard changes</button>
                    <button mat-flat-button (click)="save()" [disabled]="busy()"><mat-icon>save</mat-icon>Save</button> }
                </div>
                @if (formErrors().length) { <div class="callout bad"><ul>@for (e of formErrors(); track e) { <li>{{ e }}</li> }</ul></div> }
                @if (form(); as f) { <app-server-form [model]="f" [readonly]="!canEdit()" /> }
              </div>
            </div>
          }

          @case ('assignments') {
            @if (assignments(); as a) {
              <div class="cards-2">
                <div class="panel"><h2>Company assignments</h2>
                  <table class="data"><thead><tr><th>Company</th><th>Role</th><th>Environment</th><th>From</th><th>To</th></tr></thead>
                    <tbody>@for (r of a.company; track r.id) {
                      <tr><td><a [routerLink]="['/companies', r.companyId]">{{ r.company }}</a></td><td>{{ r.role }}</td><td>{{ r.environment }}</td>
                        <td>{{ r.effectiveFrom | date: 'short' }}</td><td>{{ (r.effectiveTo | date: 'short') ?? 'open' }}</td></tr>
                    } @empty { <tr><td colspan="5" class="empty">No company uses this server.</td></tr> }</tbody></table>
                </div>
                <div class="panel"><h2>User-level assignments</h2>
                  <table class="data"><thead><tr><th>Company</th><th>User</th><th>Role</th><th>Environment</th><th>From</th></tr></thead>
                    <tbody>@for (r of a.users; track r.id) {
                      <tr><td><a [routerLink]="['/companies', r.companyId]">{{ r.company }}</a></td>
                        <td><a [routerLink]="['/users']" [queryParams]="{ companyId: r.companyId, search: r.username }">{{ r.username }}</a></td>
                        <td>{{ r.role }}</td><td>{{ r.environment }}</td><td>{{ r.effectiveFrom | date: 'short' }}</td></tr>
                    } @empty { <tr><td colspan="5" class="empty">No user-level assignments.</td></tr> }</tbody></table>
                </div>
                <div class="panel"><h2>Pool memberships</h2>
                  <table class="data"><thead><tr><th>Pool</th><th class="num">Priority</th><th class="num">Weight override</th><th>New sessions</th></tr></thead>
                    <tbody>@for (r of a.pools; track r.poolId) {
                      <tr><td><a routerLink="/pools">{{ r.pool }}</a></td><td class="num">{{ r.priority }}</td><td class="num">{{ r.weightOverride ?? '—' }}</td>
                        <td><app-status [value]="r.acceptingNewSessions ? 'Active' : 'Disabled'" [label]="r.acceptingNewSessions ? 'Accepting' : 'Paused'" /></td></tr>
                    } @empty { <tr><td colspan="4" class="empty">Not a member of any pool.</td></tr> }</tbody></table>
                </div>
                <div class="panel"><h2>Data authorisations</h2>
                  <p class="muted small">Companies explicitly allowed to store or serve data on this server.</p>
                  <table class="data"><thead><tr><th>Company</th><th>Granted</th><th>By</th></tr></thead>
                    <tbody>@for (r of a.authorizations; track r.id) {
                      <tr><td><a [routerLink]="['/companies', r.companyId]">{{ r.company }}</a></td><td>{{ r.grantedAt | date: 'short' }}</td><td>{{ r.grantedBy }}</td></tr>
                    } @empty { <tr><td colspan="3" class="empty">No explicit grants.</td></tr> }</tbody></table>
                </div>
                <div class="panel"><div class="panel-head"><h2>Capacity reservations</h2></div>
                  <p class="muted small">Sessions held back for a company on this server. Set to 0 to remove a reservation.</p>
                  <table class="data"><thead><tr><th>Company</th><th class="num">Reserved sessions</th><th></th></tr></thead>
                    <tbody>@for (r of a.reservations; track r.id) {
                      <tr><td>{{ r.company }}</td><td class="num">{{ r.reservedSessions }}</td>
                        <td class="num">@if (auth.can(P.serversWrite)) { <button mat-button (click)="editReservation(r.companyId, r.reservedSessions)">Edit</button> }</td></tr>
                    } @empty { <tr><td colspan="3" class="empty">No reservations.</td></tr> }</tbody></table>
                  @if (auth.can(P.serversWrite)) {
                    <div class="row add-res">
                      <mat-form-field><mat-label>Company</mat-label>
                        <mat-select [(ngModel)]="resCompany">@for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }}</mat-option> }</mat-select></mat-form-field>
                      <mat-form-field class="narrow"><mat-label>Sessions</mat-label><input matInput type="number" min="0" [(ngModel)]="resSessions" /></mat-form-field>
                      <button mat-stroked-button [disabled]="!resCompany || resSessions == null" (click)="saveReservation()">Set reservation</button>
                    </div>
                  }
                </div>
              </div>
            } @else { <div class="empty">Loading…</div> }
          }

          @case ('sessions') {
            <div class="panel">
              <div class="panel-head"><h2>Active sessions</h2><span class="muted small">Seen in the last 30 minutes</span><span class="spacer"></span>
                <button mat-button (click)="loadSessions()"><mat-icon>refresh</mat-icon>Refresh</button></div>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Company</th><th>User</th><th>Application</th><th>Started</th><th>Last seen</th><th>Gateway</th><th>Client</th><th></th></tr></thead>
                <tbody>@for (x of sessions(); track x.sessionKey + x.applicationId) {
                  <tr><td>{{ x.company }}</td><td>{{ x.username }}</td><td>{{ lookups.appCode(x.applicationId) }}</td><td>{{ x.startedAt | date: 'short' }}</td>
                    <td>{{ x.lastSeenAt | ago }}</td><td>{{ x.gatewayInstance }}</td><td class="mono">{{ x.clientAddress }}</td>
                    <td class="num">@if (auth.can(P.sessionsRevoke)) { <button mat-button class="danger" (click)="revoke(x)">Revoke</button> }</td></tr>
                } @empty { <tr><td colspan="8" class="empty">No active sessions on this server.</td></tr> }</tbody>
              </table></div>
            </div>
          }

          @case ('performance') {
            <div class="panel">
              <div class="panel-head"><h2>Performance</h2><span class="spacer"></span>
                <mat-button-toggle-group [ngModel]="hours()" (ngModelChange)="hours.set($event); loadMetrics()" hideSingleSelectionIndicator aria-label="Window">
                  @for (h of [1, 6, 24, 72, 168]; track h) { <mat-button-toggle [value]="h">{{ h < 48 ? h + ' h' : h / 24 + ' d' }}</mat-button-toggle> }
                </mat-button-toggle-group></div>
              <div class="cards-2">
                <div><h3>CPU and memory</h3><app-time-chart [times]="mTimes()" [series]="cpuMem()" unit="%" yLabel="Utilisation (%)" [yMax]="100" /></div>
                <div><h3>Health-check response time</h3><app-time-chart kind="area" [times]="mTimes()" [series]="resp()" unit="ms" yLabel="Response (ms)" /></div>
                <div><h3>Active requests and sessions</h3><app-time-chart [times]="mTimes()" [series]="load_()" yLabel="Count" /></div>
                <div><h3>Error rate</h3><app-time-chart [times]="mTimes()" [series]="errors()" unit="%" yLabel="Errors (%)" /></div>
              </div>
            </div>
          }

          @case ('health') {
            <div class="panel">
              <div class="panel-head"><h2>Health</h2><span class="spacer"></span>
                <mat-form-field class="narrow"><mat-label>Days</mat-label>
                  <mat-select [ngModel]="days()" (ngModelChange)="days.set($event); loadHealth()">@for (d of [1, 7, 30, 90]; track d) { <mat-option [value]="d">{{ d }}</mat-option> }</mat-select></mat-form-field></div>
              @if (healthCurrent(); as h) {
                <dl class="kv">
                  <dt>Current</dt><dd><app-status [value]="h.state" /> since {{ h.stateChangedAt | date: 'medium' }}</dd>
                  <dt>Last reason</dt><dd>{{ h.lastReason }}</dd>
                  <dt>Consecutive failures</dt><dd>{{ h.consecutiveFailures }}</dd>
                  <dt>Database</dt><dd>{{ h.databaseHealthy == null ? '—' : h.databaseHealthy ? 'Healthy' : 'Unhealthy' }}</dd>
                  <dt>Disk</dt><dd>{{ h.diskPercent | n: 0 : '%' }}</dd>
                  <dt>Connections / queue</dt><dd>{{ h.activeConnections | n }} / {{ h.queueLength | n }}</dd>
                </dl>
              }
              <h3 class="gap">Transitions</h3>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Time</th><th>From</th><th>To</th><th>Source</th><th>Reason</th><th>Reported by</th></tr></thead>
                <tbody>@for (r of healthHistory(); track r.id) {
                  <tr><td>{{ r.at | date: 'medium' }}</td><td><app-status [value]="r.fromState" /></td><td><app-status [value]="r.toState" /></td>
                    <td>{{ r.source }}</td><td>{{ r.reason }}</td><td class="muted small">{{ r.reportedBy }}</td></tr>
                } @empty { <tr><td colspan="6" class="empty">No transitions in this period.</td></tr> }</tbody>
              </table></div>
            </div>
          }

          @case ('audit') {
            <div class="panel"><app-audit-table [rows]="history()" /></div>
          }
        }
        </app-tabs>
      }
    </div>`,
  styles: `
    .overview { display: grid; grid-template-columns: minmax(280px, 1fr) minmax(320px, 2fr); gap: 12px; }
    @media (max-width: 1000px) { .overview { grid-template-columns: 1fr; } }
    .narrow { width: 110px; } .add-res { margin-top: 8px; flex-wrap: wrap; }
    .gap { margin-top: 12px; margin-bottom: 4px; }
    pre { max-height: 200px; overflow: auto; }
  `,
})
export class ServerDetail implements OnInit {
  readonly P = P;
  id = input.required<string>();
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  private monitoring = inject(MonitoringService);
  lookups = inject(LookupService);
  auth = inject(AuthService);

  server = signal<Server | null>(null);
  form = signal<ServerRequest | null>(null);
  formErrors = signal<string[]>([]);
  busy = signal(false);
  tab = signal('overview');
  tabs = computed<TabDef[]>(() => [
    { key: 'overview', label: 'Overview' }, { key: 'assignments', label: 'Assignments' }, { key: 'sessions', label: 'Active sessions' },
    { key: 'performance', label: 'Performance' }, { key: 'health', label: 'Health history' },
    ...(this.auth.can(P.auditRead) ? [{ key: 'audit', label: 'Audit history' }] : []),
  ]);
  blockers = signal<string[]>([]);
  blockerTitle = signal('');
  testResult = signal<ConnectivityResult | null>(null);
  assignments = signal<ServerAssignments | null>(null);
  sessions = signal<ServerSession[]>([]);
  metrics = signal<ServerMetric[]>([]);
  hours = signal(24);
  days = signal(7);
  healthCurrent = signal<HealthCurrent | null>(null);
  healthHistory = signal<HealthHistoryRow[]>([]);
  history = signal<AuditRow[]>([]);
  resCompany = '';
  resSessions: number | null = null;
  canEdit = computed(() => this.auth.can(P.serversWrite) && this.server()?.adminState !== 'Retired');

  constructor() {
    this.monitoring.health$.pipe(takeUntilDestroyed()).subscribe(rows => {
      if (rows.some(r => r.serverId === this.id())) this.loadServer(false);
    });
  }

  ngOnInit() {
    this.loadServer(true);
    this.lookups.ensure(['applications', 'companies']);
  }

  async loadServer(resetForm: boolean) {
    try {
      const s = await firstValueFrom(this.api.server(this.id()));
      this.server.set(s);
      if (resetForm || !this.form()) this.form.set(serverToRequest(s));
    } catch (e) { this.ui.error(e); }
  }

  resetForm() { const s = this.server(); if (s) { this.form.set(serverToRequest(s)); this.formErrors.set([]); } }

  selectTab(key: string) {
    switch (key) {
      case 'assignments': if (this.auth.can(P.assignmentsRead)) this.loadAssignments(); break;
      case 'sessions': this.loadSessions(); break;
      case 'performance': this.loadMetrics(); break;
      case 'health': this.loadHealth(); break;
      case 'audit': this.loadHistory(); break;
    }
  }

  loadAssignments() { this.api.serverAssignments(this.id()).subscribe({ next: a => this.assignments.set(a), error: e => this.ui.error(e) }); }
  loadSessions() { if (this.auth.can(P.sessionsRead)) this.api.serverSessions(this.id()).subscribe({ next: s => this.sessions.set(s), error: e => this.ui.error(e) }); }
  loadMetrics() { this.api.serverMetrics(this.id(), this.hours()).subscribe({ next: m => this.metrics.set(m), error: e => this.ui.error(e) }); }
  loadHealth() {
    this.api.serverHealth(this.id(), this.days()).subscribe({ next: h => { this.healthCurrent.set(h.current); this.healthHistory.set(h.history); }, error: e => this.ui.error(e) });
  }
  loadHistory() { this.api.serverHistory(this.id()).subscribe({ next: h => this.history.set(h), error: e => this.ui.error(e) }); }

  mTimes = computed(() => this.metrics().map(m => new Date(m.capturedAt).getTime()));
  cpuMem = computed<ChartSeries[]>(() => [
    { name: 'CPU', values: this.metrics().map(m => m.cpuPercent), slot: 1 },
    { name: 'Memory', values: this.metrics().map(m => m.memoryPercent), slot: 2 },
  ]);
  resp = computed<ChartSeries[]>(() => [{ name: 'Response time', values: this.metrics().map(m => m.responseTimeMs), slot: 3 }]);
  load_ = computed<ChartSeries[]>(() => [
    { name: 'Active requests', values: this.metrics().map(m => m.activeRequests), slot: 1 },
    { name: 'Active sessions', values: this.metrics().map(m => m.activeSessions), slot: 3 },
  ]);
  errors = computed<ChartSeries[]>(() => [{ name: 'Error rate', values: this.metrics().map(m => m.errorRatePercent), slot: 2 }]);

  async save() {
    const f = this.form();
    if (!f) return;
    this.busy.set(true);
    this.formErrors.set([]);
    try {
      await firstValueFrom(this.api.updateServer(this.id(), cleanServer(f)));
      this.ui.ok('Server saved');
      this.lookups.invalidate('servers');
      await this.loadServer(true);
    } catch (e) {
      const list = errorList(e);
      if (list.length) this.formErrors.set(list); else this.ui.error(e);
    } finally { this.busy.set(false); }
  }

  async act(action: 'enable' | 'disable' | 'maintenance' | 'restore') {
    const s = this.server()!;
    const text: Record<string, [string, string]> = {
      enable: ['Enable server', `Return ${s.name} to Active so it accepts new sessions.`],
      disable: ['Disable server', `${s.name} will stop receiving traffic immediately. Existing sessions fail over according to policy.`],
      maintenance: ['Put into maintenance', `${s.name} stops accepting traffic for maintenance.`],
      restore: ['Restore to service', `Return ${s.name} to Active and clear any manual-restore hold.`],
    };
    const ok = await this.ui.confirm({ title: text[action][0], message: text[action][1], confirmText: text[action][0], reason: true, danger: action === 'disable' });
    if (!ok) return;
    await this.runAction(() => this.api.serverAction(s.id, action, ok.reason), `${s.name}: ${action} done`);
  }

  async drain() {
    const s = this.server()!;
    const r = await firstValueFrom(this.dialog.open(DrainDialog, { data: { name: s.name, timeout: s.drainTimeoutMinutes }, width: '440px' }).afterClosed());
    if (!r) return;
    await this.runAction(() => this.api.drainServer(s.id, r.timeout, r.reason), `${s.name} is draining`);
  }

  async retire() {
    const s = this.server()!;
    const ok = await this.ui.confirm({ title: 'Retire server', message: `Retire ${s.name}? Retired servers can never return to service. Active assignments and pool memberships must be migrated first.`,
      confirmText: 'Retire', danger: true, reason: true });
    if (!ok) return;
    await this.runAction(() => this.api.serverAction(s.id, 'retire', ok.reason), `${s.name} retired`, 'The server cannot be retired yet');
  }

  async remove() {
    const s = this.server()!;
    const ok = await this.ui.confirm({ title: 'Delete server', message: `Permanently delete ${s.name}? Only servers that were never used can be deleted; otherwise retire them.`,
      confirmText: 'Delete', danger: true, reason: true });
    if (!ok) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deleteServer(s.id, ok.reason));
      this.ui.ok('Server deleted');
      this.lookups.invalidate('servers');
      this.router.navigate(['/servers']);
    } catch (e) { this.showBlockers(e, 'The server cannot be deleted'); } finally { this.busy.set(false); }
  }

  private async runAction(call: () => ReturnType<ApiService['serverAction']>, success: string, blockerTitle = 'Action refused') {
    this.busy.set(true);
    this.blockers.set([]);
    try {
      await firstValueFrom(call());
      this.ui.ok(success);
      await this.loadServer(true);
    } catch (e) { this.showBlockers(e, blockerTitle); } finally { this.busy.set(false); }
  }

  private showBlockers(e: unknown, title: string) {
    const list = errorList(e);
    if (!list.length) { this.ui.error(e); return; }
    const msg = (e as { error?: { message?: string } }).error?.message;
    this.blockerTitle.set(msg ? `${title}: ${msg}` : title);
    this.blockers.set(list);
  }

  async test() {
    this.busy.set(true);
    this.testResult.set(null);
    try { this.testResult.set(await firstValueFrom(this.api.testServer(this.id()))); }
    catch (e) { this.testResult.set({ success: false, failure: errorMessage(e) }); }
    finally { this.busy.set(false); }
  }

  async backendConfig() {
    const ok = await this.ui.confirm({ title: 'Export backend configuration',
      message: 'This reveals the signing and health keys this application server needs. The export is recorded in the audit log. Continue?', confirmText: 'Show keys' });
    if (!ok) return;
    try {
      const c = await firstValueFrom(this.api.backendConfig(this.id()));
      this.dialog.open(SecretDialog, { width: '640px', maxWidth: '95vw', data: { title: `Backend configuration · ${this.server()?.name}`,
        warning: c.note + ' Close this dialog once they are stored; they are not shown again unless exported again.',
        fields: [{ label: 'Server ID', value: c.serverId }, { label: 'Signing key (base64)', value: c.signingKey }, { label: 'Health key', value: c.healthKey }] } });
    } catch (e) { this.ui.error(e); }
  }

  async revoke(x: ServerSession) {
    const ok = await this.ui.confirm({ title: 'Revoke session', message: `Sign ${x.company}/${x.username} out of this session?`, confirmText: 'Revoke', danger: true, reason: true });
    if (!ok) return;
    if (await this.ui.run(this.api.revokeSession(x.sessionKey, ok.reason), 'Session revoked') !== undefined) this.loadSessions();
  }

  async editReservation(companyId: string, current: number) {
    const v = await this.ui.prompt({ title: 'Capacity reservation', label: 'Reserved sessions (0 removes)', value: String(current), type: 'number' });
    if (v === undefined) return;
    await this.setReservation(companyId, Number(v));
  }

  async saveReservation() {
    if (!this.resCompany || this.resSessions == null) return;
    await this.setReservation(this.resCompany, this.resSessions);
    this.resCompany = ''; this.resSessions = null;
  }

  private async setReservation(companyId: string, n: number) {
    if (!Number.isFinite(n) || n < 0) { this.ui.error(new Error('Enter a non-negative number')); return; }
    if (await this.ui.run(this.api.setReservation(this.id(), companyId, Math.round(n)), 'Reservation saved') !== undefined) this.loadAssignments();
  }
}

@Component({
  selector: 'app-drain-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  template: `
    <h2 mat-dialog-title>Drain {{ data.name }}</h2>
    <mat-dialog-content>
      <p>New sessions stop immediately. Existing sessions continue until they end or the drain timeout expires, then they are moved per failover policy.</p>
      <mat-form-field class="full"><mat-label>Drain timeout (minutes)</mat-label><input matInput type="number" min="1" max="1440" [(ngModel)]="timeout" /></mat-form-field>
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><textarea matInput rows="2" [(ngModel)]="reason" maxlength="500"></textarea></mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!timeout || timeout < 1 || timeout > 1440" (click)="ref.close({ timeout, reason: reason.trim() })">Start draining</button>
    </mat-dialog-actions>`,
})
export class DrainDialog {
  data = inject<{ name: string; timeout: number }>(MAT_DIALOG_DATA);
  ref = inject(MatDialogRef<DrainDialog>);
  timeout = this.data.timeout || 30;
  reason = '';
}
