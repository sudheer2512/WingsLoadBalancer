import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { DashboardFilter, ROUTING_OUTCOMES, RoutingHistoryRow } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { HumanizePipe, NumPipe, StatusChip } from '../shared/display';
import { DecisionFilter } from '../shared/filters';

const EVENT_KINDS = ['NewSession', 'Reroute', 'Failover', 'Failback', 'TransportFailure', 'Denied', 'NoAssignment', 'NoHealthyDestination', 'ConfigurationStale'];

@Component({
  selector: 'app-routing-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, MatFormFieldModule, MatSelectModule, MatInputModule, MatButtonModule, MatIconModule, MatPaginatorModule,
    MatProgressBarModule, StatusChip, HumanizePipe, NumPipe, DecisionFilter],
  template: `
    <div class="page">
      <div class="page-head"><h1>Routing history</h1><span class="muted">{{ total() | n }} decision(s)</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-decision-filter [(value)]="filter" [showUsername]="true" (changed)="first()">
        <mat-form-field><mat-label>Outcome</mat-label>
          <mat-select [ngModel]="outcome()" (ngModelChange)="outcome.set($event); first()"><mat-option value="">Any outcome</mat-option>
            @for (o of outcomes; track o) { <mat-option [value]="o">{{ o | humanize }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Event</mat-label>
          <mat-select [ngModel]="eventKind()" (ngModelChange)="eventKind.set($event); first()"><mat-option value="">Any event</mat-option>
            @for (k of kinds; track k) { <mat-option [value]="k">{{ k | humanize }}</mat-option> }</mat-select></mat-form-field>
      </app-decision-filter>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      <div class="table-wrap">
        <table class="data compact">
          <thead><tr><th>Time</th><th>Event</th><th>Outcome</th><th>Company / user</th><th>Server</th><th>Source</th><th>Env / app</th><th>Flags</th>
            <th>Reason</th><th class="num">Decision</th><th>Gateway</th><th class="num">Config</th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id) {
              <tr>
                <td class="nowrap">{{ r.at | date: 'MMM d, HH:mm:ss' }}</td>
                <td>{{ r.eventKind | humanize }}</td>
                <td><app-status [value]="r.outcome" /></td>
                <td>@if (r.companyId) { <a [routerLink]="['/companies', r.companyId]">{{ r.company }}</a> } @else { — }
                  @if (r.username) { <span class="muted"> / {{ r.username }}</span> }</td>
                <td>@if (r.serverId) { <a [routerLink]="['/servers', r.serverId]">{{ r.server }}</a> } @else { — }</td>
                <td>{{ r.source | humanize }}@if (r.poolId) { <div class="muted small">{{ lookups.poolName(r.poolId) }}</div> }</td>
                <td>{{ r.environment ?? '—' }}<div class="muted small">{{ lookups.appCode(r.applicationId) }}</div></td>
                <td>@if (r.isFailover) { <span class="tag">failover</span> } @if (r.isFailback) { <span class="tag">failback</span> } @if (r.affinityApplied) { <span class="tag">affinity</span> }</td>
                <td class="small reason wrap">{{ r.reason }}</td>
                <td class="num">{{ r.decisionMicroseconds | n: 0 : ' µs' }}</td>
                <td class="small">{{ r.gatewayInstance }}<div class="muted mono">{{ r.correlationId }}</div></td>
                <td class="num mono">{{ r.configVersion }}</td>
              </tr>
            } @empty { <tr><td colspan="12" class="empty">No routing decisions match the filters.</td></tr> }
          </tbody>
        </table>
      </div>
      <mat-paginator [length]="total()" [pageIndex]="page()" [pageSize]="size()" [pageSizeOptions]="[50, 100, 250, 500]" (page)="paged($event)" />
    </div>`,
  styles: `.nowrap { white-space: nowrap; } .reason { min-width: 220px; max-width: 360px; }`,
})
export class RoutingHistory implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private route = inject(ActivatedRoute);
  lookups = inject(LookupService);
  outcomes = ROUTING_OUTCOMES; kinds = EVENT_KINDS;
  filter = signal<DashboardFilter>({});
  outcome = signal(''); eventKind = signal('');
  rows = signal<RoutingHistoryRow[]>([]);
  total = signal(0);
  page = signal(0); size = signal(100);
  loading = signal(false);

  ngOnInit() {
    const q = this.route.snapshot.queryParamMap;
    const f: DashboardFilter = {};
    if (q.get('companyId')) f.companyId = q.get('companyId')!;
    if (q.get('serverId')) f.serverId = q.get('serverId')!;
    this.filter.set(f);
    this.load();
  }

  first() { this.page.set(0); this.load(); }
  paged(e: PageEvent) { this.page.set(e.pageIndex); this.size.set(e.pageSize); this.load(); }

  async load() {
    this.loading.set(true);
    try {
      const r = await firstValueFrom(this.api.routingHistory({ ...this.filter(), outcome: this.outcome(), eventKind: this.eventKind(),
        skip: this.page() * this.size(), take: this.size() }));
      this.rows.set(r.rows);
      this.total.set(r.total);
    } catch (e) { this.ui.error(e); } finally { this.loading.set(false); }
  }
}
