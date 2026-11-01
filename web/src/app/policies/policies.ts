import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TabDef, Tabs } from '../shared/tabs';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import {
  AFFINITY_MODES, APP_ROUTING_MODES, AppBinding, Application, ENVIRONMENTS, FAILBACK_POLICIES, FailoverPolicy, RoutingPolicy,
} from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { HumanizePipe, StatusChip } from '../shared/display';

const DIALOG = [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatSlideToggleModule];

function errs(e: unknown, dup?: string) {
  const l = errorList(e);
  if (l.length) return l;
  const code = (e as { error?: { error?: string } }).error?.error;
  return [dup && code?.startsWith('duplicate') ? dup : errorMessage(e)];
}

/** Shared save-and-close behaviour for the editor dialogs. */
abstract class EditorDialog {
  busy = signal(false);
  errors = signal<string[]>([]);
  protected abstract ref: MatDialogRef<unknown, boolean>;
  protected async submit(call: Observable<unknown>, dup?: string) {
    this.busy.set(true); this.errors.set([]);
    try { await firstValueFrom(call, { defaultValue: null }); this.ref.close(true); }
    catch (e) { this.errors.set(errs(e, dup)); } finally { this.busy.set(false); }
  }
}

// ------------------------------------------------------------------------------------------------ failover policy

@Component({
  selector: 'app-failover-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...DIALOG, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit failover policy' : 'New failover policy' }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="100" /></mat-form-field>
      <mat-slide-toggle [(ngModel)]="f.failoverEnabled">Failover enabled</mat-slide-toggle>
      <div class="grid-2 top">
        <mat-form-field><mat-label>Failover pool</mat-label>
          <mat-select [(ngModel)]="f.failoverPoolId" [disabled]="!f.failoverEnabled"><mat-option [value]="null">None (secondary only)</mat-option>
            @for (p of lookups.pools(); track p.id) { <mat-option [value]="p.id">{{ p.name }} · {{ p.environment }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Failback</mat-label>
          <mat-select [(ngModel)]="f.failbackPolicy">@for (x of failbacks; track x) { <mat-option [value]="x">{{ x | humanize }}</mat-option> }</mat-select>
          <mat-hint>{{ hint(f.failbackPolicy!) }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Max failover attempts</mat-label><input matInput type="number" min="0" max="50" [(ngModel)]="f.maxFailoverAttempts" /></mat-form-field>
        <mat-form-field><mat-label>Failover priority</mat-label><input matInput type="number" min="0" max="1000" [(ngModel)]="f.failoverPriority" /></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>Description</mat-label><textarea matInput rows="2" [(ngModel)]="f.description" maxlength="1000"></textarea></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.name?.trim()" (click)="save()">Save</button></mat-dialog-actions>`,
  styles: `.top { margin-top: 12px; }`,
})
export class FailoverDialog extends EditorDialog {
  private api = inject(ApiService);
  protected ref = inject(MatDialogRef<FailoverDialog, boolean>);
  lookups = inject(LookupService);
  data = inject<FailoverPolicy | null>(MAT_DIALOG_DATA, { optional: true });
  failbacks = FAILBACK_POLICIES;
  f: Partial<FailoverPolicy> = this.data ? { ...this.data } : { name: '', description: '', failoverEnabled: true, failoverPoolId: null, failbackPolicy: 'Automatic', maxFailoverAttempts: 3, failoverPriority: 0 };
  hint(p: string) {
    return ({ Automatic: 'Return to the primary once it is healthy and stable.', EndOfSession: 'Failed-over sessions stay until they end.',
      Manual: 'Like end-of-session; the primary stays out until an administrator restores it.' } as Record<string, string>)[p] ?? '';
  }
  save() {
    const { name, description, failoverEnabled, failoverPoolId, failbackPolicy, maxFailoverAttempts, failoverPriority, concurrencyStamp } = this.f;
    this.submit(this.api.saveFailoverPolicy(this.data?.id ?? null, { name: name!.trim(), description: description?.trim() || null, failoverEnabled,
      failoverPoolId: failoverEnabled ? failoverPoolId : null, failbackPolicy, maxFailoverAttempts, failoverPriority, concurrencyStamp }));
  }
}

// ------------------------------------------------------------------------------------------------ routing policy

type WeightKey = 'cpuWeight' | 'memoryWeight' | 'activeRequestsWeight' | 'responseTimeWeight' | 'errorRateWeight';

@Component({
  selector: 'app-routing-policy-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...DIALOG, MatSliderModule, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit routing policy' : 'New routing policy' }}</h2>
    <mat-dialog-content class="dialog-wide">
      <div class="grid-2">
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="100" /></mat-form-field>
        <mat-form-field><mat-label>Default affinity</mat-label>
          <mat-select [(ngModel)]="f.defaultAffinity">@for (a of affinities; track a) { <mat-option [value]="a">{{ a | humanize }}</mat-option> }</mat-select></mat-form-field>
      </div>
      <div class="wrap-row"><mat-checkbox [(ngModel)]="f.isDefault">Default policy (used by companies without a policy)</mat-checkbox>
        <mat-checkbox [(ngModel)]="f.allowDegradedServers">Allow routing to degraded servers</mat-checkbox></div>
      <h3 class="gap">Load-score metric weights</h3>
      <p class="muted small">Used by lowest-utilisation and weighted algorithms. Each weight is 0–1; they should add up to 1.</p>
      @for (w of weights; track w.key) {
        <div class="weight">
          <span class="wl">{{ w.label }}</span>
          <mat-slider min="0" max="1" step="0.05" discrete class="ws"><input matSliderThumb [ngModel]="f[w.key]" (ngModelChange)="setW(w.key, $event)" [attr.aria-label]="w.label" /></mat-slider>
          <input class="wn" type="number" min="0" max="1" step="0.01" [ngModel]="f[w.key]" (ngModelChange)="setW(w.key, $event)" [attr.aria-label]="w.label + ' value'" />
        </div>
      }
      <div class="sum" [attr.data-ok]="sumOk()" role="status">
        <span class="material-icons" aria-hidden="true">{{ sumOk() ? 'check_circle' : 'warning' }}</span>
        Sum of weights: <b>{{ sum().toFixed(2) }}</b> {{ sumOk() ? '— balanced' : '— should be 1.00' }}
        @if (!sumOk() && sum() > 0) { <button mat-button type="button" (click)="normalise()">Normalise to 1.00</button> }
      </div>
      <div class="grid-3 gap">
        <mat-form-field><mat-label>Response-time ceiling (ms)</mat-label><input matInput type="number" min="100" max="60000" [(ngModel)]="f.responseTimeCeilingMs" />
          <mat-hint>Response time at which the metric scores 100%.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Session idle timeout (min)</mat-label><input matInput type="number" min="1" max="1440" [(ngModel)]="f.sessionIdleTimeoutMinutes" /></mat-form-field>
        <mat-form-field><mat-label>Session absolute timeout (h)</mat-label><input matInput type="number" min="1" max="168" [(ngModel)]="f.sessionAbsoluteTimeoutHours" /></mat-form-field>
      </div>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.name?.trim()" (click)="save()">Save</button></mat-dialog-actions>`,
  styles: `
    .gap { margin-top: 12px; }
    .weight { display: grid; grid-template-columns: 150px 1fr 80px; align-items: center; gap: 8px; }
    .ws { width: 100%; } .wn { font: inherit; padding: 4px 6px; border: 1px solid var(--border); border-radius: 4px; background: var(--surface); color: var(--text); text-align: right; }
    .sum { display: flex; align-items: center; gap: 6px; padding: 6px 10px; border-radius: 6px; background: var(--warn-bg); color: var(--warn); margin-top: 4px; }
    .sum[data-ok='true'] { background: var(--good-bg); color: var(--good); }
    .sum .material-icons { font-size: 18px; }
  `,
})
export class RoutingPolicyDialog extends EditorDialog {
  private api = inject(ApiService);
  protected ref = inject(MatDialogRef<RoutingPolicyDialog, boolean>);
  data = inject<RoutingPolicy | null>(MAT_DIALOG_DATA, { optional: true });
  affinities = AFFINITY_MODES;
  weights: { key: WeightKey; label: string }[] = [
    { key: 'cpuWeight', label: 'CPU' }, { key: 'memoryWeight', label: 'Memory' }, { key: 'activeRequestsWeight', label: 'Active requests' },
    { key: 'responseTimeWeight', label: 'Response time' }, { key: 'errorRateWeight', label: 'Error rate' },
  ];
  f: Partial<RoutingPolicy> = this.data ? { ...this.data } : { name: '', isDefault: false, allowDegradedServers: true, defaultAffinity: 'PerSession', cpuWeight: 0.3,
    memoryWeight: 0.2, activeRequestsWeight: 0.25, responseTimeWeight: 0.15, errorRateWeight: 0.1, responseTimeCeilingMs: 2000, sessionIdleTimeoutMinutes: 30, sessionAbsoluteTimeoutHours: 12 };
  private tick = signal(0);
  sum = computed(() => { this.tick(); return this.weights.reduce((s, w) => s + (Number(this.f[w.key]) || 0), 0); });
  sumOk = computed(() => Math.abs(this.sum() - 1) < 0.005);

  setW(k: WeightKey, v: number | string) { this.f[k] = Math.max(0, Math.min(1, Number(v) || 0)); this.tick.update(x => x + 1); }
  normalise() {
    const s = this.sum();
    for (const w of this.weights) this.f[w.key] = Math.round(((Number(this.f[w.key]) || 0) / s) * 1000) / 1000;
    this.tick.update(x => x + 1);
  }
  save() {
    const { id: _id, ...body } = this.f;
    this.submit(this.api.saveRoutingPolicy(this.data?.id ?? null, { ...body, name: this.f.name!.trim() }));
  }
}

// ------------------------------------------------------------------------------------------------ applications

@Component({
  selector: 'app-application-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: DIALOG,
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit application · ' + data.code : 'New application' }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Code</mat-label><input matInput [(ngModel)]="f.code" required maxlength="40" [disabled]="!!data" />
        <mat-hint>{{ data ? 'The code cannot be changed.' : 'Stored in upper case.' }}</mat-hint></mat-form-field>
      <mat-form-field class="full"><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="200" /></mat-form-field>
      <mat-form-field class="full"><mat-label>Minimum server version</mat-label><input matInput [(ngModel)]="f.minimumServerVersion" maxlength="50" placeholder="e.g. 2.0" /></mat-form-field>
      <mat-form-field class="full"><mat-label>Description</mat-label><textarea matInput rows="2" [(ngModel)]="f.description" maxlength="1000"></textarea></mat-form-field>
      <mat-slide-toggle [(ngModel)]="f.isEnabled">Enabled</mat-slide-toggle>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.code?.trim() || !f.name?.trim()" (click)="save()">Save</button></mat-dialog-actions>`,
})
export class ApplicationDialog extends EditorDialog {
  private api = inject(ApiService);
  protected ref = inject(MatDialogRef<ApplicationDialog, boolean>);
  data = inject<Application | null>(MAT_DIALOG_DATA, { optional: true });
  f: Partial<Application> = this.data ? { ...this.data } : { code: '', name: '', description: '', isEnabled: true, minimumServerVersion: '' };
  save() {
    this.submit(this.api.saveApplication(this.data?.id ?? null, { code: this.f.code!.trim(), name: this.f.name!.trim(), description: this.f.description?.trim() || null,
      isEnabled: this.f.isEnabled, minimumServerVersion: this.f.minimumServerVersion?.trim() || null, concurrencyStamp: this.data?.concurrencyStamp }), 'An application with this code already exists.');
  }
}

@Component({
  selector: 'app-binding-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: DIALOG,
  template: `
    <h2 mat-dialog-title>{{ data.binding ? 'Edit' : 'Add' }} environment binding · {{ data.app.code }}</h2>
    <mat-dialog-content>
      <div class="grid-2">
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Routing mode</mat-label>
          <mat-select [(ngModel)]="f.routingMode">@for (m of modes; track m) { <mat-option [value]="m">{{ m }}</mat-option> }</mat-select>
          <mat-hint>{{ f.routingMode === 'Proxy' ? 'Transparent reverse proxy (preferred)' : 'Redirect with a secure hand-off token' }}</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Public host</mat-label><input matInput [(ngModel)]="f.publicHost" required maxlength="253" placeholder="erp.example.com" /></mat-form-field>
        <mat-form-field><mat-label>Path prefix</mat-label><input matInput [(ngModel)]="f.pathPrefix" maxlength="200" placeholder="/" /></mat-form-field>
        <mat-form-field><mat-label>Request timeout (s)</mat-label><input matInput type="number" min="1" max="3600" [(ngModel)]="f.requestTimeoutSeconds" /></mat-form-field>
        <mat-form-field><mat-label>Max request body (MB)</mat-label><input matInput type="number" min="0" [(ngModel)]="bodyMb" /><mat-hint>Empty = gateway default</mat-hint></mat-form-field>
      </div>
      <mat-slide-toggle [(ngModel)]="f.isEnabled">Enabled</mat-slide-toggle>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.publicHost?.trim()" (click)="save()">Save</button></mat-dialog-actions>`,
})
export class BindingDialog extends EditorDialog {
  private api = inject(ApiService);
  protected ref = inject(MatDialogRef<BindingDialog, boolean>);
  data = inject<{ app: Application; binding: AppBinding | null }>(MAT_DIALOG_DATA);
  envs = ENVIRONMENTS; modes = APP_ROUTING_MODES;
  f: Partial<AppBinding> = this.data.binding ? { ...this.data.binding } : { environment: 'Production', publicHost: '', pathPrefix: '/', routingMode: 'Proxy', isEnabled: true, requestTimeoutSeconds: 100 };
  bodyMb: number | null = this.data.binding?.maxRequestBodyBytes ? Math.round(this.data.binding.maxRequestBodyBytes / 1048576 * 100) / 100 : null;
  save() {
    const { id: _id, ...rest } = this.f;
    this.submit(this.api.saveBinding(this.data.app.id, this.data.binding?.id ?? null, { ...rest, publicHost: rest.publicHost!.trim(), pathPrefix: rest.pathPrefix?.trim() || '/',
      maxRequestBodyBytes: this.bodyMb ? Math.round(this.bodyMb * 1048576) : null }), 'That host and path prefix are already bound.');
  }
}

// ------------------------------------------------------------------------------------------------ page

@Component({
  selector: 'app-policies',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tabs, MatButtonModule, MatIconModule, StatusChip, HumanizePipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Policies and applications</h1></div>
      <app-tabs [tabs]="tabs()" [(active)]="tab" >
          @switch (tab()) {
        @case ('failover') {
          <div class="panel">
            <div class="panel-head"><h2>Failover policies</h2><span class="spacer"></span>
              @if (canWrite) { <button mat-flat-button (click)="editFailover(null)"><mat-icon>add</mat-icon>New policy</button> }</div>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Name</th><th>Failover</th><th>Failover pool</th><th>Failback</th><th class="num">Max attempts</th><th class="num">Priority</th><th>Description</th><th></th></tr></thead>
              <tbody>@for (p of failover(); track p.id) {
                <tr><td><b>{{ p.name }}</b></td><td><app-status [value]="p.failoverEnabled ? 'Enabled' : 'Disabled'" /></td><td>{{ p.failoverPool ?? '—' }}</td>
                  <td>{{ p.failbackPolicy | humanize }}</td><td class="num">{{ p.maxFailoverAttempts }}</td><td class="num">{{ p.failoverPriority }}</td><td class="small">{{ p.description }}</td>
                  <td>@if (canWrite) { <button mat-button (click)="editFailover(p)">Edit</button> }</td></tr>
              } @empty { <tr><td colspan="8" class="empty">No failover policies.</td></tr> }</tbody>
            </table></div>
            <p class="muted small">Policies cannot be deleted through the API; stop referencing a policy from assignments and pools instead.</p>
          </div>
        }
        @case ('routing') {
          <div class="panel">
            <div class="panel-head"><h2>Routing policies</h2><span class="spacer"></span>
              @if (canWrite) { <button mat-flat-button (click)="editRouting(null)"><mat-icon>add</mat-icon>New policy</button> }</div>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Name</th><th>Default</th><th>Degraded servers</th><th>Affinity</th><th>Load-score weights (CPU / mem / req / resp / err)</th><th class="num">Resp. ceiling</th><th>Session timeouts</th><th></th></tr></thead>
              <tbody>@for (p of routing(); track p.id) {
                <tr><td><b>{{ p.name }}</b></td><td>@if (p.isDefault) { <app-status value="Active" label="Default" /> }</td>
                  <td>{{ p.allowDegradedServers ? 'Allowed' : 'Excluded' }}</td><td>{{ p.defaultAffinity | humanize }}</td>
                  <td class="mono">{{ p.cpuWeight }} / {{ p.memoryWeight }} / {{ p.activeRequestsWeight }} / {{ p.responseTimeWeight }} / {{ p.errorRateWeight }}</td>
                  <td class="num">{{ p.responseTimeCeilingMs }} ms</td><td>{{ p.sessionIdleTimeoutMinutes }} min idle · {{ p.sessionAbsoluteTimeoutHours }} h max</td>
                  <td>@if (canWrite) { <button mat-button (click)="editRouting(p)">Edit</button> }</td></tr>
              } @empty { <tr><td colspan="8" class="empty">No routing policies.</td></tr> }</tbody>
            </table></div>
          </div>
        }
        @case ('apps') {
          <div class="panel">
            <div class="panel-head"><h2>Applications and environment bindings</h2><span class="spacer"></span>
              @if (canWrite) { <button mat-flat-button (click)="editApp(null)"><mat-icon>add</mat-icon>New application</button> }</div>
            @for (a of apps(); track a.id) {
              <div class="app">
                <div class="panel-head"><b>{{ a.code }}</b><span>{{ a.name }}</span><app-status [value]="a.isEnabled ? 'Enabled' : 'Disabled'" />
                  @if (a.minimumServerVersion) { <span class="tag">min server {{ a.minimumServerVersion }}</span> }
                  <span class="muted small">{{ a.description }}</span><span class="spacer"></span>
                  @if (canWrite) { <button mat-button (click)="editApp(a)">Edit</button><button mat-button (click)="editBinding(a, null)"><mat-icon>add_link</mat-icon>Add binding</button> }</div>
                <table class="data"><thead><tr><th>Environment</th><th>Public host</th><th>Path prefix</th><th>Mode</th><th class="num">Timeout</th><th class="num">Max body</th><th>Status</th><th></th></tr></thead>
                  <tbody>@for (b of a.environments; track b.id) {
                    <tr><td>{{ b.environment }}</td><td class="mono">{{ b.publicHost }}</td><td class="mono">{{ b.pathPrefix }}</td><td>{{ b.routingMode }}</td>
                      <td class="num">{{ b.requestTimeoutSeconds }} s</td><td class="num">{{ b.maxRequestBodyBytes ? (b.maxRequestBodyBytes / 1048576).toFixed(1) + ' MB' : 'default' }}</td>
                      <td><app-status [value]="b.isEnabled ? 'Enabled' : 'Disabled'" /></td>
                      <td>@if (canWrite) { <button mat-button (click)="editBinding(a, b)">Edit</button><button mat-button class="danger" (click)="deleteBinding(a, b)">Delete</button> }</td></tr>
                  } @empty { <tr><td colspan="8" class="empty">No environment bindings — the gateway will not route this application.</td></tr> }</tbody></table>
              </div>
            } @empty { <div class="empty">No applications.</div> }
          </div>
        }
      }
        </app-tabs>
    </div>`,
  styles: `.app { border: 1px solid var(--border); border-radius: 6px; padding: 8px; margin-bottom: 10px; }`,
})
export class Policies implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  private lookups = inject(LookupService);
  canWrite = inject(AuthService).can(P.policiesWrite);
  tab = signal('failover');
  tabs = signal<TabDef[]>([{ key: 'failover', label: 'Failover policies' }, { key: 'routing', label: 'Routing policies' }, { key: 'apps', label: 'Applications' }]);
  failover = signal<FailoverPolicy[]>([]);
  routing = signal<RoutingPolicy[]>([]);
  apps = signal<Application[]>([]);

  ngOnInit() { this.load(); this.lookups.ensure(['pools']); }

  load() {
    this.api.failoverPolicies().subscribe({ next: x => { this.failover.set(x); this.lookups.failoverPolicies.set(x); }, error: e => this.ui.error(e) });
    this.api.routingPolicies().subscribe({ next: x => { this.routing.set(x); this.lookups.routingPolicies.set(x); }, error: e => this.ui.error(e) });
    this.api.applications().subscribe({ next: x => { this.apps.set(x); this.lookups.applications.set(x); }, error: e => this.ui.error(e) });
  }

  private async open(c: unknown, data: unknown, msg: string, width = '560px') {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const ok = await firstValueFrom(this.dialog.open(c as any, { data, width, maxWidth: '96vw' }).afterClosed());
    if (ok) { this.ui.ok(msg); this.load(); }
  }

  editFailover(p: FailoverPolicy | null) { this.open(FailoverDialog, p, 'Failover policy saved'); }
  editRouting(p: RoutingPolicy | null) { this.open(RoutingPolicyDialog, p, 'Routing policy saved', '720px'); }
  editApp(a: Application | null) { this.open(ApplicationDialog, a, 'Application saved', '480px'); }
  editBinding(a: Application, b: AppBinding | null) { this.open(BindingDialog, { app: a, binding: b }, 'Binding saved', '620px'); }

  async deleteBinding(a: Application, b: AppBinding) {
    const ok = await this.ui.confirm({ title: 'Delete binding', message: `Stop routing ${b.publicHost}${b.pathPrefix} (${b.environment}) to ${a.code}?`, confirmText: 'Delete', danger: true });
    if (!ok) return;
    if (await this.ui.run(this.api.deleteBinding(a.id, b.id), 'Binding deleted') !== undefined) this.load();
  }
}
