import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService, urlWith } from '../core/api.service';
import { AuditRow } from '../core/models';
import { LookupService, UiService, fromLocalInput } from '../core/ui.service';
import { AuditTable } from '../shared/audit-table';
import { NumPipe } from '../shared/display';

interface AuditFilter { eventType: string; actor: string; companyId: string; serverId: string; userId: string; from: string; to: string; search: string; }

@Component({
  selector: 'app-audit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatIconModule, MatPaginatorModule, MatProgressBarModule, AuditTable, NumPipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Audit log</h1><span class="muted">{{ total() | n }} entr{{ total() === 1 ? 'y' : 'ies' }}</span><span class="spacer"></span>
        <a mat-stroked-button [href]="exportUrl('csv')" download><mat-icon>download</mat-icon>CSV</a>
        <a mat-stroked-button [href]="exportUrl('xlsx')" download><mat-icon>download</mat-icon>Excel</a></div>
      <form class="filters" (ngSubmit)="first()">
        <mat-form-field class="wide"><mat-label>Event type</mat-label>
          <mat-select name="et" [(ngModel)]="f.eventType" (ngModelChange)="first()"><mat-option value="">All events</mat-option>
            @for (t of eventTypes(); track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Actor contains</mat-label><input matInput name="actor" [(ngModel)]="f.actor" /></mat-form-field>
        <mat-form-field><mat-label>Company</mat-label>
          <mat-select name="co" [(ngModel)]="f.companyId" (ngModelChange)="first()"><mat-option value="">All</mat-option>
            @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Server</mat-label>
          <mat-select name="sv" [(ngModel)]="f.serverId" (ngModelChange)="first()"><mat-option value="">All</mat-option>
            @for (s of lookups.servers(); track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>User ID</mat-label><input matInput name="uid" [(ngModel)]="f.userId" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>From</mat-label><input matInput name="from" type="datetime-local" [(ngModel)]="f.from" (ngModelChange)="first()" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>To</mat-label><input matInput name="to" type="datetime-local" [(ngModel)]="f.to" (ngModelChange)="first()" /></mat-form-field>
        <mat-form-field class="wide"><mat-label>Search reason / new value</mat-label><input matInput name="q" [(ngModel)]="f.search" /></mat-form-field>
        <button mat-flat-button><mat-icon>search</mat-icon>Apply</button>
        <button mat-button type="button" (click)="clear()"><mat-icon>filter_alt_off</mat-icon>Clear</button>
      </form>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      <app-audit-table [rows]="rows()" />
      <mat-paginator [length]="total()" [pageIndex]="page()" [pageSize]="size()" [pageSizeOptions]="[50, 100, 250, 500]" (page)="paged($event)" />
    </div>`,
})
export class Audit implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private route = inject(ActivatedRoute);
  lookups = inject(LookupService);
  f: AuditFilter = this.blank();
  eventTypes = signal<string[]>([]);
  rows = signal<AuditRow[]>([]);
  total = signal(0);
  page = signal(0); size = signal(100);
  loading = signal(false);
  private version = signal(0);
  private query = computed(() => {
    this.version();
    return { eventType: this.f.eventType, actor: this.f.actor.trim(), companyId: this.f.companyId, serverId: this.f.serverId, userId: this.f.userId.trim(),
      from: fromLocalInput(this.f.from) ?? '', to: fromLocalInput(this.f.to) ?? '', search: this.f.search.trim() };
  });

  private blank(): AuditFilter { return { eventType: '', actor: '', companyId: '', serverId: '', userId: '', from: '', to: '', search: '' }; }

  ngOnInit() {
    const q = this.route.snapshot.queryParamMap;
    this.f.companyId = q.get('companyId') ?? '';
    this.f.serverId = q.get('serverId') ?? '';
    this.lookups.ensure(['companies', 'servers']);
    this.api.auditEventTypes().subscribe({ next: t => this.eventTypes.set(t), error: () => undefined });
    this.load();
  }

  exportUrl(format: 'csv' | 'xlsx') { return urlWith('/api/audit', { ...this.query(), format }); }
  clear() { this.f = this.blank(); this.first(); }
  first() { this.page.set(0); this.version.update(v => v + 1); this.load(); }
  paged(e: PageEvent) { this.page.set(e.pageIndex); this.size.set(e.pageSize); this.load(); }

  async load() {
    this.loading.set(true);
    try {
      const r = await firstValueFrom(this.api.audit({ ...this.query(), skip: this.page() * this.size(), take: this.size() }));
      this.rows.set(r.rows); this.total.set(r.total);
    } catch (e) { this.ui.error(e); } finally { this.loading.set(false); }
  }
}
