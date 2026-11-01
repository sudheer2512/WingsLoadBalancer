import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime, firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P } from '../core/auth.service';
import { ActiveSession } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { AgoPipe, Grid } from '../shared/display';

@Component({
  selector: 'app-sessions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatTableModule, MatSortModule, MatPaginatorModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatButtonModule, MatIconModule, MatProgressBarModule, AgoPipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Active sessions</h1><span class="muted">{{ grid.count() }}</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <div class="filters">
        <mat-form-field class="wide"><mat-label>Company</mat-label>
          <mat-select [ngModel]="companyId()" (ngModelChange)="companyId.set($event); load()"><mat-option value="">All companies</mat-option>
            @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field class="wide"><mat-label>Server</mat-label>
          <mat-select [ngModel]="serverId()" (ngModelChange)="serverId.set($event); load()"><mat-option value="">All servers</mat-option>
            @for (s of lookups.servers(); track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Username contains</mat-label><input matInput [ngModel]="username()" (ngModelChange)="username.set($event); typed$.next()" /></mat-form-field>
        <mat-form-field><mat-label>Filter results</mat-label><mat-icon matPrefix>search</mat-icon><input matInput [ngModel]="grid.filter()" (ngModelChange)="grid.setFilter($event)" /></mat-form-field>
      </div>
      @if (grid.loading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table mat-table [dataSource]="grid.view()" matSort (matSortChange)="grid.sortBy($event)" aria-label="Active sessions">
          <ng-container matColumnDef="company"><th mat-header-cell *matHeaderCellDef mat-sort-header>Company</th>
            <td mat-cell *matCellDef="let s"><a [routerLink]="['/companies', s.companyId]">{{ s.company }}</a></td></ng-container>
          <ng-container matColumnDef="username"><th mat-header-cell *matHeaderCellDef mat-sort-header>User</th><td mat-cell *matCellDef="let s">{{ s.username }}</td></ng-container>
          <ng-container matColumnDef="application"><th mat-header-cell *matHeaderCellDef mat-sort-header>Application</th><td mat-cell *matCellDef="let s">{{ s.application }}</td></ng-container>
          <ng-container matColumnDef="server"><th mat-header-cell *matHeaderCellDef mat-sort-header>Server</th>
            <td mat-cell *matCellDef="let s">@if (s.serverId) { <a [routerLink]="['/servers', s.serverId]">{{ s.server }}</a> } @else { — }</td></ng-container>
          <ng-container matColumnDef="startedAt"><th mat-header-cell *matHeaderCellDef mat-sort-header>Started</th><td mat-cell *matCellDef="let s" class="nowrap">{{ s.startedAt | date: 'short' }}</td></ng-container>
          <ng-container matColumnDef="lastSeenAt"><th mat-header-cell *matHeaderCellDef mat-sort-header>Last seen</th><td mat-cell *matCellDef="let s">{{ s.lastSeenAt | ago }}</td></ng-container>
          <ng-container matColumnDef="gatewayInstance"><th mat-header-cell *matHeaderCellDef mat-sort-header>Gateway</th><td mat-cell *matCellDef="let s">{{ s.gatewayInstance }}</td></ng-container>
          <ng-container matColumnDef="clientAddress"><th mat-header-cell *matHeaderCellDef>Client</th>
            <td mat-cell *matCellDef="let s"><span class="mono">{{ s.clientAddress }}</span><div class="muted small ua" [title]="s.userAgent">{{ s.userAgent }}</div></td></ng-container>
          <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
            <td mat-cell *matCellDef="let s">@if (canRevoke) { <button mat-button class="danger" (click)="revoke(s)">Revoke</button> }</td></ng-container>
          <tr mat-header-row *matHeaderRowDef="cols; sticky: true"></tr>
          <tr mat-row *matRowDef="let s; columns: cols"></tr>
          <tr class="mat-row" *matNoDataRow><td class="empty" [attr.colspan]="cols.length">No active sessions.</td></tr>
        </table>
      </div>
      <mat-paginator [length]="grid.count()" [pageIndex]="grid.pageIndex()" [pageSize]="grid.pageSize()" [pageSizeOptions]="[25, 50, 100, 250]" (page)="grid.pageBy($event)" />
    </div>`,
  styles: `.nowrap { white-space: nowrap; } .ua { max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }`,
})
export class Sessions implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  lookups = inject(LookupService);
  canRevoke = inject(AuthService).can(P.sessionsRevoke);
  cols = ['company', 'username', 'application', 'server', 'startedAt', 'lastSeenAt', 'gatewayInstance', 'clientAddress', 'actions'];
  companyId = signal(''); serverId = signal(''); username = signal('');
  grid = new Grid<ActiveSession>(s => `${s.company} ${s.username} ${s.server ?? ''} ${s.application ?? ''} ${s.clientAddress ?? ''}`, undefined, 50);
  typed$ = new Subject<void>();
  constructor() { this.typed$.pipe(debounceTime(300), takeUntilDestroyed()).subscribe(() => this.load()); }
  ngOnInit() { this.load(); this.lookups.ensure(['companies', 'servers']); }
  async load() {
    this.grid.loading.set(true);
    try { this.grid.set(await firstValueFrom(this.api.sessions({ companyId: this.companyId(), serverId: this.serverId(), username: this.username(), take: 2000 }))); }
    catch (e) { this.ui.error(e); } finally { this.grid.loading.set(false); }
  }
  async revoke(s: ActiveSession) {
    const ok = await this.ui.confirm({ title: 'Revoke session', message: `Sign ${s.company}/${s.username} out of this session (all applications sharing it)?`, confirmText: 'Revoke', danger: true, reason: true });
    if (ok && await this.ui.run(this.api.revokeSession(s.sessionKey, ok.reason), 'Session revoked') !== undefined) this.load();
  }
}
