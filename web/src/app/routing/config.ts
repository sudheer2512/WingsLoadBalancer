import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { RoutingConfig } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { AgoPipe, StatusChip } from '../shared/display';

@Component({
  selector: 'app-routing-config',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatIconModule, StatusChip, AgoPipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Routing configuration</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      @if (cfg(); as c) {
        <div class="cards-2">
          <section class="panel">
            <h2>Current version</h2>
            <div class="ver mono">{{ c.version }}</div>
            <p class="muted">Every routing-relevant change creates a new version in the same transaction; gateways pick it up on their next sync.</p>
            <app-status [value]="inSync() ? 'Healthy' : 'Warning'" [label]="inSync() ? 'All gateways in sync' : outOfSync() + ' gateway(s) out of sync'" />
          </section>
          <section class="panel">
            <h2>Gateway sync status</h2>
            <table class="data"><thead><tr><th>Gateway</th><th>Config version</th><th>Status</th><th>Heartbeat</th></tr></thead>
              <tbody>@for (g of c.gateways; track g.instanceName) {
                <tr><td><b>{{ g.instanceName }}</b></td><td class="mono">{{ g.configVersion }}</td>
                  <td><app-status [value]="g.inSync ? 'InSync' : 'OutOfSync'" [label]="g.inSync ? 'In sync' : 'Behind'" /></td><td>{{ g.lastHeartbeatAt | ago }}</td></tr>
              } @empty { <tr><td colspan="4" class="empty">No gateways registered.</td></tr> }</tbody></table>
          </section>
        </div>
        <section class="panel">
          <h2>Validation issues ({{ c.issues.length }})</h2>
          <table class="data"><thead><tr><th>Severity</th><th>Code</th><th>Message</th><th>Company</th><th>Server</th></tr></thead>
            <tbody>@for (i of c.issues; track $index) {
              <tr><td><app-status [value]="i.severity" /></td><td class="mono">{{ i.code }}</td><td>{{ i.message }}</td>
                <td>@if (i.companyId) { <a [routerLink]="['/companies', i.companyId]">{{ lookups.companyCode(i.companyId) }}</a> }</td>
                <td>@if (i.serverId) { <a [routerLink]="['/servers', i.serverId]">{{ lookups.serverName(i.serverId) }}</a> }</td></tr>
            } @empty { <tr><td colspan="5"><app-status value="Healthy" label="No issues — the configuration validates cleanly" /></td></tr> }</tbody></table>
        </section>
        <section class="panel">
          <h2>Version log</h2>
          <div class="table-wrap log"><table class="data"><thead><tr><th>Version</th><th>Changed</th><th>By</th><th>Summary</th></tr></thead>
            <tbody>@for (v of c.versions; track v.id) {
              <tr><td class="mono">{{ v.version }}</td><td class="nowrap">{{ v.changedAt | date: 'medium' }}</td><td>{{ v.changedBy }}</td><td class="small">{{ v.summary }}</td></tr>
            }</tbody></table></div>
        </section>
      } @else { <div class="empty">Loading…</div> }
    </div>`,
  styles: `.ver { font-size: 22px; font-weight: 600; margin: 4px 0; } .nowrap { white-space: nowrap; } .log { max-height: 480px; }`,
})
export class RoutingConfigPage implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  lookups = inject(LookupService);
  cfg = signal<RoutingConfig | null>(null);
  outOfSync = computed(() => this.cfg()?.gateways.filter(g => !g.inSync).length ?? 0);
  inSync = computed(() => this.outOfSync() === 0);
  ngOnInit() { this.load(); this.lookups.ensure(['companies', 'servers']); }
  load() { this.api.routingConfig().subscribe({ next: c => this.cfg.set(c), error: e => this.ui.error(e) }); }
}
