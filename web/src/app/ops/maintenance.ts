import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import { MaintenanceWindow } from '../core/models';
import { LookupService, UiService, fromLocalInput, toLocalInput } from '../core/ui.service';
import { StatusChip } from '../shared/display';

@Component({
  selector: 'app-maintenance-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule],
  template: `
    <h2 mat-dialog-title>Schedule maintenance</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Server</mat-label>
        <mat-select [(ngModel)]="serverId" required>@for (s of lookups.servers(); track s.id) { <mat-option [value]="s.id">{{ s.name }} · {{ s.environment }}</mat-option> }</mat-select></mat-form-field>
      <div class="grid-2">
        <mat-form-field><mat-label>Starts</mat-label><input matInput type="datetime-local" [(ngModel)]="starts" required /></mat-form-field>
        <mat-form-field><mat-label>Ends</mat-label><input matInput type="datetime-local" [(ngModel)]="ends" required /></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>Drain timeout (minutes)</mat-label><input matInput type="number" min="1" max="1440" [(ngModel)]="drain" />
        <mat-hint>The server drains from the start time; remaining sessions move after this timeout.</mat-hint></mat-form-field>
      <mat-checkbox [(ngModel)]="autoRestore">Return the server to service automatically when the window ends</mat-checkbox>
      <mat-form-field class="full"><mat-label>Reason</mat-label><textarea matInput rows="2" [(ngModel)]="reason" maxlength="500"></textarea></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !serverId || !starts || !ends" (click)="save()">Schedule</button></mat-dialog-actions>`,
})
export class MaintenanceDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<MaintenanceDialog, boolean>);
  lookups = inject(LookupService);
  serverId = '';
  starts = toLocalInput(new Date(Date.now() + 3600_000).toISOString());
  ends = toLocalInput(new Date(Date.now() + 3 * 3600_000).toISOString());
  drain = 30; autoRestore = true; reason = '';
  busy = signal(false);
  errors = signal<string[]>([]);
  async save() {
    this.busy.set(true); this.errors.set([]);
    try {
      await firstValueFrom(this.api.scheduleMaintenance({ serverId: this.serverId, startsAt: fromLocalInput(this.starts)!, endsAt: fromLocalInput(this.ends)!,
        drainTimeoutMinutes: this.drain, autoRestore: this.autoRestore, reason: this.reason.trim() || null }));
      this.ref.close(true);
    } catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-maintenance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatButtonModule, MatIconModule, MatCheckboxModule, StatusChip],
  template: `
    <div class="page">
      <div class="page-head"><h1>Maintenance windows</h1><span class="spacer"></span>
        <mat-checkbox [ngModel]="includeCompleted()" (ngModelChange)="includeCompleted.set($event); load()">Include completed and cancelled</mat-checkbox>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (canOperate) { <button mat-flat-button (click)="schedule()"><mat-icon>event</mat-icon>Schedule maintenance</button> }</div>
      <div class="table-wrap"><table class="data">
        <thead><tr><th>Server</th><th>Status</th><th>Starts</th><th>Ends</th><th class="num">Drain timeout</th><th>Auto-restore</th><th>Reason</th><th>Created</th><th></th></tr></thead>
        <tbody>@for (w of rows(); track w.id) {
          <tr><td><a [routerLink]="['/servers', w.serverId]">{{ w.server }}</a></td><td><app-status [value]="w.status" /></td>
            <td class="nowrap">{{ w.startsAt | date: 'medium' }}</td><td class="nowrap">{{ w.endsAt | date: 'medium' }}</td><td class="num">{{ w.drainTimeoutMinutes }} min</td>
            <td>{{ w.autoRestore ? 'Yes' : 'No' }}</td><td>{{ w.reason }}</td><td class="small">{{ w.createdBy }} · {{ w.createdAt | date: 'short' }}</td>
            <td>@if (canOperate && w.status === 'Scheduled') { <button mat-button class="danger" (click)="cancel(w)">Cancel</button> }</td></tr>
        } @empty { <tr><td colspan="9" class="empty">No maintenance windows.</td></tr> }</tbody>
      </table></div>
    </div>`,
  styles: `.nowrap { white-space: nowrap; }`,
})
export class Maintenance implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  private lookups = inject(LookupService);
  canOperate = inject(AuthService).can(P.serversOperate);
  includeCompleted = signal(false);
  rows = signal<MaintenanceWindow[]>([]);
  ngOnInit() { this.load(); this.lookups.ensure(['servers']); }
  load() { this.api.maintenance(this.includeCompleted()).subscribe({ next: r => this.rows.set(r), error: e => this.ui.error(e) }); }
  async schedule() {
    if (await firstValueFrom(this.dialog.open(MaintenanceDialog, { width: '520px', maxWidth: '95vw' }).afterClosed())) { this.ui.ok('Maintenance scheduled'); this.load(); }
  }
  async cancel(w: MaintenanceWindow) {
    const ok = await this.ui.confirm({ title: 'Cancel maintenance', message: `Cancel the window for ${w.server} starting ${new Date(w.startsAt).toLocaleString()}?`, confirmText: 'Cancel window', danger: true });
    if (ok && await this.ui.run(this.api.cancelMaintenance(w.id), 'Maintenance cancelled') !== undefined) this.load();
  }
}
