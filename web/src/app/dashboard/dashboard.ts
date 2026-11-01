import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatTree, MatTreeModule } from '@angular/material/tree';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P } from '../core/auth.service';
import { DashboardFilter, DashboardSummary, ServerUtilization, TimeSeriesBucket, TopologyNode } from '../core/models';
import { MonitoringService } from '../core/monitoring.service';
import { UiService } from '../core/ui.service';
import { BarChart, ChartSeries, Sparkline, TimeChart } from '../shared/charts';
import { AgoPipe, HumanizePipe, Meter, NumPipe, StatusChip } from '../shared/display';
import { DecisionFilter } from '../shared/filters';

interface Kpi { label: string; value: string; sub?: string; icon: string; tone?: 'good' | 'warn' | 'bad'; link?: string; }

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, NgTemplateOutlet, RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatSelectModule, MatIconModule,
    MatTooltipModule, MatProgressBarModule, MatTreeModule, StatusChip, Meter, Sparkline, TimeChart, BarChart, NumPipe, AgoPipe,
    HumanizePipe, DecisionFilter],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Operations dashboard</h1>
        @if (summary(); as s) { <span class="muted small">Updated {{ s.generatedAt | date: 'mediumTime' }}{{ monitoring.connected() ? ' · live' : '' }}</span> }
        <span class="spacer"></span>
        <button mat-stroked-button (click)="reloadAll()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>

      <section class="kpis" aria-label="Key indicators">
        @for (k of kpis(); track k.label) {
          <a class="kpi" [attr.data-tone]="k.tone ?? null" [routerLink]="k.link ?? null" [class.static]="!k.link">
            <mat-icon class="ki">{{ k.icon }}</mat-icon>
            <div class="kl">{{ k.label }}</div>
            <div class="kv">{{ k.value }}</div>
            @if (k.sub) { <div class="ks">{{ k.sub }}</div> }
          </a>
        }
      </section>

      <section class="panel">
        <div class="panel-head"><h2>Traffic and routing decisions</h2><span class="spacer"></span>
          <mat-form-field class="bucket"><mat-label>Bucket</mat-label>
            <mat-select [ngModel]="bucket()" (ngModelChange)="bucket.set($event); loadSeries()">
              @for (b of [5, 15, 30, 60, 240]; track b) { <mat-option [value]="b">{{ b < 60 ? b + ' min' : b / 60 + ' h' }}</mat-option> }
            </mat-select></mat-form-field>
        </div>
        <app-decision-filter [(value)]="filter" (changed)="loadSeries()" />
        @if (seriesLoading()) { <mat-progress-bar mode="indeterminate" /> }
        <div class="cards-2 charts">
          <div><h3>Routing decisions per bucket</h3>
            <app-time-chart kind="bar" [times]="seriesTimes()" [series]="decisionSeries()" yLabel="Decisions" ariaLabel="Routing decisions over time" [height]="190" /></div>
          <div><h3>Requests processed per bucket</h3>
            <app-time-chart kind="area" [times]="seriesTimes()" [series]="requestSeries()" yLabel="Requests" ariaLabel="Requests over time" [height]="190" /></div>
          <div><h3>Average gateway latency</h3>
            <app-time-chart [times]="seriesTimes()" [series]="latencySeries()" unit="ms" yLabel="Latency (ms)" ariaLabel="Gateway latency over time" [height]="190" /></div>
          <div><h3>Failovers per bucket</h3>
            <app-time-chart kind="bar" [times]="seriesTimes()" [series]="failoverSeries()" yLabel="Failovers" ariaLabel="Failovers over time" [height]="190" /></div>
        </div>
      </section>

      <section class="panel">
        <div class="panel-head"><h2>Server utilisation</h2>
          <span class="muted small">Current values with the trend over the selected window</span><span class="spacer"></span>
          <mat-button-toggle-group [ngModel]="hours()" (ngModelChange)="hours.set($event); loadUtilization()" aria-label="Utilisation window" hideSingleSelectionIndicator>
            @for (h of [1, 6, 24, 72]; track h) { <mat-button-toggle [value]="h">{{ h }} h</mat-button-toggle> }
          </mat-button-toggle-group>
        </div>
        <div class="table-wrap">
          <table class="data util">
            <thead><tr>
              <th>Server</th><th>Health</th><th>Type</th><th>CPU</th><th>Memory</th><th class="num">Active requests</th>
              <th class="num">Response time</th><th class="num">Error rate</th><th class="num">Sessions / capacity</th><th>Last check</th>
            </tr></thead>
            <tbody>
              @for (s of utilization(); track s.id) {
                <tr class="clickable" (click)="openServer(s.id)">
                  <td><a [routerLink]="['/servers', s.id]" (click)="$event.stopPropagation()">{{ s.name }}</a><div class="muted small">{{ s.environment }}</div></td>
                  <td><app-status [value]="s.adminState === 'Active' ? s.health : s.adminState" [title]="s.current?.lastReason" />
                    @if (s.current?.awaitingManualRestore) { <div class="small warnText">Awaiting manual restore</div> }</td>
                  <td>{{ s.type }}</td>
                  <td><div class="cell"><app-meter [value]="s.current?.cpuPercent" label="CPU" />
                    <app-sparkline [values]="col(s, 'cpu')" [max]="100" label="CPU %" /></div></td>
                  <td><div class="cell"><app-meter [value]="s.current?.memoryPercent" label="Memory" />
                    <app-sparkline [values]="col(s, 'memory')" [max]="100" label="Memory %" [slot]="2" /></div></td>
                  <td class="num"><div class="cell r">{{ s.current?.activeRequests | n }}<app-sparkline [values]="col(s, 'requests')" label="Active requests" [slot]="3" /></div></td>
                  <td class="num"><div class="cell r">{{ s.current?.responseTimeMs | n: 1 : ' ms' }}<app-sparkline [values]="col(s, 'responseMs')" label="Response ms" [slot]="1" /></div></td>
                  <td class="num"><div class="cell r">{{ s.current?.errorRatePercent | n: 2 : '%' }}<app-sparkline [values]="col(s, 'errors')" label="Error %" [slot]="2" /></div></td>
                  <td class="num">{{ s.current?.activeSessions | n }} / {{ s.maxCapacity || '∞' }}</td>
                  <td class="muted small">{{ s.current?.lastCheckAt | ago }}</td>
                </tr>
              } @empty { <tr><td colspan="10" class="empty">No servers.</td></tr> }
            </tbody>
          </table>
        </div>
      </section>

      <div class="cards-2 section-gap">
        <section class="panel">
          <div class="panel-head"><h2>Live routing decisions</h2>
            <app-status [value]="monitoring.connected() ? 'Online' : 'Offline'" [label]="monitoring.connected() ? 'Streaming' : 'Disconnected'" />
            <span class="spacer"></span>
            <a mat-button routerLink="/routing/history">Full history</a>
          </div>
          <div class="feed" role="log" aria-live="polite">
            @for (d of monitoring.decisions(); track d.id) {
              <div class="feed-row">
                <span class="t mono">{{ d.at | date: 'HH:mm:ss' }}</span>
                <app-status [value]="d.outcome" />
                <span class="what">
                  <b>{{ d.company ?? '—' }}</b> → {{ d.server ?? 'no server' }}
                  <span class="muted">· {{ d.eventKind | humanize }} · {{ d.source | humanize }}</span>
                  @if (d.isFailover) { <span class="tag">failover</span> } @if (d.isFailback) { <span class="tag">failback</span> }
                  @if (d.reason) { <div class="muted small">{{ d.reason }}</div> }
                </span>
              </div>
            } @empty { <div class="empty">Waiting for routing decisions… New sessions, reroutes and failovers appear here as they happen.</div> }
          </div>
        </section>

        <section class="panel">
          <div class="panel-head nowrap-head"><h2>Topology</h2>
            <span class="spacer"></span>
            <button mat-icon-button (click)="tree.expandAll()" matTooltip="Expand all" aria-label="Expand all"><mat-icon>unfold_more</mat-icon></button>
            <button mat-icon-button (click)="tree.collapseAll()" matTooltip="Collapse all" aria-label="Collapse all"><mat-icon>unfold_less</mat-icon></button>
          </div>
          <p class="muted small sub">Environment → pool / dedicated group → server → company. Select a server or company to open it.</p>
          <div class="tree-wrap">
            <mat-tree #tree [dataSource]="topology()" [childrenAccessor]="children" class="topo">
              <mat-tree-node *matTreeNodeDef="let node" matTreeNodePadding [matTreeNodePaddingIndent]="20">
                <span class="twisty"></span><ng-container *ngTemplateOutlet="nodeTpl; context: { $implicit: node }" />
              </mat-tree-node>
              <mat-tree-node *matTreeNodeDef="let node; when: hasChild" matTreeNodePadding [matTreeNodePaddingIndent]="20" [isExpandable]="true">
                <button mat-icon-button matTreeNodeToggle class="twisty" [attr.aria-label]="'Toggle ' + node.label">
                  <mat-icon>{{ tree.isExpanded(node) ? 'expand_more' : 'chevron_right' }}</mat-icon></button>
                <ng-container *ngTemplateOutlet="nodeTpl; context: { $implicit: node }" />
              </mat-tree-node>
            </mat-tree>
            @if (topology().length === 0) { <div class="empty">No topology data.</div> }
          </div>
          <ng-template #nodeTpl let-node>
            <span class="node" [attr.data-kind]="node.kind">
              <mat-icon class="ni">{{ icon(node.kind) }}</mat-icon>
              @switch (node.kind) {
                @case ('server') {
                  <a [routerLink]="['/servers', node.id]">{{ node.label }}</a>
                  <app-status [value]="node.adminState === 'Active' ? node.health : node.adminState" />
                  <span class="muted small">{{ node.type }}@if (node.dedicatedUsers) { · {{ node.dedicatedUsers }} dedicated user(s) }</span>
                }
                @case ('company') {
                  <a [routerLink]="['/companies', node.id]">{{ node.label }}</a><span class="tag">{{ node.role }}</span>
                }
                @case ('pool') {
                  <b>{{ node.label }}</b><span class="muted small">{{ node.algorithm | humanize }} · {{ node.affinity | humanize }} affinity</span>
                  @for (t of node.tenants ?? []; track t) { <span class="tag">{{ t }}</span> }
                }
                @default { <b>{{ node.label }}</b> }
              }
            </span>
          </ng-template>
        </section>
      </div>

      <div class="cards-2 section-gap">
        <section class="panel">
          <h2>Gateway instances</h2>
          <div class="table-wrap">
            <table class="data">
              <thead><tr><th>Instance</th><th>Status</th><th class="num">Req/s</th><th class="num">Latency</th><th class="num">Active req.</th>
                <th>Config</th><th>Dependencies</th><th>Heartbeat</th></tr></thead>
              <tbody>
                @for (g of summary()?.gateways ?? []; track g.instanceName) {
                  <tr>
                    <td><b>{{ g.instanceName }}</b><div class="muted small">{{ g.host }} · v{{ g.version }}</div></td>
                    <td><app-status [value]="g.online ? 'Online' : 'Offline'" /></td>
                    <td class="num">{{ g.requestsPerSecond | n: 1 }}</td>
                    <td class="num">{{ g.averageLatencyMs | n: 1 : ' ms' }}</td>
                    <td class="num">{{ g.activeRequests | n }}</td>
                    <td>@if (g.configurationStale) { <app-status value="Stale" label="Stale" /> } @else { <app-status value="Current" label="In sync" [title]="'v' + g.configVersion" /> }</td>
                    <td class="small">
                      <app-status [value]="g.databaseAvailable ? 'Up' : 'Down'" [label]="'Database ' + (g.databaseAvailable ? 'up' : 'down')" />
                      <app-status [value]="g.redisAvailable ? 'Up' : 'None'" [label]="'Redis ' + (g.redisAvailable ? 'up' : 'n/a')" />
                    </td>
                    <td class="muted small">{{ g.lastHeartbeatAt | ago }}</td>
                  </tr>
                } @empty { <tr><td colspan="8" class="empty">No gateway has reported in the last 24 hours.</td></tr> }
              </tbody>
            </table>
          </div>
        </section>
        <section class="panel">
          <h2>Active sessions by server</h2>
          <app-bar-chart [items]="sessionBars()" emptyText="No active sessions" />
        </section>
      </div>
    </div>`,
  styles: `
    .kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(165px, 1fr)); gap: 10px; margin-bottom: 12px; }
    .kpi { position: relative; display: block; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; color: var(--text); text-decoration: none; border-left: 4px solid var(--border); }
    .kpi:not(.static):hover { background: var(--hover); }
    .kpi[data-tone='good'] { border-left-color: var(--good); } .kpi[data-tone='warn'] { border-left-color: var(--warn); } .kpi[data-tone='bad'] { border-left-color: var(--bad); }
    .ki { position: absolute; right: 10px; top: 10px; color: var(--muted); font-size: 20px; width: 20px; height: 20px; }
    .kl { font-size: 12px; color: var(--muted); padding-right: 24px; }
    .kv { font-size: 24px; font-weight: 600; font-variant-numeric: tabular-nums; line-height: 1.3; }
    .ks { font-size: 11px; color: var(--muted); }
    .charts { margin-top: 4px; } .charts h3 { margin-bottom: 4px; }
    .bucket { width: 120px; }
    .section-gap { margin-top: 12px; }
    .util td { white-space: nowrap; }
    .cell { display: flex; align-items: center; gap: 8px; } .cell.r { justify-content: flex-end; }
    .warnText { color: var(--warn); }
    .feed { max-height: 420px; overflow: auto; }
    .feed-row { display: grid; grid-template-columns: 64px auto 1fr; gap: 8px; align-items: start; padding: 5px 2px; border-bottom: 1px solid var(--border); }
    .feed-row .t { color: var(--muted); padding-top: 2px; }
    .tree-wrap { max-height: 420px; overflow: auto; } .nowrap-head { flex-wrap: nowrap; } .sub { margin: 0 0 4px; }
    .topo { background: transparent !important; }
    .topo .mat-tree-node { min-height: 30px; }
    .twisty { width: 28px !important; height: 28px !important; padding: 2px !important; display: inline-flex; flex: none; }
    .node { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .ni { font-size: 18px; width: 18px; height: 18px; color: var(--muted); }
  `,
})
export class Dashboard implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private router = inject(Router);
  auth = inject(AuthService);
  monitoring = inject(MonitoringService);
  private destroyRef = inject(DestroyRef);

  tree = viewChild<MatTree<TopologyNode>>('tree');
  private initialSummary = signal<DashboardSummary | null>(null);
  summary = computed(() => this.monitoring.summary() ?? this.initialSummary());
  utilization = signal<ServerUtilization[]>([]);
  series = signal<TimeSeriesBucket[]>([]);
  seriesLoading = signal(false);
  topology = signal<TopologyNode[]>([]);
  filter = signal<DashboardFilter>({});
  bucket = signal(15);
  hours = signal(6);

  children = (n: TopologyNode) => n.children ?? [];
  hasChild = (_: number, n: TopologyNode) => !!n.children?.length;

  constructor() {
    // Expand the first two levels whenever the topology is (re)loaded.
    effect(() => {
      const data = this.topology();
      const t = this.tree();
      if (!t || !data.length) return;
      queueMicrotask(() => { for (const env of data) { t.expand(env); for (const c of env.children ?? []) t.expand(c); } });
    });
  }

  ngOnInit() {
    this.reloadAll();
    // Reflect live health changes in the utilisation grid without a reload.
    this.monitoring.health$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(rows => {
      const byId = new Map(rows.map(r => [r.serverId, r]));
      this.utilization.update(list => list.map(s => {
        const h = byId.get(s.id);
        return h ? { ...s, health: h.state, current: s.current ? { ...s.current, lastReason: h.lastReason, cpuPercent: h.cpuPercent ?? s.current.cpuPercent,
          memoryPercent: h.memoryPercent ?? s.current.memoryPercent, responseTimeMs: h.responseTimeMs ?? s.current.responseTimeMs } : s.current } : s;
      }));
      this.loadTopology();
    });
    const timer = setInterval(() => this.loadUtilization(), 60_000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  reloadAll() {
    this.api.summary().subscribe({ next: s => this.initialSummary.set(s), error: e => this.ui.error(e) });
    this.loadUtilization();
    this.loadSeries();
    this.loadTopology();
  }

  loadUtilization() { this.api.utilization(this.hours()).subscribe({ next: u => this.utilization.set(u), error: e => this.ui.error(e) }); }
  loadTopology() { this.api.topology().subscribe({ next: t => this.topology.set(t), error: () => undefined }); }

  async loadSeries() {
    this.seriesLoading.set(true);
    try { this.series.set(fillBuckets(await firstValueFrom(this.api.timeseries(this.filter(), this.bucket())), this.bucket(), this.filter())); }
    catch (e) { this.ui.error(e); }
    finally { this.seriesLoading.set(false); }
  }

  seriesTimes = computed(() => this.series().map(b => new Date(b.at).getTime()));
  decisionSeries = computed<ChartSeries[]>(() => [
    { name: 'Routed', values: this.series().map(b => b.routedDecisions), slot: 1 },
    { name: 'Failed / refused', values: this.series().map(b => b.failedDecisions), slot: 2 },
  ]);
  requestSeries = computed<ChartSeries[]>(() => [{ name: 'Requests', values: this.series().map(b => b.requests), slot: 1 }]);
  latencySeries = computed<ChartSeries[]>(() => [{ name: 'Avg latency', values: this.series().map(b => (b.requests || b.averageLatencyMs ? b.averageLatencyMs : null)), slot: 3 }]);
  failoverSeries = computed<ChartSeries[]>(() => [{ name: 'Failovers', values: this.series().map(b => b.failovers), slot: 2 }]);
  sessionBars = computed(() => this.utilization().filter(s => (s.current?.activeSessions ?? 0) > 0)
    .map(s => ({ label: s.name, value: s.current?.activeSessions ?? 0 })).sort((a, b) => b.value - a.value));

  kpis = computed<Kpi[]>(() => {
    const s = this.summary();
    if (!s) return [];
    const n = (v: number, d = 0) => v.toLocaleString(undefined, { maximumFractionDigits: d });
    return [
      { label: 'Servers', value: n(s.servers.total), sub: `${s.servers.draining} draining · ${s.servers.unknown} unknown`, icon: 'dns', link: '/servers' },
      { label: 'Healthy', value: n(s.servers.healthy), icon: 'check_circle', tone: 'good', link: '/servers' },
      { label: 'Degraded', value: n(s.servers.degraded), icon: 'warning', tone: s.servers.degraded ? 'warn' : undefined, link: '/servers' },
      { label: 'Offline', value: n(s.servers.offline), sub: 'Disabled, maintenance or unhealthy', icon: 'error', tone: s.servers.offline ? 'bad' : undefined, link: '/servers' },
      { label: 'Companies', value: n(s.companies.total), icon: 'apartment', link: '/companies' },
      { label: 'Dedicated vs shared', value: `${s.companies.dedicated} / ${s.companies.sharedPools}`, sub: 'Companies with dedicated / shared-pool routes', icon: 'call_split', link: '/companies' },
      { label: 'Users on dedicated servers', value: n(s.usersOnDedicatedServers), icon: 'person_pin', link: '/users' },
      { label: 'Active sessions', value: n(s.activeSessions), icon: 'groups', link: '/sessions' },
      { label: 'Requests / sec', value: n(s.requestsPerSecond, 1), sub: 'All live gateways', icon: 'speed' },
      { label: 'Failed routing (1 h)', value: n(s.failedRoutingLastHour), icon: 'block', tone: s.failedRoutingLastHour ? 'warn' : undefined, link: '/routing/history' },
      { label: 'Avg gateway latency', value: `${n(s.averageGatewayLatencyMs, 1)} ms`, sub: `Decision ${n(s.averageDecisionMicroseconds, 0)} µs`, icon: 'timer' },
      { label: 'Current failovers', value: n(s.currentFailovers), icon: 'swap_horiz', tone: s.currentFailovers ? 'warn' : undefined, link: '/routing/history' },
      { label: 'Open alerts', value: n(s.openAlerts), icon: 'notifications_active', tone: s.openAlerts ? 'bad' : 'good', link: this.auth.can(P.alertsRead) ? '/alerts' : undefined },
    ];
  });

  col(s: ServerUtilization, k: 'cpu' | 'memory' | 'requests' | 'responseMs' | 'errors') { return s.series.map(p => p[k]); }
  icon(kind: string) { return ({ environment: 'public', pool: 'hub', group: 'folder_special', server: 'dns', company: 'apartment' } as Record<string, string>)[kind] ?? 'circle'; }
  openServer(id: string) { this.router.navigate(['/servers', id]); }
}

/** The API only returns buckets that have data; fill the gaps with zeros so bars and lines read correctly over time. */
function fillBuckets(rows: TimeSeriesBucket[], minutes: number, f: DashboardFilter): TimeSeriesBucket[] {
  const step = minutes * 60_000;
  const to = f.to ? new Date(f.to).getTime() : Date.now();
  const from = f.from ? new Date(f.from).getTime() : to - 24 * 3600_000;
  const start = Math.floor(from / step) * step, end = Math.floor(to / step) * step;
  if ((end - start) / step > 2000) return rows;
  const byTime = new Map(rows.map(r => [new Date(r.at).getTime(), r]));
  const out: TimeSeriesBucket[] = [];
  for (let t = start; t <= end; t += step) {
    out.push(byTime.get(t) ?? { at: new Date(t).toISOString(), routedDecisions: 0, failedDecisions: 0, failovers: 0, requests: 0, averageLatencyMs: 0 });
  }
  return out;
}
