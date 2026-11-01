import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TabDef, Tabs } from '../shared/tabs';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import { ALERT_SEVERITIES, ALERT_STATUSES, Alert, CHANNEL_TYPES, Channel } from '../core/models';
import { UiService } from '../core/ui.service';
import { AgoPipe, StatusChip } from '../shared/display';

@Component({
  selector: 'app-channel-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit channel' : 'New notification channel' }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="100" /></mat-form-field>
      <div class="grid-2">
        <mat-form-field><mat-label>Type</mat-label><mat-select [(ngModel)]="f.type">@for (t of types; track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Minimum severity</mat-label><mat-select [(ngModel)]="f.minimumSeverity">@for (s of severities; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>{{ f.type === 'Email' ? 'E-mail addresses (comma separated)' : 'Webhook URL (https)' }}</mat-label>
        <input matInput [(ngModel)]="f.target" required maxlength="2000" />
        @if (f.type !== 'Email') { <mat-hint>Signing secrets are configured on the server, not stored here.</mat-hint> }</mat-form-field>
      <mat-slide-toggle [(ngModel)]="f.isEnabled">Enabled</mat-slide-toggle>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.name.trim() || !f.target.trim()" (click)="save()">Save</button></mat-dialog-actions>`,
})
export class ChannelDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<ChannelDialog, boolean>);
  data = inject<Channel | null>(MAT_DIALOG_DATA, { optional: true });
  types = CHANNEL_TYPES; severities = ALERT_SEVERITIES;
  f: Omit<Channel, 'id'> = this.data ? { name: this.data.name, type: this.data.type, target: this.data.target, minimumSeverity: this.data.minimumSeverity, isEnabled: this.data.isEnabled }
    : { name: '', type: 'Email', target: '', minimumSeverity: 'Warning', isEnabled: true };
  busy = signal(false);
  errors = signal<string[]>([]);
  async save() {
    this.busy.set(true); this.errors.set([]);
    try { await firstValueFrom(this.api.saveChannel(this.data?.id ?? null, { ...this.f, name: this.f.name.trim(), target: this.f.target.trim() }), { defaultValue: null }); this.ref.close(true); }
    catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-alerts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tabs, DatePipe, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatSelectModule, StatusChip, AgoPipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Alerts</h1><span class="spacer"></span><button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-tabs [tabs]="tabs()" [(active)]="tab">
          @switch (tab()) {
        @case ('alerts') {
          <div class="filters">
            <mat-form-field><mat-label>Status</mat-label>
              <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); loadAlerts()"><mat-option value="">Open and acknowledged</mat-option>
                @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Minimum severity</mat-label>
              <mat-select [ngModel]="severity()" (ngModelChange)="severity.set($event); loadAlerts()"><mat-option value="">Any</mat-option>
                @for (s of severities; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
          </div>
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Severity</th><th>Status</th><th>Alert</th><th>Server / company</th><th class="num">Count</th><th>First raised</th><th>Last raised</th><th>Handled</th><th></th></tr></thead>
            <tbody>@for (a of alerts(); track a.id) {
              <tr><td><app-status [value]="a.severity" /></td><td><app-status [value]="a.status" /></td>
                <td><b>{{ a.title }}</b><div class="muted small">{{ a.type }}{{ a.message ? ' — ' + a.message : '' }}</div></td>
                <td>@if (a.serverId) { <a [routerLink]="['/servers', a.serverId]">{{ a.server }}</a> } @if (a.companyId) { <a [routerLink]="['/companies', a.companyId]">{{ a.company }}</a> }</td>
                <td class="num">{{ a.occurrences }}</td><td class="nowrap">{{ a.firstRaisedAt | date: 'short' }}</td><td>{{ a.lastRaisedAt | ago }}</td>
                <td class="small">@if (a.acknowledgedBy) { Ack {{ a.acknowledgedBy }} {{ a.acknowledgedAt | ago }} }
                  @if (a.resolvedBy) { <div>Resolved {{ a.resolvedBy }} {{ a.resolvedAt | ago }}{{ a.resolutionNote ? ': ' + a.resolutionNote : '' }}</div> }</td>
                <td class="nowrap">@if (canManage && a.status !== 'Resolved') {
                  @if (a.status === 'Open') { <button mat-button (click)="ack(a)">Acknowledge</button> }
                  <button mat-button (click)="resolve(a)">Resolve</button> }</td></tr>
            } @empty { <tr><td colspan="9" class="empty">No alerts match. All quiet.</td></tr> }</tbody>
          </table></div>
        }
        @case ('channels') {
          <div class="panel">
            <div class="panel-head"><h2>Notification channels</h2><span class="spacer"></span>
              @if (canManage) { <button mat-flat-button (click)="editChannel(null)"><mat-icon>add</mat-icon>New channel</button> }</div>
            <table class="data"><thead><tr><th>Name</th><th>Type</th><th>Target</th><th>Minimum severity</th><th>Status</th><th></th></tr></thead>
              <tbody>@for (c of channels(); track c.id) {
                <tr><td><b>{{ c.name }}</b></td><td>{{ c.type }}</td><td class="mono target">{{ c.target }}</td><td><app-status [value]="c.minimumSeverity" /></td>
                  <td><app-status [value]="c.isEnabled ? 'Enabled' : 'Disabled'" /></td>
                  <td class="nowrap">@if (canManage) { <button mat-button (click)="test(c)">Send test</button><button mat-button (click)="editChannel(c)">Edit</button>
                    <button mat-button class="danger" (click)="deleteChannel(c)">Delete</button> }</td></tr>
              } @empty { <tr><td colspan="6" class="empty">No channels configured.</td></tr> }</tbody></table>
          </div>
        }
      }
        </app-tabs>
    </div>`,
  styles: `.nowrap { white-space: nowrap; } .target { max-width: 320px; word-break: break-all; }`,
})
export class Alerts implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  canManage = inject(AuthService).can(P.alertsManage);
  statuses = ALERT_STATUSES; severities = ALERT_SEVERITIES;
  status = signal(''); severity = signal('');
  alerts = signal<Alert[]>([]);
  tab = signal('alerts');
  tabs = computed<TabDef[]>(() => [{ key: 'alerts', label: `Alerts (${this.alerts().length})` }, { key: 'channels', label: 'Notification channels' }]);
  channels = signal<Channel[]>([]);
  ngOnInit() { this.load(); }
  load() { this.loadAlerts(); this.api.channels().subscribe({ next: c => this.channels.set(c), error: e => this.ui.error(e) }); }
  loadAlerts() { this.api.alerts({ status: this.status(), severity: this.severity(), take: 1000 }).subscribe({ next: a => this.alerts.set(a), error: e => this.ui.error(e) }); }
  async ack(a: Alert) { if (await this.ui.run(this.api.acknowledgeAlert(a.id), 'Alert acknowledged') !== undefined) this.loadAlerts(); }
  async resolve(a: Alert) {
    const note = await this.ui.prompt({ title: `Resolve · ${a.title}`, label: 'Resolution note', required: false, multiline: true, confirmText: 'Resolve' });
    if (note === undefined) return;
    if (await this.ui.run(this.api.resolveAlert(a.id, note), 'Alert resolved') !== undefined) this.loadAlerts();
  }
  async editChannel(c: Channel | null) {
    if (await firstValueFrom(this.dialog.open(ChannelDialog, { data: c, width: '520px', maxWidth: '95vw' }).afterClosed())) { this.ui.ok('Channel saved'); this.load(); }
  }
  async deleteChannel(c: Channel) {
    const ok = await this.ui.confirm({ title: 'Delete channel', message: `Delete ${c.name}? It stops receiving alert notifications.`, confirmText: 'Delete', danger: true });
    if (ok && await this.ui.run(this.api.deleteChannel(c.id), 'Channel deleted') !== undefined) this.load();
  }
  async test(c: Channel) {
    const r = await this.ui.run(this.api.testChannel(c.id));
    if (r) { if (r.success) this.ui.ok(`Test notification sent to ${c.name}`); else this.ui.error(new Error(`Test failed: ${r.error}`)); }
  }
}
