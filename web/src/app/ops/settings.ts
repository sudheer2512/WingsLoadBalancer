import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import { Setting } from '../core/models';
import { UiService } from '../core/ui.service';

@Component({
  selector: 'app-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, MatButtonModule, MatIconModule],
  template: `
    <div class="page">
      <div class="page-head"><h1>System settings</h1><span class="spacer"></span>
        @if (canWrite) {
          <button mat-button [disabled]="!dirtyCount()" (click)="reset()">Discard changes</button>
          <button mat-flat-button [disabled]="!dirtyCount() || busy()" (click)="save()"><mat-icon>save</mat-icon>Save {{ dirtyCount() || '' }} change(s)</button>
        }</div>
      @if (!canWrite) { <div class="callout">You can view settings; changing them requires the settings.write permission.</div> }
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
      @for (g of groups(); track g.name) {
        <section class="panel">
          <h2>{{ g.name }}</h2>
          <table class="data"><thead><tr><th>Setting</th><th>Description</th><th class="val">Value</th><th>Last changed</th></tr></thead>
            <tbody>@for (s of g.items; track s.key) {
              <tr [class.dirty]="edits()[s.key] !== undefined && edits()[s.key] !== s.value">
                <td class="mono">{{ s.key }}</td><td>{{ s.description }}</td>
                <td class="val"><input type="number" min="0" step="any" [value]="edits()[s.key] ?? s.value" [disabled]="!canWrite" [attr.aria-label]="s.key"
                  (input)="edit(s.key, $any($event.target).value)" /></td>
                <td class="small muted">{{ s.modifiedAt | date: 'short' }}{{ s.modifiedBy ? ' · ' + s.modifiedBy : '' }}</td></tr>
            }</tbody></table>
        </section>
      } @empty { <div class="empty">No settings.</div> }
    </div>`,
  styles: `
    .val { width: 160px; } .val input { width: 140px; font: inherit; padding: 4px 6px; text-align: right; border: 1px solid var(--border); border-radius: 4px; background: var(--surface); color: var(--text); }
    tr.dirty td { background: var(--warn-bg); }
  `,
})
export class Settings implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  canWrite = inject(AuthService).can(P.settingsWrite);
  settings = signal<Setting[]>([]);
  edits = signal<Record<string, string>>({});
  busy = signal(false);
  errors = signal<string[]>([]);
  groups = computed(() => {
    const map = new Map<string, Setting[]>();
    for (const s of this.settings()) { const g = s.key.includes('.') ? s.key.split('.')[0] : 'General'; map.set(g, [...(map.get(g) ?? []), s]); }
    return [...map.entries()].map(([name, items]) => ({ name, items }));
  });
  dirtyCount = computed(() => this.settings().filter(s => this.edits()[s.key] !== undefined && this.edits()[s.key] !== s.value).length);

  ngOnInit() { this.load(); }
  load() { this.api.settings().subscribe({ next: s => { this.settings.set(s); this.edits.set({}); }, error: e => this.ui.error(e) }); }
  edit(key: string, v: string) { this.edits.update(e => ({ ...e, [key]: v })); }
  reset() { this.edits.set({}); this.errors.set([]); }

  async save() {
    const updates = this.settings().filter(s => this.edits()[s.key] !== undefined && this.edits()[s.key] !== s.value).map(s => ({ key: s.key, value: String(this.edits()[s.key]).trim() }));
    this.busy.set(true); this.errors.set([]);
    try { await firstValueFrom(this.api.updateSettings(updates), { defaultValue: null }); this.ui.ok('Settings saved'); this.load(); }
    catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); } finally { this.busy.set(false); }
  }
}
