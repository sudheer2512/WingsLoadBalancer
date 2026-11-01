import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P } from '../core/auth.service';
import { ENVIRONMENTS, HEALTH_STATES, SERVER_TYPES, Server } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { AgoPipe, Grid, Meter, NumPipe, StatusChip } from '../shared/display';
import { AddServerDialog } from './server-form';

const STATUSES = ['Online', 'Degraded', 'Offline', 'Maintenance', 'Unknown', 'Retired'];

@Component({
  selector: 'app-servers',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatTableModule, MatSortModule, MatPaginatorModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatButtonModule, MatIconModule, MatCheckboxModule, MatProgressBarModule, StatusChip, Meter, NumPipe, AgoPipe],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Servers</h1><span class="muted">{{ grid.count() }} of {{ all().length }}</span>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (auth.can(P.serversWrite)) { <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon>Add server</button> }
      </div>
      <div class="filters">
        <mat-form-field class="wide"><mat-label>Search name, host, region, pool</mat-label><mat-icon matPrefix>search</mat-icon>
          <input matInput [ngModel]="grid.filter()" (ngModelChange)="grid.setFilter($event)" /></mat-form-field>
        <mat-form-field><mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); apply()"><mat-option value="">Any status</mat-option>
            @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Health</mat-label>
          <mat-select [ngModel]="health()" (ngModelChange)="health.set($event); apply()"><mat-option value="">Any health</mat-option>
            @for (s of healthStates; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [ngModel]="env()" (ngModelChange)="env.set($event); apply()"><mat-option value="">All environments</mat-option>
            @for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Type</mat-label>
          <mat-select [ngModel]="type()" (ngModelChange)="type.set($event); apply()"><mat-option value="">All types</mat-option>
            @for (t of types; track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select></mat-form-field>
        <mat-checkbox [ngModel]="includeRetired()" (ngModelChange)="includeRetired.set($event); load()">Include retired</mat-checkbox>
      </div>
      @if (grid.loading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table mat-table [dataSource]="grid.view()" matSort (matSortChange)="grid.sortBy($event)" aria-label="Servers">
          <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef mat-sort-header>Server</th>
            <td mat-cell *matCellDef="let s"><a class="nm" [routerLink]="['/servers', s.id]" (click)="$event.stopPropagation()"><b>{{ s.name }}</b></a>
              <div class="muted small">{{ s.hostname }}</div></td></ng-container>
          <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th>
            <td mat-cell *matCellDef="let s"><app-status [value]="s.status" [title]="s.lastReason" /></td></ng-container>
          <ng-container matColumnDef="health"><th mat-header-cell *matHeaderCellDef mat-sort-header>Health</th>
            <td mat-cell *matCellDef="let s"><app-status [value]="s.health" [title]="s.lastReason" />
              @if (s.awaitingManualRestore) { <div class="small warnText">Awaiting restore</div> }</td></ng-container>
          <ng-container matColumnDef="adminState"><th mat-header-cell *matHeaderCellDef mat-sort-header>Admin state</th>
            <td mat-cell *matCellDef="let s">{{ s.adminState }}</td></ng-container>
          <ng-container matColumnDef="environment"><th mat-header-cell *matHeaderCellDef mat-sort-header>Environment</th>
            <td mat-cell *matCellDef="let s">{{ s.environment }}<div class="muted small">{{ s.region }}</div></td></ng-container>
          <ng-container matColumnDef="serverType"><th mat-header-cell *matHeaderCellDef mat-sort-header>Type</th>
            <td mat-cell *matCellDef="let s">{{ s.serverType }}</td></ng-container>
          <ng-container matColumnDef="cpuPercent"><th mat-header-cell *matHeaderCellDef mat-sort-header>CPU</th>
            <td mat-cell *matCellDef="let s"><app-meter [value]="s.cpuPercent" label="CPU" /></td></ng-container>
          <ng-container matColumnDef="memoryPercent"><th mat-header-cell *matHeaderCellDef mat-sort-header>Memory</th>
            <td mat-cell *matCellDef="let s"><app-meter [value]="s.memoryPercent" label="Memory" /></td></ng-container>
          <ng-container matColumnDef="responseTimeMs"><th mat-header-cell *matHeaderCellDef mat-sort-header class="num">Response</th>
            <td mat-cell *matCellDef="let s" class="num">{{ s.responseTimeMs | n: 1 : ' ms' }}</td></ng-container>
          <ng-container matColumnDef="activeSessions"><th mat-header-cell *matHeaderCellDef mat-sort-header class="num">Sessions</th>
            <td mat-cell *matCellDef="let s" class="num">{{ s.activeSessions | n }} / {{ s.maxCapacity || '∞' }}</td></ng-container>
          <ng-container matColumnDef="applicationVersion"><th mat-header-cell *matHeaderCellDef mat-sort-header>Version</th>
            <td mat-cell *matCellDef="let s">{{ s.applicationVersion ?? '—' }}
              @if (s.reportedVersion && s.reportedVersion !== s.applicationVersion) { <div class="small warnText" title="Version reported by the server">reports {{ s.reportedVersion }}</div> }</td></ng-container>
          <ng-container matColumnDef="pools"><th mat-header-cell *matHeaderCellDef>Pools</th>
            <td mat-cell *matCellDef="let s">@for (p of s.pools; track p) { <span class="tag">{{ p }}</span> }</td></ng-container>
          <ng-container matColumnDef="lastCheckAt"><th mat-header-cell *matHeaderCellDef mat-sort-header>Last check</th>
            <td mat-cell *matCellDef="let s" class="muted small">{{ s.lastCheckAt | ago }}</td></ng-container>
          <tr mat-header-row *matHeaderRowDef="cols; sticky: true"></tr>
          <tr mat-row *matRowDef="let s; columns: cols" class="clickable" (click)="open(s)"></tr>
          <tr class="mat-row" *matNoDataRow><td class="empty" [attr.colspan]="cols.length">No servers match the filters.</td></tr>
        </table>
      </div>
      <mat-paginator [length]="grid.count()" [pageIndex]="grid.pageIndex()" [pageSize]="grid.pageSize()" [pageSizeOptions]="[25, 50, 100]"
                     (page)="grid.pageBy($event)" aria-label="Server pages" />
    </div>`,
  styles: `.warnText { color: var(--warn); } .nm { white-space: nowrap; }`,
})
export class Servers implements OnInit {
  readonly P = P;
  private api = inject(ApiService);
  private ui = inject(UiService);
  private lookups = inject(LookupService);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  auth = inject(AuthService);
  statuses = STATUSES; healthStates = HEALTH_STATES; envs = ENVIRONMENTS; types = SERVER_TYPES;
  cols = ['name', 'status', 'health', 'adminState', 'environment', 'serverType', 'cpuPercent', 'memoryPercent', 'responseTimeMs', 'activeSessions',
    'applicationVersion', 'pools', 'lastCheckAt'];

  all = signal<Server[]>([]);
  status = signal(''); health = signal(''); env = signal(''); type = signal('');
  includeRetired = signal(false);
  grid = new Grid<Server>(s => [s.name, s.hostname, s.region, s.ipAddress, s.notes, ...s.pools].join(' '));
  private filtered = computed(() => this.all().filter(s =>
    (!this.status() || s.status === this.status()) && (!this.health() || s.health === this.health())
    && (!this.env() || s.environment === this.env()) && (!this.type() || s.serverType === this.type())));

  ngOnInit() {
    const q = this.route.snapshot.queryParamMap;
    this.status.set(q.get('status') ?? '');
    this.load();
  }

  async load() {
    this.grid.loading.set(true);
    try {
      this.all.set(await firstValueFrom(this.api.servers({ includeRetired: this.includeRetired() })));
      this.apply();
      this.lookups.servers.set(this.all().filter(s => s.adminState !== 'Retired'));
    } catch (e) { this.ui.error(e); } finally { this.grid.loading.set(false); }
  }

  apply() { this.grid.set(this.filtered()); }
  open(s: Server) { this.router.navigate(['/servers', s.id]); }

  async add() {
    const id = await firstValueFrom(this.dialog.open(AddServerDialog, { width: '820px', maxWidth: '96vw' }).afterClosed());
    if (id) {
      this.ui.ok('Server added');
      this.lookups.invalidate('servers');
      this.router.navigate(['/servers', id]);
    }
  }
}
