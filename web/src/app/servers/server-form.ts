import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { errorList, errorMessage } from '../core/auth.service';
import { ENVIRONMENTS, SERVER_TYPES, Server, ServerRequest } from '../core/models';
import { LookupService } from '../core/ui.service';

export function emptyServer(): ServerRequest {
  return {
    name: '', hostname: '', ipAddress: null, baseUrl: 'https://', publicUrl: null, healthCheckUrl: null, environment: 'Production',
    region: null, serverType: 'Shared', cpuCores: null, ramGb: null, maxCapacity: 500, applicationVersion: null, priority: 0, weight: 100,
    requireManualRestore: false, drainTimeoutMinutes: 30, clientCertificateThumbprint: null, backendCertificateThumbprint: null, notes: null,
    applicationIds: [],
  };
}

export function serverToRequest(s: Server): ServerRequest {
  return {
    name: s.name, hostname: s.hostname, ipAddress: s.ipAddress ?? null, baseUrl: s.baseUrl, publicUrl: s.publicUrl ?? null,
    healthCheckUrl: s.healthCheckUrl ?? null, environment: s.environment, region: s.region ?? null, serverType: s.serverType,
    cpuCores: s.cpuCores ?? null, ramGb: s.ramGb ?? null, maxCapacity: s.maxCapacity, applicationVersion: s.applicationVersion ?? null,
    priority: s.priority, weight: s.weight, requireManualRestore: s.requireManualRestore, drainTimeoutMinutes: s.drainTimeoutMinutes,
    clientCertificateThumbprint: s.clientCertificateThumbprint ?? null, backendCertificateThumbprint: s.backendCertificateThumbprint ?? null,
    notes: s.notes ?? null, applicationIds: [...s.applicationIds], concurrencyStamp: s.concurrencyStamp,
  };
}

/** Normalises blank strings to null so optional fields validate server-side. */
export function cleanServer(r: ServerRequest): ServerRequest {
  const blank = (v?: string | null) => (v && v.trim() ? v.trim() : null);
  return {
    ...r, name: r.name.trim(), hostname: r.hostname.trim(), baseUrl: r.baseUrl.trim(), ipAddress: blank(r.ipAddress), publicUrl: blank(r.publicUrl),
    healthCheckUrl: blank(r.healthCheckUrl), region: blank(r.region), applicationVersion: blank(r.applicationVersion),
    clientCertificateThumbprint: blank(r.clientCertificateThumbprint), backendCertificateThumbprint: blank(r.backendCertificateThumbprint),
    notes: blank(r.notes), cpuCores: r.cpuCores ?? null, ramGb: r.ramGb ?? null,
  };
}

/** Every editable server field. Binds directly to the request object passed in. */
@Component({
  selector: 'app-server-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule],
  template: `
    @let f = model();
    <h3>Identity and addresses</h3>
    <div class="grid-3">
      <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="100" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Hostname</mat-label><input matInput [(ngModel)]="f.hostname" required maxlength="253" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>IP address</mat-label><input matInput [(ngModel)]="f.ipAddress" maxlength="45" [disabled]="readonly()" /></mat-form-field>
    </div>
    <div class="grid-2">
      <mat-form-field><mat-label>Base URL (backend)</mat-label><input matInput [(ngModel)]="f.baseUrl" required [disabled]="readonly()" />
        <mat-hint>Must be on an allow-listed backend host or network.</mat-hint></mat-form-field>
      <mat-form-field><mat-label>Health check URL</mat-label><input matInput [(ngModel)]="f.healthCheckUrl" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Public URL (redirect mode)</mat-label><input matInput [(ngModel)]="f.publicUrl" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Region</mat-label><input matInput [(ngModel)]="f.region" maxlength="100" [disabled]="readonly()" /></mat-form-field>
    </div>
    <h3>Placement and capacity</h3>
    <div class="grid-3">
      <mat-form-field><mat-label>Environment</mat-label>
        <mat-select [(ngModel)]="f.environment" [disabled]="readonly()">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field><mat-label>Server type</mat-label>
        <mat-select [(ngModel)]="f.serverType" [disabled]="readonly()">@for (t of types; track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select>
        <mat-hint>Dedicated servers serve a single tenant.</mat-hint></mat-form-field>
      <mat-form-field><mat-label>Application version</mat-label><input matInput [(ngModel)]="f.applicationVersion" maxlength="50" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>CPU cores</mat-label><input matInput type="number" min="0" max="4096" [(ngModel)]="f.cpuCores" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>RAM (GB)</mat-label><input matInput type="number" min="0" max="65536" [(ngModel)]="f.ramGb" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Max capacity (sessions)</mat-label><input matInput type="number" min="0" max="1000000" [(ngModel)]="f.maxCapacity" [disabled]="readonly()" />
        <mat-hint>0 = unlimited</mat-hint></mat-form-field>
      <mat-form-field><mat-label>Priority</mat-label><input matInput type="number" min="0" max="1000" [(ngModel)]="f.priority" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Weight</mat-label><input matInput type="number" min="0" max="10000" [(ngModel)]="f.weight" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Drain timeout (minutes)</mat-label><input matInput type="number" min="1" max="1440" [(ngModel)]="f.drainTimeoutMinutes" [disabled]="readonly()" /></mat-form-field>
    </div>
    <mat-form-field class="full"><mat-label>Hosted applications</mat-label>
      <mat-select multiple [(ngModel)]="f.applicationIds" [disabled]="readonly()">
        @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }} · {{ a.name }}</mat-option> }
      </mat-select></mat-form-field>
    <mat-checkbox [(ngModel)]="f.requireManualRestore" [disabled]="readonly()">Require manual restore after recovering from failure</mat-checkbox>
    <h3>Transport security</h3>
    <div class="grid-2">
      <mat-form-field><mat-label>Client certificate thumbprint (mTLS)</mat-label><input matInput [(ngModel)]="f.clientCertificateThumbprint" maxlength="64" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Pinned backend certificate thumbprint</mat-label><input matInput [(ngModel)]="f.backendCertificateThumbprint" maxlength="64" [disabled]="readonly()" /></mat-form-field>
    </div>
    <mat-form-field class="full"><mat-label>Notes</mat-label><textarea matInput rows="2" [(ngModel)]="f.notes" maxlength="4000" [disabled]="readonly()"></textarea></mat-form-field>`,
  styles: `h3 { margin: 10px 0 6px; } mat-checkbox { display: block; margin-bottom: 8px; }`,
})
export class ServerForm implements OnInit {
  lookups = inject(LookupService);
  model = input.required<ServerRequest>();
  readonly = input(false);
  envs = ENVIRONMENTS;
  types = SERVER_TYPES;
  ngOnInit() { this.lookups.ensure(['applications']); }
}

@Component({
  selector: 'app-add-server-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, ServerForm],
  template: `
    <h2 mat-dialog-title>Add server</h2>
    <mat-dialog-content class="dialog-wide"><app-server-form [model]="form" />
      @if (errors().length) { <div class="callout bad"><strong>Could not save</strong><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !form.name || !form.hostname || !form.baseUrl" (click)="save()">Add server</button>
    </mat-dialog-actions>`,
})
export class AddServerDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<AddServerDialog, string>);
  data = inject<ServerRequest | null>(MAT_DIALOG_DATA, { optional: true });
  form: ServerRequest = this.data ?? emptyServer();
  busy = signal(false);
  errors = signal<string[]>([]);

  async save() {
    this.busy.set(true);
    this.errors.set([]);
    try {
      const r = await firstValueFrom(this.api.createServer(cleanServer(this.form)));
      this.ref.close(r.id);
    } catch (e) {
      const list = errorList(e);
      this.errors.set(list.length ? list : [errorMessage(e)]);
    } finally { this.busy.set(false); }
  }
}
