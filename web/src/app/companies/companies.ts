import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSortModule } from '@angular/material/sort';
import { MatTableModule } from '@angular/material/table';
import { Router, RouterLink } from '@angular/router';
import { Subject, debounceTime, firstValueFrom } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from '../core/api.service';
import { AuthService, P } from '../core/auth.service';
import { COMPANY_STATUSES, CompanyListItem } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { Grid, HumanizePipe, StatusChip } from '../shared/display';
import { CompanyDialog } from './company-form';

@Component({
  selector: 'app-companies',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatTableModule, MatSortModule, MatPaginatorModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatButtonModule, MatIconModule, MatProgressBarModule, StatusChip, HumanizePipe],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Companies</h1><span class="muted">{{ grid.count() }}</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (auth.can(P.companiesWrite)) { <button mat-flat-button (click)="add()"><mat-icon>add</mat-icon>Add company</button> }
      </div>
      <div class="filters">
        <mat-form-field class="wide"><mat-label>Search code, name or alias</mat-label><mat-icon matPrefix>search</mat-icon>
          <input matInput [ngModel]="search()" (ngModelChange)="search.set($event); search$.next()" /></mat-form-field>
        <mat-form-field><mat-label>Status</mat-label>
          <mat-select [ngModel]="status()" (ngModelChange)="status.set($event); load()"><mat-option value="">Any status</mat-option>
            @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Routing mode</mat-label>
          <mat-select [ngModel]="mode()" (ngModelChange)="mode.set($event); apply()"><mat-option value="">Any mode</mat-option>
            @for (m of ['Dedicated', 'SharedPool', 'Cluster']; track m) { <mat-option [value]="m">{{ m | humanize }}</mat-option> }</mat-select></mat-form-field>
      </div>
      @if (grid.loading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table mat-table [dataSource]="grid.view()" matSort (matSortChange)="grid.sortBy($event)" aria-label="Companies">
          <ng-container matColumnDef="code"><th mat-header-cell *matHeaderCellDef mat-sort-header>Code</th>
            <td mat-cell *matCellDef="let c"><a [routerLink]="['/companies', c.id]" (click)="$event.stopPropagation()"><b>{{ c.code }}</b></a>
              @if (c.aliases.length) { <div class="muted small">{{ c.aliases.join(', ') }}</div> }</td></ng-container>
          <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef mat-sort-header>Name</th><td mat-cell *matCellDef="let c">{{ c.name }}</td></ng-container>
          <ng-container matColumnDef="servicePlan"><th mat-header-cell *matHeaderCellDef mat-sort-header>Plan</th><td mat-cell *matCellDef="let c">{{ c.servicePlan ?? '—' }}</td></ng-container>
          <ng-container matColumnDef="routingMode"><th mat-header-cell *matHeaderCellDef mat-sort-header>Routing mode</th><td mat-cell *matCellDef="let c">{{ c.routingMode | humanize }}</td></ng-container>
          <ng-container matColumnDef="primaryServer"><th mat-header-cell *matHeaderCellDef mat-sort-header>Primary</th><td mat-cell *matCellDef="let c">{{ c.primaryServer ?? '—' }}</td></ng-container>
          <ng-container matColumnDef="secondaryServer"><th mat-header-cell *matHeaderCellDef mat-sort-header>Secondary</th><td mat-cell *matCellDef="let c">{{ c.secondaryServer ?? '—' }}</td></ng-container>
          <ng-container matColumnDef="sharedPool"><th mat-header-cell *matHeaderCellDef mat-sort-header>Shared pool</th><td mat-cell *matCellDef="let c">{{ c.sharedPool ?? '—' }}</td></ng-container>
          <ng-container matColumnDef="failoverPolicy"><th mat-header-cell *matHeaderCellDef mat-sort-header>Failover policy</th><td mat-cell *matCellDef="let c" class="small">{{ c.failoverPolicy ?? '—' }}</td></ng-container>
          <ng-container matColumnDef="users"><th mat-header-cell *matHeaderCellDef mat-sort-header class="num">Users</th><td mat-cell *matCellDef="let c" class="num">{{ c.users }}</td></ng-container>
          <ng-container matColumnDef="activeUsers"><th mat-header-cell *matHeaderCellDef mat-sort-header class="num">Active users</th><td mat-cell *matCellDef="let c" class="num">{{ c.activeUsers }}</td></ng-container>
          <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef mat-sort-header>Status</th><td mat-cell *matCellDef="let c"><app-status [value]="c.status" /></td></ng-container>
          <tr mat-header-row *matHeaderRowDef="cols; sticky: true"></tr>
          <tr mat-row *matRowDef="let c; columns: cols" class="clickable" (click)="open(c)"></tr>
          <tr class="mat-row" *matNoDataRow><td class="empty" [attr.colspan]="cols.length">No companies match.</td></tr>
        </table>
      </div>
      <mat-paginator [length]="grid.count()" [pageIndex]="grid.pageIndex()" [pageSize]="grid.pageSize()" [pageSizeOptions]="[25, 50, 100]" (page)="grid.pageBy($event)" />
    </div>`,
})
export class Companies implements OnInit {
  readonly P = P;
  private api = inject(ApiService);
  private ui = inject(UiService);
  private lookups = inject(LookupService);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  auth = inject(AuthService);
  statuses = COMPANY_STATUSES;
  cols = ['code', 'name', 'servicePlan', 'routingMode', 'primaryServer', 'secondaryServer', 'sharedPool', 'failoverPolicy', 'users', 'activeUsers', 'status'];
  search = signal(''); status = signal(''); mode = signal('');
  all = signal<CompanyListItem[]>([]);
  grid = new Grid<CompanyListItem>(c => c.code);
  search$ = new Subject<void>();

  constructor() { this.search$.pipe(debounceTime(250), takeUntilDestroyed()).subscribe(() => this.load()); }
  ngOnInit() { this.load(); }

  async load() {
    this.grid.loading.set(true);
    try {
      this.all.set(await firstValueFrom(this.api.companies({ search: this.search(), status: this.status() })));
      if (!this.search() && !this.status()) this.lookups.companies.set(this.all());
      this.apply();
    } catch (e) { this.ui.error(e); } finally { this.grid.loading.set(false); }
  }
  apply() { this.grid.set(this.all().filter(c => !this.mode() || c.routingMode === this.mode())); }
  open(c: CompanyListItem) { this.router.navigate(['/companies', c.id]); }

  async add() {
    const id = await firstValueFrom(this.dialog.open(CompanyDialog, { width: '820px', maxWidth: '96vw' }).afterClosed());
    if (id) { this.ui.ok('Company added'); this.lookups.invalidate('companies'); this.router.navigate(['/companies', id]); }
  }
}
