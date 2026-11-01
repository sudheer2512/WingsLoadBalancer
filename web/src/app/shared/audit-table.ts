import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { AuditRow } from '../core/models';
import { StatusChip } from './display';

/** Audit entries with expandable previous/new values. */
@Component({
  selector: 'app-audit-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatIconModule, StatusChip],
  template: `
    <div class="table-wrap">
      <table class="data">
        <thead><tr><th></th><th>Time</th><th>Event</th><th>Actor</th><th>Entity</th><th>Change</th><th>Reason</th><th>Severity</th></tr></thead>
        <tbody>
          @for (r of rows(); track r.id) {
            <tr>
              <td><button mat-icon-button class="x" (click)="toggle(r.id)" [attr.aria-label]="'Details for ' + r.eventType">
                <mat-icon>{{ open().has(r.id) ? 'expand_less' : 'expand_more' }}</mat-icon></button></td>
              <td class="nowrap">{{ r.at | date: 'medium' }}</td>
              <td><b>{{ r.eventType }}</b></td>
              <td>{{ r.actor }}<div class="muted small">{{ r.actorType }}{{ r.sourceIp ? ' · ' + r.sourceIp : '' }}</div></td>
              <td class="small">{{ r.entityType ?? '' }}</td>
              <td class="small change">@if (r.previousValue) { <span class="old">{{ short(r.previousValue) }}</span> → } {{ short(r.newValue) }}</td>
              <td>{{ r.reason }}</td>
              <td><app-status [value]="r.severity" /></td>
            </tr>
            @if (open().has(r.id)) {
              <tr class="detail"><td></td><td colspan="7">
                <dl class="kv small">
                  <dt>Entity</dt><dd class="mono">{{ r.entityType }} {{ r.entityId }}</dd>
                  @if (r.companyId) { <dt>Company</dt><dd class="mono">{{ r.companyId }}</dd> }
                  @if (r.userId) { <dt>User</dt><dd class="mono">{{ r.userId }}</dd> }
                  @if (r.serverId) { <dt>Server</dt><dd class="mono">{{ r.serverId }}</dd> }
                  <dt>Correlation</dt><dd class="mono">{{ r.correlationId ?? '—' }}</dd>
                  <dt>Previous</dt><dd><pre class="mono">{{ pretty(r.previousValue) }}</pre></dd>
                  <dt>New</dt><dd><pre class="mono">{{ pretty(r.newValue) }}</pre></dd>
                </dl>
              </td></tr>
            }
          } @empty { <tr><td colspan="8" class="empty">No audit entries.</td></tr> }
        </tbody>
      </table>
    </div>`,
  styles: `
    .nowrap { white-space: nowrap; } .x { width: 28px !important; height: 28px !important; padding: 2px !important; }
    .change { min-width: 220px; max-width: 420px; overflow-wrap: anywhere; } .old { color: var(--muted); text-decoration: line-through; }
    pre { margin: 0; white-space: pre-wrap; word-break: break-word; max-height: 260px; overflow: auto; }
    tr.detail td { background: var(--surface-2); }
  `,
})
export class AuditTable {
  rows = input<AuditRow[]>([]);
  open = signal(new Set<string>());
  toggle(id: string) { const s = new Set(this.open()); if (s.has(id)) s.delete(id); else s.add(id); this.open.set(s); }
  short(v?: string | null) { if (!v) return ''; return v.length > 120 ? v.slice(0, 117) + '…' : v; }
  pretty(v?: string | null) { if (!v) return '—'; try { return JSON.stringify(JSON.parse(v), null, 2); } catch { return v; } }
}
