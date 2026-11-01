import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { errorList, errorMessage } from '../core/auth.service';
import {
  CLUSTER_MODES, COMPANY_STATUSES, CompanyAssignmentRequest, CompanyDetail, CompanyStatus, CutoverMode, ENVIRONMENTS, Environment,
  PreviewResult, Server, SharedPoolAssignmentRequest, TransferCheck, TransferRequest, TransferResult,
} from '../core/models';
import { LookupService, fromLocalInput } from '../core/ui.service';
import { HumanizePipe, StatusChip } from '../shared/display';
import { PreviewTable } from '../shared/preview-table';

export function serverLabel(s: Server) { return `${s.name} · ${s.serverType} · ${s.health}${s.adminState !== 'Active' ? ' · ' + s.adminState : ''}`; }

const COMMON = [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule,
  MatIconModule, MatProgressBarModule];

function errorsOf(e: unknown) { const l = errorList(e); return l.length ? l : [errorMessage(e)]; }

// ------------------------------------------------------------------------------------------------ assign / change server

@Component({
  selector: 'app-assign-company-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...COMMON, PreviewTable, HumanizePipe],
  template: `
    <h2 mat-dialog-title>Assign or change server · {{ company.code }}</h2>
    <mat-dialog-content class="dialog-wide">
      <p class="muted">Creates a new effective-dated company assignment for the scope (environment + application). The current assignment in that scope is closed at the effective time. A future start date schedules the change.</p>
      <div class="grid-2">
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment" (ngModelChange)="changed()">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="f.applicationId" (ngModelChange)="changed()"><mat-option [value]="null">All applications</mat-option>
            @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }} · {{ a.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Primary server</mat-label>
          <mat-select [(ngModel)]="f.primaryServerId" (ngModelChange)="changed()" required>
            @for (s of servers(); track s.id) { <mat-option [value]="s.id">{{ label(s) }}</mat-option> }</mat-select>
          <mat-hint>Servers in {{ f.environment }} only.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Secondary server</mat-label>
          <mat-select [(ngModel)]="f.secondaryServerId" (ngModelChange)="changed()"><mat-option [value]="null">None</mat-option>
            @for (s of servers(); track s.id) { <mat-option [value]="s.id" [disabled]="s.id === f.primaryServerId">{{ label(s) }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Cluster pool</mat-label>
          <mat-select [(ngModel)]="f.clusterPoolId" (ngModelChange)="changed()"><mat-option [value]="null">None (dedicated)</mat-option>
            @for (p of pools(); track p.id) { <mat-option [value]="p.id">{{ p.name }} ({{ p.members.length }} members)</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Cluster mode</mat-label>
          <mat-select [(ngModel)]="f.clusterMode" (ngModelChange)="changed()" [disabled]="!f.clusterPoolId">
            @for (m of clusterModes; track m) { <mat-option [value]="m">{{ m | humanize }}</mat-option> }</mat-select>
          <mat-hint>Balanced spreads sessions over healthy cluster members.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Failover policy</mat-label>
          <mat-select [(ngModel)]="f.failoverPolicyId" (ngModelChange)="changed()"><mat-option [value]="null">None</mat-option>
            @for (p of lookups.failoverPolicies(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
        <span></span>
        <mat-form-field><mat-label>Effective from</mat-label><input matInput type="datetime-local" [(ngModel)]="from" (ngModelChange)="changed()" />
          <mat-hint>Empty = now</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Effective to</mat-label><input matInput type="datetime-local" [(ngModel)]="to" (ngModelChange)="changed()" />
          <mat-hint>Empty = open-ended</mat-hint></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><textarea matInput rows="2" [(ngModel)]="f.reason" maxlength="500"></textarea></mat-form-field>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (errors().length) { <div class="callout bad"><strong>Not saved</strong><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
      @if (preview()) { <h3>Preview: before and after for sample users</h3><app-preview-table [result]="preview()" /> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-stroked-button [disabled]="busy() || !f.primaryServerId" (click)="runPreview()"><mat-icon>preview</mat-icon>Preview</button>
      <button mat-flat-button [disabled]="busy() || !f.primaryServerId" (click)="save()">{{ from ? 'Schedule change' : 'Apply now' }}</button>
    </mat-dialog-actions>`,
})
export class AssignCompanyDialog implements OnInit {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<AssignCompanyDialog, boolean>);
  lookups = inject(LookupService);
  company = inject<CompanyDetail>(MAT_DIALOG_DATA);
  envs = ENVIRONMENTS; clusterModes = CLUSTER_MODES;
  f: CompanyAssignmentRequest = this.initial();
  from = ''; to = '';
  busy = signal(false);
  errors = signal<string[]>([]);
  preview = signal<PreviewResult | null>(null);
  private env = signal<Environment>(this.f.environment);
  servers = computed(() => this.lookups.servers().filter(s => s.environment === this.env() && s.adminState !== 'Retired'));
  pools = computed(() => this.lookups.pools().filter(p => p.environment === this.env()));
  label = serverLabel;

  ngOnInit() { this.lookups.ensure(['servers', 'pools', 'applications', 'failoverPolicies']); }

  private initial(): CompanyAssignmentRequest {
    const c = this.company;
    const cur = c.companyAssignments.find(a => a.state === 'Current' && a.environment === c.defaultEnvironment && !a.applicationId);
    return {
      environment: cur?.environment ?? c.defaultEnvironment, applicationId: null, primaryServerId: cur?.primaryServerId ?? '',
      secondaryServerId: cur?.secondaryServerId ?? null, clusterPoolId: cur?.clusterPoolId ?? null, clusterMode: cur?.clusterMode ?? 'PrimaryPreferred',
      failoverPolicyId: cur?.failoverPolicyId ?? null, reason: '', expectedStamp: c.concurrencyStamp,
    };
  }

  changed() { this.env.set(this.f.environment); this.preview.set(null); }
  private body(): CompanyAssignmentRequest {
    return { ...this.f, effectiveFrom: fromLocalInput(this.from), effectiveTo: fromLocalInput(this.to), reason: this.f.reason?.trim() || null,
      clusterMode: this.f.clusterPoolId ? this.f.clusterMode : 'PrimaryPreferred' };
  }

  async runPreview() {
    this.busy.set(true); this.errors.set([]);
    try { this.preview.set(await firstValueFrom(this.api.previewCompanyAssignment(this.company.id, this.body(), 10))); }
    catch (e) { this.errors.set(errorsOf(e)); } finally { this.busy.set(false); }
  }

  async save() {
    this.busy.set(true); this.errors.set([]);
    try { await firstValueFrom(this.api.assignCompany(this.company.id, this.body())); this.ref.close(true); }
    catch (e) { this.errors.set(errorsOf(e)); } finally { this.busy.set(false); }
  }
}

// ------------------------------------------------------------------------------------------------ shared pool

@Component({
  selector: 'app-shared-pool-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...COMMON],
  template: `
    <h2 mat-dialog-title>Assign shared pool · {{ company.code }}</h2>
    <mat-dialog-content class="dialog-wide">
      <div class="grid-2">
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment" (ngModelChange)="env.set($event)">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Pool</mat-label>
          <mat-select [(ngModel)]="f.poolId" required>
            @for (p of pools(); track p.id) { <mat-option [value]="p.id">{{ p.name }}{{ p.eligiblePlans.length ? ' · plans: ' + p.eligiblePlans.join(', ') : '' }}</mat-option> }
          </mat-select><mat-hint>Pools in {{ f.environment }}. Company plan: {{ company.servicePlan ?? 'none' }}.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="f.applicationId"><mat-option [value]="null">All applications</mat-option>
            @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Priority</mat-label><input matInput type="number" min="0" [(ngModel)]="f.priority" /><mat-hint>Lower is preferred.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Effective from</mat-label><input matInput type="datetime-local" [(ngModel)]="from" /><mat-hint>Empty = now</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Effective to</mat-label><input matInput type="datetime-local" [(ngModel)]="to" /></mat-form-field>
      </div>
      <mat-checkbox [(ngModel)]="f.replaceDedicated">End the company's dedicated assignment in this scope (switch to shared-pool routing)</mat-checkbox>
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><textarea matInput rows="2" [(ngModel)]="f.reason" maxlength="500"></textarea></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.poolId" (click)="save()">Assign pool</button>
    </mat-dialog-actions>`,
})
export class SharedPoolDialog implements OnInit {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<SharedPoolDialog, boolean>);
  lookups = inject(LookupService);
  company = inject<CompanyDetail>(MAT_DIALOG_DATA);
  envs = ENVIRONMENTS;
  f: SharedPoolAssignmentRequest = { poolId: '', applicationId: null, environment: this.company.defaultEnvironment, priority: 0, replaceDedicated: false,
    reason: '', expectedStamp: this.company.concurrencyStamp };
  from = ''; to = '';
  env = signal<Environment>(this.f.environment);
  pools = computed(() => this.lookups.pools().filter(p => p.environment === this.env()));
  busy = signal(false);
  errors = signal<string[]>([]);
  ngOnInit() { this.lookups.ensure(['pools', 'applications']); }
  async save() {
    this.busy.set(true); this.errors.set([]);
    try {
      await firstValueFrom(this.api.assignSharedPool(this.company.id, { ...this.f, effectiveFrom: fromLocalInput(this.from), effectiveTo: fromLocalInput(this.to), reason: this.f.reason?.trim() || null }));
      this.ref.close(true);
    } catch (e) { this.errors.set(errorsOf(e)); } finally { this.busy.set(false); }
  }
}

// ------------------------------------------------------------------------------------------------ transfer

@Component({
  selector: 'app-transfer-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...COMMON, MatRadioModule, StatusChip],
  template: `
    <h2 mat-dialog-title>Transfer {{ company.code }} to another server</h2>
    <mat-dialog-content class="dialog-wide">
      <p class="muted">Step 1 validates the target (environment, application, isolation, health, version and capacity). Step 2 performs the cutover.</p>
      <div class="grid-2">
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment" (ngModelChange)="env.set($event); dirty()">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="f.applicationId" (ngModelChange)="dirty()"><mat-option [value]="null">All applications</mat-option>
            @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Target server</mat-label>
          <mat-select [(ngModel)]="f.targetServerId" (ngModelChange)="dirty()" required>
            @for (s of servers(); track s.id) { <mat-option [value]="s.id">{{ label(s) }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Target secondary server</mat-label>
          <mat-select [(ngModel)]="f.targetSecondaryServerId" (ngModelChange)="dirty()"><mat-option [value]="null">Keep current secondary</mat-option>
            @for (s of servers(); track s.id) { <mat-option [value]="s.id" [disabled]="s.id === f.targetServerId">{{ label(s) }}</mat-option> }</mat-select></mat-form-field>
      </div>
      <h3>Cutover</h3>
      <mat-radio-group [(ngModel)]="f.mode" (ngModelChange)="dirty()" class="modes" aria-label="Cutover mode">
        <mat-radio-button value="Immediate">Immediate — new sessions go to the target; existing sessions finish where they are</mat-radio-button>
        <mat-radio-button value="Scheduled">Scheduled — switch at a set time</mat-radio-button>
        <mat-radio-button value="ForceReauthentication">Force re-authentication — switch now and sign every user out</mat-radio-button>
      </mat-radio-group>
      @if (f.mode === 'Scheduled') {
        <mat-form-field><mat-label>Cutover at</mat-label><input matInput type="datetime-local" [(ngModel)]="scheduledAt" (ngModelChange)="dirty()" required /></mat-form-field>
      }
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><textarea matInput rows="2" [(ngModel)]="f.reason" maxlength="500"></textarea></mat-form-field>
      @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
      @if (checks().length) {
        <h3>Validation {{ validated() ? '(passed)' : '(failed)' }}</h3>
        <table class="data"><thead><tr><th>Check</th><th>Result</th><th>Detail</th></tr></thead>
          <tbody>@for (c of checks(); track c.check) {
            <tr><td>{{ c.check }}</td><td><app-status [value]="c.passed ? 'Passed' : 'Failed'" /></td><td>{{ c.detail }}</td></tr>
          }</tbody></table>
      }
      @if (result(); as r) {
        <div class="callout" [class.good]="r.applied" [class.bad]="!r.applied">{{ r.applied ? 'Transfer applied.' : 'Transfer not applied: one or more checks failed.' }}</div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="!!result()?.applied">{{ result()?.applied ? 'Close' : 'Cancel' }}</button>
      @if (!result()?.applied) {
        <button mat-stroked-button [disabled]="busy() || !f.targetServerId" (click)="run(true)"><mat-icon>rule</mat-icon>1. Validate</button>
        <button mat-flat-button [class.danger]="f.mode === 'ForceReauthentication'" [disabled]="busy() || !validated()" (click)="run(false)">2. Perform transfer</button>
      }
    </mat-dialog-actions>`,
  styles: `.modes { display: flex; flex-direction: column; gap: 2px; margin-bottom: 8px; }`,
})
export class TransferDialog implements OnInit {
  private api = inject(ApiService);
  lookups = inject(LookupService);
  company = inject<CompanyDetail>(MAT_DIALOG_DATA);
  envs = ENVIRONMENTS;
  f: TransferRequest = { targetServerId: '', targetSecondaryServerId: null, environment: this.company.defaultEnvironment, applicationId: null,
    mode: 'Immediate' as CutoverMode, validateOnly: true, reason: '', expectedStamp: this.company.concurrencyStamp };
  scheduledAt = '';
  env = signal<Environment>(this.f.environment);
  servers = computed(() => this.lookups.servers().filter(s => s.environment === this.env() && s.adminState !== 'Retired'));
  checks = signal<TransferCheck[]>([]);
  validated = signal(false);
  result = signal<TransferResult | null>(null);
  busy = signal(false);
  errors = signal<string[]>([]);
  label = serverLabel;
  ngOnInit() { this.lookups.ensure(['servers', 'applications']); }

  dirty() { this.validated.set(false); this.checks.set([]); }

  async run(validateOnly: boolean) {
    this.busy.set(true); this.errors.set([]);
    const body: TransferRequest = { ...this.f, validateOnly, scheduledAt: this.f.mode === 'Scheduled' ? fromLocalInput(this.scheduledAt) : null, reason: this.f.reason?.trim() || null };
    try {
      const r = await firstValueFrom(this.api.transferCompany(this.company.id, body));
      this.checks.set(r.checks);
      if (validateOnly) this.validated.set(r.passed); else this.result.set(r);
    } catch (e) {
      // 422 carries the failed check list.
      if (e instanceof HttpErrorResponse && e.status === 422 && e.error?.checks) {
        this.checks.set(e.error.checks); this.validated.set(false); this.result.set(e.error as TransferResult);
      } else this.errors.set(errorsOf(e));
    } finally { this.busy.set(false); }
  }
}

// ------------------------------------------------------------------------------------------------ status

@Component({
  selector: 'app-company-status-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...COMMON],
  template: `
    <h2 mat-dialog-title>Company access · {{ company.code }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Status</mat-label>
        <mat-select [(ngModel)]="status">@for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select>
        <mat-hint>Suspended and disabled companies are refused at sign-in.</mat-hint></mat-form-field>
      <mat-checkbox [(ngModel)]="revoke">Also revoke all active sessions now</mat-checkbox>
      <mat-form-field class="full"><mat-label>Reason (recorded in the audit log)</mat-label><textarea matInput rows="2" [(ngModel)]="reason" maxlength="500"></textarea></mat-form-field>
      @if (error()) { <p class="error">{{ error() }}</p> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger]="status !== 'Active'" [disabled]="busy()" (click)="save()">Apply</button>
    </mat-dialog-actions>`,
})
export class CompanyStatusDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CompanyStatusDialog, { status: string; revokedSessions: number }>);
  company = inject<CompanyDetail>(MAT_DIALOG_DATA);
  statuses = COMPANY_STATUSES;
  status: CompanyStatus = this.company.status === 'Active' ? 'Disabled' : 'Active';
  revoke = this.company.status === 'Active';
  reason = '';
  busy = signal(false);
  error = signal('');
  async save() {
    this.busy.set(true); this.error.set('');
    try { this.ref.close(await firstValueFrom(this.api.setCompanyStatus(this.company.id, this.status, this.revoke, this.reason.trim()))); }
    catch (e) { this.error.set(errorMessage(e)); } finally { this.busy.set(false); }
  }
}
