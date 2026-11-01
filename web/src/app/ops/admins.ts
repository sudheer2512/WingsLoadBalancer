import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { TabDef, Tabs } from '../shared/tabs';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, errorList, errorMessage } from '../core/auth.service';
import { Administrator, PermissionInfo, Role } from '../core/models';
import { UiService } from '../core/ui.service';
import { AgoPipe, HumanizePipe, StatusChip } from '../shared/display';

@Component({
  selector: 'app-admin-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ admin ? 'Edit administrator · ' + admin.username : 'New administrator' }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full"><mat-label>Username</mat-label><input matInput [(ngModel)]="username" [disabled]="!!admin" required maxlength="100" autocomplete="off" /></mat-form-field>
      <mat-form-field class="full"><mat-label>Display name</mat-label><input matInput [(ngModel)]="displayName" required maxlength="200" /></mat-form-field>
      <mat-form-field class="full"><mat-label>E-mail</mat-label><input matInput type="email" [(ngModel)]="email" /></mat-form-field>
      <mat-form-field class="full"><mat-label>{{ admin ? 'New password (leave empty to keep)' : 'Initial password' }}</mat-label>
        <input matInput type="password" [(ngModel)]="password" autocomplete="new-password" [required]="!admin" /><mat-hint>At least 12 characters.</mat-hint></mat-form-field>
      <mat-form-field class="full"><mat-label>Status</mat-label>
        <mat-select [(ngModel)]="status" [disabled]="isSelf"><mat-option value="Active">Active</mat-option><mat-option value="Disabled">Disabled</mat-option></mat-select>
        @if (isSelf) { <mat-hint>You cannot disable your own account.</mat-hint> }</mat-form-field>
      <h3>Roles</h3>
      @for (r of data.roles; track r.id) {
        <div><mat-checkbox [checked]="roles.has(r.name)" (change)="toggle(r.name, $event.checked)">{{ r.name | humanize }}</mat-checkbox>
          <span class="muted small"> {{ r.permissions.length }} permission(s)</span></div>
      }
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !username.trim() || !displayName.trim() || (!admin && password.length < 12) || (password.length > 0 && password.length < 12)" (click)="save()">Save</button></mat-dialog-actions>`,
})
export class AdminDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<AdminDialog, boolean>);
  data = inject<{ admin: Administrator | null; roles: Role[]; me: string }>(MAT_DIALOG_DATA);
  admin = this.data.admin;
  isSelf = this.admin?.username === this.data.me;
  username = this.admin?.username ?? ''; displayName = this.admin?.displayName ?? ''; email = this.admin?.email ?? ''; password = '';
  status = this.admin?.status ?? 'Active';
  roles = new Set(this.admin?.roles ?? []);
  busy = signal(false);
  errors = signal<string[]>([]);
  toggle(r: string, on: boolean) { if (on) this.roles.add(r); else this.roles.delete(r); }
  async save() {
    this.busy.set(true); this.errors.set([]);
    try {
      await firstValueFrom(this.api.saveAdmin(this.admin?.id ?? null, { username: this.username.trim(), displayName: this.displayName.trim(), email: this.email.trim() || null,
        password: this.password || null, status: this.status, roles: [...this.roles] }), { defaultValue: null });
      this.password = '';
      this.ref.close(true);
    } catch (e) {
      const l = errorList(e);
      this.errors.set(l.length ? l : [(e as { error?: { error?: string } }).error?.error === 'duplicate_username' ? 'That username is already taken.' : errorMessage(e)]);
    } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-admins',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Tabs, MatButtonModule, MatIconModule, StatusChip, AgoPipe, HumanizePipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Administrators</h1><span class="spacer"></span>
        <button mat-flat-button (click)="edit(null)"><mat-icon>person_add</mat-icon>New administrator</button></div>
      <app-tabs [tabs]="tabs()" [(active)]="tab" >
          @switch (tab()) {
        @case ('admins') {
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Username</th><th>Display name</th><th>E-mail</th><th>Status</th><th>MFA</th><th>Roles</th><th>Last sign-in</th><th></th></tr></thead>
            <tbody>@for (a of admins(); track a.id) {
              <tr><td><b>{{ a.username }}</b></td><td>{{ a.displayName }}</td><td>{{ a.email }}</td>
                <td><app-status [value]="a.status" />@if (isLocked(a)) { <app-status value="Warning" label="Locked out" /> }</td>
                <td><app-status [value]="a.mfaEnabled ? 'Enabled' : 'None'" [label]="a.mfaEnabled ? 'On' : 'Off'" /></td>
                <td>@for (r of a.roles; track r) { <span class="tag">{{ r | humanize }}</span> }</td><td>{{ a.lastLoginAt | ago }}</td>
                <td><button mat-button (click)="edit(a)">Edit</button></td></tr>
            } @empty { <tr><td colspan="8" class="empty">No administrators.</td></tr> }</tbody></table></div>
        }
        @case ('roles') {
          <div class="cards">
            @for (r of roles(); track r.id) {
              <div class="panel"><div class="panel-head"><h2>{{ r.name | humanize }}</h2>@if (r.isSystem) { <span class="tag">Built-in</span> }</div>
                <p class="muted small">{{ r.description }}</p>
                @for (p of r.permissions; track p) { <span class="tag" [title]="describe(p)">{{ p }}</span> }</div>
            }
          </div>
          <p class="muted small">Roles are read-only in this version of the API.</p>
        }
        @case ('catalog') {
          <table class="data"><thead><tr><th>Permission</th><th>Description</th><th>Granted by roles</th></tr></thead>
            <tbody>@for (p of catalog(); track p.code) {
              <tr><td class="mono">{{ p.code }}</td><td>{{ p.description }}</td><td>@for (r of rolesWith(p.code); track r) { <span class="tag">{{ r | humanize }}</span> }</td></tr>
            }</tbody></table>
        }
      }
        </app-tabs>
    </div>`,
  styles: `.cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 12px; } .cards .panel + .panel { margin-top: 0; }`,
})
export class Admins implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  private auth = inject(AuthService);
  admins = signal<Administrator[]>([]);
  tab = signal('admins');
  tabs = signal<TabDef[]>([{ key: 'admins', label: 'Administrators' }, { key: 'roles', label: 'Roles' }, { key: 'catalog', label: 'Permission catalogue' }]);
  roles = signal<Role[]>([]);
  catalog = signal<PermissionInfo[]>([]);
  ngOnInit() { this.load(); }
  load() {
    this.api.admins().subscribe({ next: a => this.admins.set(a), error: e => this.ui.error(e) });
    this.api.roles().subscribe({ next: r => this.roles.set(r), error: e => this.ui.error(e) });
    this.api.permissionCatalog().subscribe({ next: c => this.catalog.set(c), error: e => this.ui.error(e) });
  }
  isLocked(a: Administrator) { return !!a.lockoutEndAt && new Date(a.lockoutEndAt).getTime() > Date.now(); }
  describe(code: string) { return this.catalog().find(c => c.code === code)?.description ?? code; }
  rolesWith(code: string) { return this.roles().filter(r => r.permissions.includes(code)).map(r => r.name); }
  async edit(a: Administrator | null) {
    const ok = await firstValueFrom(this.dialog.open(AdminDialog, { data: { admin: a, roles: this.roles(), me: this.auth.me().username }, width: '520px', maxWidth: '95vw' }).afterClosed());
    if (ok) { this.ui.ok('Administrator saved'); this.load(); }
  }
}
