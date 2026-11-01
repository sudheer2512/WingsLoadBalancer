import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { errorList, errorMessage } from '../core/auth.service';
import { ImportResult } from '../core/models';
import { UiService } from '../core/ui.service';
import { StatusChip } from '../shared/display';

@Component({
  selector: 'app-bulk-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatProgressBarModule, MatCheckboxModule, StatusChip],
  template: `
    <div class="page">
      <div class="page-head"><a class="crumb" routerLink="/users">User assignments</a><span class="muted">/</span><h1>Bulk upload</h1></div>
      <div class="panel">
        <h2>1. Prepare the file</h2>
        <p>Upload a CSV or Excel (.xlsx) file with a header row. Columns: <code class="mono">Company, Username, Action (Assign|Remove), PrimaryServer, SecondaryServer, FailoverPolicy, Application, Environment, EffectiveFrom, EffectiveTo, Reason</code>.
          Servers, policies and applications are referenced by name/code. Up to 5,000 rows per file.</p>
        <a mat-stroked-button [href]="api.bulkTemplateUrl" download="user-assignments-template.csv"><mat-icon>download</mat-icon>Download template</a>
      </div>
      <div class="panel">
        <h2>2. Upload and validate (dry run)</h2>
        <div class="wrap-row">
          <label class="file">
            <input type="file" accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" (change)="pick($event)" />
          </label>
          <button mat-flat-button [disabled]="!file() || busy()" (click)="run(true)"><mat-icon>rule</mat-icon>Validate</button>
        </div>
        @if (file(); as f) { <p class="muted small">{{ f.name }} · {{ (f.size / 1024).toFixed(1) }} KB</p> }
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
      </div>
      @if (result(); as r) {
        <div class="panel">
          <div class="panel-head">
            <h2>{{ r.dryRun ? 'Dry-run results' : 'Import results' }}</h2>
            <app-status [value]="r.valid ? 'Valid' : 'Invalid'" [label]="r.valid ? 'All rows valid' : invalidCount() + ' invalid row(s)'" />
            <span class="muted">{{ r.total }} row(s){{ r.dryRun ? '' : ' · ' + r.applied + ' applied' }}</span>
            <span class="spacer"></span>
            <mat-checkbox [(ngModel)]="onlyErrors">Show only rows with errors</mat-checkbox>
          </div>
          @if (r.dryRun) {
            <div class="callout" [class.warn]="!r.valid">
              @if (r.valid) { All rows are valid. Committing applies every row in one transaction. }
              @else { Fix the rows with errors and upload the file again. Commit stays disabled until every row is valid. }
            </div>
            <button mat-flat-button [disabled]="!r.valid || busy() || !r.total" (click)="commit()"><mat-icon>task_alt</mat-icon>Commit {{ r.total }} row(s)</button>
          } @else {
            <div class="callout good">Import committed: {{ r.applied }} row(s) applied.</div>
          }
          <div class="table-wrap results"><table class="data">
            <thead><tr><th class="num">Row</th><th>Company</th><th>Username</th><th>Action</th><th>Result</th><th>Errors / summary</th></tr></thead>
            <tbody>@for (row of visibleRows(); track row.row) {
              <tr><td class="num">{{ row.row }}</td><td>{{ row.company }}</td><td>{{ row.username }}</td><td>{{ row.action }}</td>
                <td><app-status [value]="row.valid ? 'Valid' : 'Invalid'" /></td>
                <td>@if (row.errors.length) { <ul class="errs">@for (e of row.errors; track e) { <li>{{ e }}</li> }</ul> } @else { <span class="muted">{{ row.summary }}</span> }</td></tr>
            } @empty { <tr><td colspan="6" class="empty">No rows to show.</td></tr> }</tbody>
          </table></div>
        </div>
      }
    </div>`,
  styles: `.results { max-height: 520px; margin-top: 8px; } .errs { margin: 0; padding-left: 16px; color: var(--bad); } .file input { font: inherit; }`,
})
export class BulkImport {
  api = inject(ApiService);
  private ui = inject(UiService);
  file = signal<File | null>(null);
  result = signal<ImportResult | null>(null);
  busy = signal(false);
  errors = signal<string[]>([]);
  onlyErrors = false;
  invalidCount = computed(() => this.result()?.rows.filter(r => !r.valid).length ?? 0);
  visibleRows = () => (this.result()?.rows ?? []).filter(r => !this.onlyErrors || !r.valid);

  pick(e: Event) {
    const f = (e.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(f);
    this.result.set(null);
    this.errors.set([]);
  }

  async run(dryRun: boolean) {
    const f = this.file();
    if (!f) return;
    this.busy.set(true); this.errors.set([]);
    try {
      this.result.set(await firstValueFrom(this.api.bulkImport(f, dryRun)));
      if (!dryRun) this.ui.ok('Import committed');
    } catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); }
    finally { this.busy.set(false); }
  }

  async commit() {
    const ok = await this.ui.confirm({ title: 'Commit import', message: `Apply all ${this.result()?.total} row(s) now? Assignments take effect immediately (or at their EffectiveFrom).`, confirmText: 'Commit' });
    if (ok) await this.run(false);
  }
}
