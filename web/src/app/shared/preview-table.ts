import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PreviewResult } from '../core/models';
import { StatusChip } from './display';

/** Before/after routing comparison returned by the assignment preview endpoints. */
@Component({
  selector: 'app-preview-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusChip],
  template: `
    @if (result(); as r) {
      @if (r.validationErrors.length) {
        <div class="callout bad"><strong>This change would be rejected</strong><ul>@for (e of r.validationErrors; track e) { <li>{{ e }}</li> }</ul></div>
      } @else {
        <div class="callout good">Validation passed. Nothing has been saved yet.</div>
      }
      @if (r.preview.issues.length) {
        <div class="callout warn"><strong>Configuration issues after the change</strong><ul>@for (e of r.preview.issues; track e) { <li>{{ e }}</li> }</ul></div>
      }
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>User</th><th>Environment</th><th>Before</th><th>After</th><th>Changed</th></tr></thead>
          <tbody>
            @for (p of r.preview.results; track p.username) {
              <tr [class.changed]="p.changed">
                <td><b>{{ p.company }}</b> / {{ p.username }}</td><td>{{ p.environment }}</td>
                <td><app-status [value]="p.before.outcome" /> {{ p.before.server ?? '—' }}<div class="muted small">{{ p.before.reason }}</div></td>
                <td><app-status [value]="p.after.outcome" /> <b>{{ p.after.server ?? '—' }}</b><div class="muted small">{{ p.after.reason }}</div></td>
                <td>{{ p.changed ? 'Yes' : 'No' }}</td>
              </tr>
            } @empty { <tr><td colspan="5" class="empty">No sample users in this company.</td></tr> }
          </tbody>
        </table>
      </div>
    }`,
  styles: `tr.changed td { background: var(--info-bg); }`,
})
export class PreviewTable {
  result = input<PreviewResult | null>(null);
}
