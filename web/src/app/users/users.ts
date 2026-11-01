import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage, isConflict } from '../core/auth.service';
import { ENVIRONMENTS, PreviewResult, UserAssignment, UserAssignmentRequest, UserAssignmentView, UserRow } from '../core/models';
import { LookupService, UiService, fromLocalInput } from '../core/ui.service';
import { Grid, StatusChip } from '../shared/display';
import { PreviewTable } from '../shared/preview-table';

// ------------------------------------------------------------------------------------------------ per-user panel

@Component({
  selector: 'app-user-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule,
    MatSlideToggleModule, MatProgressBarModule, MatTooltipModule, StatusChip, PreviewTable],
  template: `
    @let u = user();
    <div class="panel-head">
      <h2>{{ u.company }} / {{ u.username }}</h2><app-status [value]="u.status" /><span class="spacer"></span>
      <button mat-icon-button (click)="closed.emit()" aria-label="Close panel"><mat-icon>close</mat-icon></button>
    </div>
    <div class="muted">{{ u.displayName }}{{ u.email ? ' · ' + u.email : '' }} · last sign-in {{ (u.lastLoginAt | date: 'short') ?? 'never' }}</div>
    <div class="wrap-row actions">
      @if (auth.can(P.companiesWrite)) {
        <button mat-stroked-button (click)="toggleMembership()"><mat-icon>{{ u.status === 'Active' ? 'person_off' : 'person' }}</mat-icon>{{ u.status === 'Active' ? 'Disable membership' : 'Enable membership' }}</button>
        <button mat-stroked-button (click)="resetPassword()"><mat-icon>lock_reset</mat-icon>Reset password</button>
      }
      @if (auth.can(P.routingSimulate)) {
        <a mat-stroked-button routerLink="/routing/simulator" [queryParams]="{ company: u.company, username: u.username }"><mat-icon>alt_route</mat-icon>Simulate</a>
      }
    </div>
    @if (busy()) { <mat-progress-bar mode="indeterminate" /> }

    <h3 class="gap">Current and scheduled user-level assignments</h3>
    @if (view(); as v) {
      <table class="data">
        <thead><tr><th>State</th><th>Scope</th><th>Primary</th><th>Secondary</th><th>Failover</th><th>From</th><th>To</th><th></th></tr></thead>
        <tbody>@for (a of v.assignments; track a.id) {
          <tr><td><app-status [value]="a.state" /></td><td>{{ a.environment }}{{ a.application ? ' · ' + a.application : '' }}</td>
            <td>{{ a.primaryServer }}</td><td>{{ a.secondaryServer ?? '—' }}</td>
            <td class="small">{{ a.failoverPolicy ?? '—' }}@if (a.allowCompanyRouteFallback) { <div class="muted">falls back to company route</div> }</td>
            <td class="nowrap">{{ a.effectiveFrom | date: 'short' }}</td><td class="nowrap">{{ (a.effectiveTo | date: 'short') ?? 'open' }}</td>
            <td>@if (auth.can(P.assignmentsWrite)) {
              <button mat-button (click)="edit(a)" matTooltip="Load into the form">Edit</button>
              @if (a.state === 'Current') { <button mat-button class="danger" (click)="removeOverride(a)">Remove override</button> }
            }</td></tr>
        } @empty { <tr><td colspan="8" class="empty">No user-level override — the user follows the company route ({{ u.routingType }}{{ u.assignedServer ? ': ' + u.assignedServer : '' }}).</td></tr> }</tbody>
      </table>
    }

    @if (auth.can(P.assignmentsWrite) && view(); as v) {
      <h3 class="gap">Assign dedicated server</h3>
      <div class="callout">Only servers authorised for {{ u.company }}'s data can be selected ({{ v.authorizedServerIds.length }} authorised). Other servers are listed but disabled:
        tenant isolation requires a data authorisation on the company before a user can be placed there. Grant one on the company's <a [routerLink]="['/companies', u.companyId]">Data authorisations</a> tab.</div>
      <div class="grid-2">
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment" (ngModelChange)="dirty()">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="f.applicationId" (ngModelChange)="dirty()"><mat-option [value]="null">All applications</mat-option>
            @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Dedicated (primary) server</mat-label>
          <mat-select [(ngModel)]="f.primaryServerId" (ngModelChange)="dirty()">
            @for (s of serverOptions(); track s.id) {
              <mat-option [value]="s.id" [disabled]="!s.authorized">{{ s.name }} · {{ s.environment }} · {{ s.health }}{{ s.authorized ? '' : ' — not authorised' }}</mat-option>
            }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Secondary server</mat-label>
          <mat-select [(ngModel)]="f.secondaryServerId" (ngModelChange)="dirty()"><mat-option [value]="null">None</mat-option>
            @for (s of serverOptions(); track s.id) {
              <mat-option [value]="s.id" [disabled]="!s.authorized || s.id === f.primaryServerId">{{ s.name }}{{ s.authorized ? '' : ' — not authorised' }}</mat-option>
            }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Failover policy</mat-label>
          <mat-select [(ngModel)]="f.failoverPolicyId" (ngModelChange)="dirty()"><mat-option [value]="null">None</mat-option>
            @for (p of lookups.failoverPolicies(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
        <div class="toggles">
          <mat-slide-toggle [(ngModel)]="f.isEnabled" (ngModelChange)="dirty()">Assignment enabled</mat-slide-toggle>
          <mat-checkbox [(ngModel)]="f.allowCompanyRouteFallback" (ngModelChange)="dirty()">Fall back to the company route if unavailable</mat-checkbox>
        </div>
        <mat-form-field><mat-label>Effective from</mat-label><input matInput type="datetime-local" [(ngModel)]="from" (ngModelChange)="dirty()" /><mat-hint>Empty = now; future = scheduled</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Effective to</mat-label><input matInput type="datetime-local" [(ngModel)]="to" (ngModelChange)="dirty()" /></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><input matInput [(ngModel)]="f.reason" maxlength="500" /></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
      <div class="wrap-row">
        <button mat-stroked-button [disabled]="busy() || !f.primaryServerId" (click)="preview()"><mat-icon>preview</mat-icon>Preview</button>
        <button mat-flat-button [disabled]="busy() || !f.primaryServerId" (click)="save()">{{ from ? 'Schedule assignment' : 'Assign now' }}</button>
      </div>
      @if (previewResult()) { <h3 class="gap">Preview</h3><app-preview-table [result]="previewResult()" /> }
    }

    <h3 class="gap">History</h3>
    <div class="table-wrap hist"><table class="data">
      <thead><tr><th>State</th><th>Scope</th><th>Primary</th><th>Secondary</th><th>From</th><th>To</th><th>By</th><th>Reason</th><th></th></tr></thead>
      <tbody>@for (a of history(); track a.id) {
        <tr><td><app-status [value]="a.state" /></td><td>{{ a.environment }}{{ a.application ? ' · ' + a.application : '' }}</td><td>{{ a.primaryServer }}</td>
          <td>{{ a.secondaryServer ?? '—' }}</td><td class="nowrap">{{ a.effectiveFrom | date: 'short' }}</td><td class="nowrap">{{ (a.effectiveTo | date: 'short') ?? 'open' }}</td>
          <td class="small">{{ a.createdBy }}</td><td class="small">{{ a.reason }}</td>
          <td>@if (auth.can(P.assignmentsWrite) && a.state !== 'Current') { <button mat-button (click)="rollback(a)">Rollback</button> }</td></tr>
      } @empty { <tr><td colspan="9" class="empty">No assignment history.</td></tr> }</tbody>
    </table></div>`,
  styles: `
    .actions { margin: 8px 0; } .gap { margin: 14px 0 6px; } .nowrap { white-space: nowrap; }
    .toggles { display: flex; flex-direction: column; gap: 4px; justify-content: center; }
    .hist { max-height: 320px; }
  `,
})
export class UserPanel {
  readonly P = P;
  user = input.required<UserRow>();
  closed = output<void>();
  changed = output<void>();
  private api = inject(ApiService);
  private ui = inject(UiService);
  lookups = inject(LookupService);
  auth = inject(AuthService);
  envs = ENVIRONMENTS;
  view = signal<UserAssignmentView | null>(null);
  history = signal<UserAssignment[]>([]);
  previewResult = signal<PreviewResult | null>(null);
  errors = signal<string[]>([]);
  busy = signal(false);
  f: UserAssignmentRequest = this.blank('');
  from = ''; to = '';
  private env = signal('Production');

  serverOptions = computed(() => {
    const auth = new Set(this.view()?.authorizedServerIds ?? []);
    return this.lookups.servers().filter(s => s.environment === this.env() && s.adminState !== 'Retired')
      .map(s => ({ id: s.id, name: s.name, environment: s.environment, health: s.health, authorized: auth.has(s.id) }))
      .sort((a, b) => Number(b.authorized) - Number(a.authorized) || a.name.localeCompare(b.name));
  });

  constructor() {
    this.lookups.ensure(['servers', 'applications', 'failoverPolicies']);
    effect(() => { const u = this.user(); this.reset(u.companyId); this.load(); });
  }

  private blank(companyId: string): UserAssignmentRequest {
    return { companyId, primaryServerId: '', secondaryServerId: null, failoverPolicyId: null, applicationId: null, environment: 'Production',
      allowCompanyRouteFallback: false, isEnabled: true, reason: '' };
  }

  private reset(companyId: string) {
    this.f = this.blank(companyId); this.from = ''; this.to = ''; this.env.set('Production');
    this.previewResult.set(null); this.errors.set([]);
  }

  async load() {
    const u = this.user();
    try {
      const [v, h] = await Promise.all([
        firstValueFrom(this.api.userAssignment(u.userId, u.companyId)),
        firstValueFrom(this.api.userAssignmentHistory(u.userId, u.companyId)),
      ]);
      this.view.set(v);
      this.history.set(h);
    } catch (e) { this.ui.error(e); }
  }

  dirty() { this.env.set(this.f.environment); this.previewResult.set(null); }

  edit(a: UserAssignment) {
    this.f = { companyId: this.user().companyId, primaryServerId: a.primaryServerId, secondaryServerId: a.secondaryServerId ?? null,
      failoverPolicyId: a.failoverPolicyId ?? null, applicationId: a.applicationId ?? null, environment: a.environment,
      allowCompanyRouteFallback: a.allowCompanyRouteFallback, isEnabled: a.isEnabled, reason: '' };
    this.from = ''; this.to = '';
    this.dirty();
  }

  private body(): UserAssignmentRequest {
    return { ...this.f, companyId: this.user().companyId, effectiveFrom: fromLocalInput(this.from), effectiveTo: fromLocalInput(this.to),
      reason: this.f.reason?.trim() || null, expectedStamp: this.view()?.membershipStamp };
  }

  async preview() {
    this.busy.set(true); this.errors.set([]);
    try { this.previewResult.set(await firstValueFrom(this.api.previewUserAssignment(this.user().userId, this.body()))); }
    catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  async save() {
    this.busy.set(true); this.errors.set([]);
    try {
      await firstValueFrom(this.api.setUserAssignment(this.user().userId, this.body()));
      this.ui.ok(this.from ? 'Assignment scheduled' : 'Assignment saved');
      this.reset(this.user().companyId);
      await this.load();
      this.changed.emit();
    } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  private fail(e: unknown) {
    const l = errorList(e);
    this.errors.set(l.length ? l : [isConflict(e) ? `${errorMessage(e)} The panel has been reloaded with the latest data.` : errorMessage(e)]);
    if (isConflict(e)) this.load();
  }

  async removeOverride(a: UserAssignment) {
    const ok = await this.ui.confirm({ title: 'Remove user override', message: `Remove the ${a.environment}${a.application ? ' / ' + a.application : ''} override? The user returns to the company route.`,
      confirmText: 'Remove override', danger: true, reason: true });
    if (!ok) return;
    const u = this.user();
    const r = await this.ui.run(this.api.removeUserAssignment(u.userId, { companyId: u.companyId, environment: a.environment, applicationId: a.applicationId,
      expectedStamp: this.view()?.membershipStamp, reason: ok.reason }), 'Override removed');
    await this.load();
    if (r !== undefined) this.changed.emit();
  }

  async rollback(a: UserAssignment) {
    const ok = await this.ui.confirm({ title: 'Roll back assignment', message: `Restore the assignment to ${a.primaryServer}${a.secondaryServer ? ' / ' + a.secondaryServer : ''} (${a.environment}) from ${new Date(a.effectiveFrom).toLocaleString()}? It becomes the current assignment now.`,
      confirmText: 'Roll back', reason: true });
    if (!ok) return;
    const r = await this.ui.run(this.api.rollbackUserAssignment(this.user().userId, a.id, ok.reason), 'Assignment rolled back');
    await this.load();
    if (r !== undefined) this.changed.emit();
  }

  async toggleMembership() {
    const u = this.user();
    const next = u.status === 'Active' ? 'Disabled' : 'Active';
    const ok = await this.ui.confirm({ title: `${next === 'Disabled' ? 'Disable' : 'Enable'} membership`, message: `${next === 'Disabled' ? 'Disable' : 'Enable'} ${u.username}'s access to ${u.company}?`,
      confirmText: next === 'Disabled' ? 'Disable' : 'Enable', danger: next === 'Disabled', reason: true });
    if (!ok) return;
    if (await this.ui.run(this.api.setMembershipStatus(u.userId, u.companyId, next, ok.reason), `Membership ${next.toLowerCase()}`) !== undefined) this.changed.emit();
  }

  async resetPassword() {
    const pw = await this.ui.prompt({ title: `Reset password · ${this.user().username}`, label: 'New password', type: 'password', minLength: 12,
      hint: 'At least 12 characters. Also clears any sign-in lockout. Applies to the person across all companies.', confirmText: 'Reset password' });
    if (!pw) return;
    await this.ui.run(this.api.resetUserPassword(this.user().userId, pw), 'Password reset');
  }
}

// ------------------------------------------------------------------------------------------------ create user

@Component({
  selector: 'app-create-user-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 mat-dialog-title>Create user</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Company</mat-label>
        <mat-select [(ngModel)]="companyId" required>@for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c.name }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field class="full"><mat-label>Username</mat-label><input matInput [(ngModel)]="username" required maxlength="200" autocomplete="off" />
        <mat-hint>Unique within the company.</mat-hint></mat-form-field>
      <mat-form-field class="full"><mat-label>Display name</mat-label><input matInput [(ngModel)]="displayName" required maxlength="200" /></mat-form-field>
      <mat-form-field class="full"><mat-label>E-mail</mat-label><input matInput type="email" [(ngModel)]="email" maxlength="320" /></mat-form-field>
      <mat-form-field class="full"><mat-label>Initial password (local authentication)</mat-label><input matInput type="password" [(ngModel)]="password" autocomplete="new-password" />
        <mat-hint>At least 12 characters. Leave empty for externally authenticated users.</mat-hint></mat-form-field>
      <mat-form-field class="full"><mat-label>External subject (OIDC / Wings auth)</mat-label><input matInput [(ngModel)]="externalSubject" maxlength="200" /></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !companyId || !username.trim() || !displayName.trim() || (password.length > 0 && password.length < 12)" (click)="save()">Create user</button>
    </mat-dialog-actions>`,
})
export class CreateUserDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CreateUserDialog>);
  lookups = inject(LookupService);
  companyId = inject<string>(MAT_DIALOG_DATA) || '';
  username = ''; displayName = ''; email = ''; password = ''; externalSubject = '';
  busy = signal(false);
  errors = signal<string[]>([]);
  async save() {
    this.busy.set(true); this.errors.set([]);
    try {
      const r = await firstValueFrom(this.api.createUser({ companyId: this.companyId, username: this.username.trim(), displayName: this.displayName.trim(),
        email: this.email.trim() || null, password: this.password || null, externalSubject: this.externalSubject.trim() || null }));
      this.password = '';
      this.ref.close({ id: r.id, companyId: this.companyId, username: this.username.trim() });
    } catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-users',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatTableModule, MatSortModule, MatPaginatorModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatCheckboxModule, MatButtonModule, MatIconModule, MatProgressBarModule, StatusChip, UserPanel],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>User assignments</h1><span class="muted">{{ grid.count() }} user(s)</span><span class="spacer"></span>
        @if (auth.can(P.assignmentsWrite)) { <a mat-stroked-button routerLink="/users/bulk"><mat-icon>upload_file</mat-icon>Bulk upload</a> }
        @if (auth.can(P.companiesWrite)) { <button mat-flat-button (click)="create()"><mat-icon>person_add</mat-icon>Create user</button> }
      </div>
      <div class="filters">
        <mat-form-field class="wide"><mat-label>Company</mat-label>
          <mat-select [ngModel]="companyId()" (ngModelChange)="companyId.set($event); load()"><mat-option value="">All companies</mat-option>
            @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field class="wide"><mat-label>Username or display name</mat-label><mat-icon matPrefix>search</mat-icon>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event); search$.next()" /></mat-form-field>
        <mat-checkbox [ngModel]="dedicatedOnly()" (ngModelChange)="dedicatedOnly.set($event); load()">Only users with a dedicated server</mat-checkbox>
      </div>
      <p class="muted small">The same username in different companies is a different user; each row is one company membership. Routing type shows the Production route.</p>
      @if (grid.loading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="layout" [class.with-panel]="selected()">
        <div class="list">
          <div class="table-wrap">
            <table mat-table [dataSource]="grid.view()" matSort (matSortChange)="grid.sortBy($event)" aria-label="Users">
              <ng-container matColumnDef="company"><th mat-header-cell *matHeaderCellDef mat-sort-header>Company</th>
                <td mat-cell *matCellDef="let u"><b>{{ u.company }}</b></td></ng-container>
              <ng-container matColumnDef="username"><th mat-header-cell *matHeaderCellDef mat-sort-header>Username</th>
                <td mat-cell *matCellDef="let u">{{ u.username }}<div class="muted small">{{ u.displayName }}</div></td></ng-container>
              <ng-container matColumnDef="assignedServer"><th mat-header-cell *matHeaderCellDef mat-sort-header>Assigned server</th>
                <td mat-cell *matCellDef="let u">{{ u.assignedServer ?? '—' }}@if (u.secondaryServer) { <div class="muted small">secondary {{ u.secondaryServer }}</div> }</td></ng-container>
              <ng-container matColumnDef="routingType"><th mat-header-cell *matHeaderCellDef mat-sort-header>Routing type</th>
                <td mat-cell *matCellDef="let u"><span class="rt" [attr.data-type]="u.routingType">{{ u.routingType }}</span></td></ng-container>
              <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th>
                <td mat-cell *matCellDef="let u"><app-status [value]="u.status" />@if (u.userStatus !== 'Active') { <app-status [value]="u.userStatus" label="Account disabled" /> }</td></ng-container>
              <tr mat-header-row *matHeaderRowDef="cols; sticky: true"></tr>
              <tr mat-row *matRowDef="let u; columns: cols" class="clickable" [class.sel]="selected()?.userId === u.userId && selected()?.companyId === u.companyId" (click)="select(u)"></tr>
              <tr class="mat-row" *matNoDataRow><td class="empty" [attr.colspan]="cols.length">No users match.</td></tr>
            </table>
          </div>
          <mat-paginator [length]="grid.count()" [pageIndex]="grid.pageIndex()" [pageSize]="grid.pageSize()" [pageSizeOptions]="[25, 50, 100]" (page)="grid.pageBy($event)" />
        </div>
        @if (selected(); as s) {
          <app-user-panel class="panel side" [user]="s" (closed)="selected.set(null)" (changed)="load()" />
        }
      </div>
    </div>`,
  styles: `
    .layout { display: grid; grid-template-columns: 1fr; gap: 12px; }
    .layout.with-panel { grid-template-columns: minmax(380px, 1fr) minmax(420px, 1.2fr); }
    @media (max-width: 1100px) { .layout.with-panel { grid-template-columns: 1fr; } }
    .side { align-self: start; }
    tr.sel td { background: var(--selected) !important; }
    .rt { font-size: 12px; font-weight: 600; }
    .rt[data-type='User Dedicated'] { color: var(--accent); } .rt[data-type='Unassigned'] { color: var(--bad); }
  `,
})
export class Users implements OnInit {
  readonly P = P;
  private api = inject(ApiService);
  private ui = inject(UiService);
  private route = inject(ActivatedRoute);
  private dialog = inject(MatDialog);
  lookups = inject(LookupService);
  auth = inject(AuthService);
  cols = ['company', 'username', 'assignedServer', 'routingType', 'status'];
  companyId = signal(''); search = signal(''); dedicatedOnly = signal(false);
  grid = new Grid<UserRow>(u => `${u.company} ${u.username} ${u.displayName} ${u.assignedServer ?? ''}`);
  selected = signal<UserRow | null>(null);
  search$ = new Subject<void>();
  private openId: string | null = null;

  constructor() { this.search$.pipe(debounceTime(300), takeUntilDestroyed()).subscribe(() => this.load()); }

  ngOnInit() {
    const q = this.route.snapshot.queryParamMap;
    this.companyId.set(q.get('companyId') ?? '');
    this.search.set(q.get('search') ?? '');
    this.openId = q.get('open');
    this.lookups.ensure(['companies']);
    this.load();
  }

  async load() {
    this.grid.loading.set(true);
    try {
      const rows = await firstValueFrom(this.api.users({ companyId: this.companyId(), search: this.search(), dedicatedOnly: this.dedicatedOnly() || undefined, take: 1000 }));
      this.grid.set(rows);
      const sel = this.selected();
      const keep = sel ? rows.find(r => r.userId === sel.userId && r.companyId === sel.companyId) : null;
      if (keep) this.selected.set(keep);
      if (this.openId) { const o = rows.find(r => r.userId === this.openId); if (o) this.selected.set(o); this.openId = null; }
    } catch (e) { this.ui.error(e); } finally { this.grid.loading.set(false); }
  }

  select(u: UserRow) { this.selected.set(u); }

  async create() {
    const r = await firstValueFrom(this.dialog.open(CreateUserDialog, { data: this.companyId(), width: '520px', maxWidth: '95vw' }).afterClosed());
    if (r) { this.ui.ok('User created'); this.search.set(r.username); this.companyId.set(r.companyId); this.openId = r.id; this.load(); }
  }
}
