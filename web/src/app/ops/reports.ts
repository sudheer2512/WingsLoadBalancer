import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatPaginatorModule } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSortModule, Sort } from '@angular/material/sort';
import { firstValueFrom } from 'rxjs';
import { ApiService, urlWith } from '../core/api.service';
import { ReportDefinition, ReportTable } from '../core/models';
import { UiService, fromLocalInput, toLocalInput } from '../core/ui.service';
import { Grid } from '../shared/display';

@Component({
  selector: 'app-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatListModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSortModule, MatPaginatorModule],
  template: `
    <div class="page">
      <div class="page-head"><h1>Reports</h1></div>
      <div class="layout">
        <nav class="panel list" aria-label="Reports">
          @for (r of defs(); track r.name) {
            <button type="button" class="rep" [class.sel]="selected()?.name === r.name" (click)="select(r)">
              <b>{{ r.title }}</b><span class="muted small">{{ r.description }}</span></button>
          }
        </nav>
        <section class="panel">
          @if (selected(); as d) {
            <div class="panel-head"><h2>{{ d.title }}</h2><span class="spacer"></span>
              <a mat-stroked-button [href]="url('csv')" download><mat-icon>download</mat-icon>CSV</a>
              <a mat-stroked-button [href]="url('xlsx')" download><mat-icon>download</mat-icon>Excel</a></div>
            <p class="muted">{{ d.description }}</p>
            <div class="filters">
              <mat-form-field class="wide"><mat-label>From</mat-label><input matInput type="datetime-local" [(ngModel)]="from" /></mat-form-field>
              <mat-form-field class="wide"><mat-label>To</mat-label><input matInput type="datetime-local" [(ngModel)]="to" /></mat-form-field>
              <button mat-flat-button (click)="run()"><mat-icon>play_arrow</mat-icon>Run</button>
              <mat-form-field><mat-label>Filter rows</mat-label><mat-icon matPrefix>search</mat-icon><input matInput [ngModel]="grid.filter()" (ngModelChange)="grid.setFilter($event)" /></mat-form-field>
              <span class="muted small">Default range: the last 7 days. Times are UTC in exports.</span>
            </div>
            @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
            @if (table(); as t) {
              <div class="table-wrap res"><table class="data compact" matSort (matSortChange)="sort($event)">
                <thead><tr>@for (c of t.columns; track $index) { <th [mat-sort-header]="'' + $index">{{ c }}</th> }</tr></thead>
                <tbody>@for (row of grid.view(); track $index) {
                  <tr>@for (v of row; track $index) { <td [class.num]="isNum(v)">{{ fmt(v) }}</td> }</tr>
                } @empty { <tr><td [attr.colspan]="t.columns.length" class="empty">No rows for this period.</td></tr> }</tbody>
              </table></div>
              <mat-paginator [length]="grid.count()" [pageIndex]="grid.pageIndex()" [pageSize]="grid.pageSize()" [pageSizeOptions]="[50, 100, 500]" (page)="grid.pageBy($event)" />
            }
          } @else { <div class="empty">Choose a report.</div> }
        </section>
      </div>
    </div>`,
  styles: `
    .layout { display: grid; grid-template-columns: 300px 1fr; gap: 12px; align-items: start; }
    @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
    .list { padding: 4px; } .rep { display: flex; flex-direction: column; gap: 2px; width: 100%; text-align: left; padding: 8px 10px; border: 0; border-radius: 6px; background: none; color: inherit; font: inherit; cursor: pointer; }
    .rep:hover { background: var(--hover); } .rep.sel { background: var(--selected); }
    .res { max-height: 60vh; }
  `,
})
export class Reports implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  defs = signal<ReportDefinition[]>([]);
  selected = signal<ReportDefinition | null>(null);
  table = signal<ReportTable | null>(null);
  busy = signal(false);
  from = toLocalInput(new Date(Date.now() - 7 * 86400_000).toISOString());
  to = '';
  grid = new Grid<unknown[]>(r => r.join(' '), (r, c) => r[Number(c)], 100);
  private dateRe = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

  ngOnInit() { this.api.reports().subscribe({ next: d => { this.defs.set(d); if (d[0]) this.select(d[0]); }, error: e => this.ui.error(e) }); }
  select(d: ReportDefinition) { this.selected.set(d); this.table.set(null); this.run(); }
  url(format: 'csv' | 'xlsx') { return urlWith(`/api/reports/${encodeURIComponent(this.selected()!.name)}`, { from: fromLocalInput(this.from), to: fromLocalInput(this.to), format }); }
  sort(s: Sort) { this.grid.sortBy(s); }

  async run() {
    const d = this.selected();
    if (!d) return;
    this.busy.set(true);
    try {
      const t = await firstValueFrom(this.api.runReport(d.name, fromLocalInput(this.from) ?? undefined, fromLocalInput(this.to) ?? undefined));
      this.table.set(t);
      this.grid.sortBy({ active: '', direction: '' });
      this.grid.set(t.rows);
    } catch (e) { this.ui.error(e); } finally { this.busy.set(false); }
  }

  isNum(v: unknown) { return typeof v === 'number'; }
  fmt(v: unknown) {
    if (v === null || v === undefined) return '';
    if (typeof v === 'number') return Number.isInteger(v) ? v.toLocaleString() : v.toLocaleString(undefined, { maximumFractionDigits: 2 });
    if (typeof v === 'boolean') return v ? 'Yes' : 'No';
    if (typeof v === 'string' && this.dateRe.test(v)) { const d = new Date(v); return isNaN(d.getTime()) ? v : d.toLocaleString(); }
    return String(v);
  }
}
