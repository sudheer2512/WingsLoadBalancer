import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { errorMessage } from '../core/auth.service';
import { E2eResult, ENVIRONMENTS, Environment, ServerSummary, SimulationResult } from '../core/models';
import { LookupService } from '../core/ui.service';
import { HumanizePipe, StatusChip } from '../shared/display';

@Component({
  selector: 'app-simulator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, NgTemplateOutlet, RouterLink, MatFormFieldModule, MatInputModule, MatSelectModule, MatAutocompleteModule, MatButtonModule, MatIconModule,
    MatProgressBarModule, StatusChip, HumanizePipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Routing simulator</h1><span class="muted">What-if routing over the live configuration and health. Nothing is changed.</span></div>
      <form class="panel" (ngSubmit)="simulate()">
        <div class="filters">
          <mat-form-field class="wide"><mat-label>Company (code, name or alias)</mat-label>
            <input matInput name="company" [(ngModel)]="company" [matAutocomplete]="companyAuto" required (ngModelChange)="companyText.set($event)" />
            <mat-autocomplete #companyAuto>@for (c of companyOptions(); track c.id) { <mat-option [value]="c.code">{{ c.code }} · {{ c.name }}</mat-option> }</mat-autocomplete>
          </mat-form-field>
          <mat-form-field class="wide"><mat-label>Username</mat-label><input matInput name="username" [(ngModel)]="username" required autocomplete="off" /></mat-form-field>
          <mat-form-field><mat-label>Application</mat-label>
            <mat-select name="app" [(ngModel)]="application"><mat-option value="">First application</mat-option>
              @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.code">{{ a.code }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Environment</mat-label>
            <mat-select name="env" [(ngModel)]="environment">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
          <button mat-flat-button [disabled]="busy() || !company.trim() || !username.trim()"><mat-icon>play_arrow</mat-icon>Simulate</button>
        </div>
        @if (busy()) { <mat-progress-bar mode="indeterminate" /> }
        @if (error()) { <p class="error">{{ error() }}</p> }
      </form>

      @if (result(); as r) {
        <section class="panel result" [attr.data-outcome]="r.outcome">
          <div class="panel-head">
            <app-status [value]="r.outcome" /><h2>{{ r.outcome === 'Routed' ? 'Routed to ' + (r.chosenServer?.name ?? '?') : (r.outcome | humanize) }}</h2>
            @if (r.isFailover) { <app-status value="Warning" label="Failover" /> }
            <span class="spacer"></span><span class="muted small">Config version {{ r.configVersion }}</span>
          </div>
          <p class="reason">{{ r.reason }}</p>
          <div class="cards">
            <div class="card"><h3>Resolved identity</h3>
              <dl class="kv">
                <dt>Company</dt><dd>@if (r.companyId) { <a [routerLink]="['/companies', r.companyId]">{{ r.companyCode }}</a> · {{ r.companyName }} <app-status [value]="r.companyStatus" /> } @else { not found }</dd>
                <dt>Company ID</dt><dd class="mono">{{ r.companyId ?? '—' }}</dd>
                <dt>User ID</dt><dd class="mono">{{ r.userId ?? '—' }}</dd>
                <dt>Membership</dt><dd>{{ r.membershipStatus ?? '—' }}</dd>
                <dt>Application</dt><dd>{{ r.applicationCode ?? '—' }} <span class="mono muted small">{{ r.applicationId }}</span></dd>
              </dl></div>
            <div class="card"><h3>Decision</h3>
              <dl class="kv">
                <dt>Source</dt><dd>{{ r.source | humanize }}</dd>
                <dt>Chosen server</dt><dd>@if (r.chosenServer) { <ng-container [ngTemplateOutlet]="srv" /> } @else { none }</dd>
                <dt>Failover server</dt><dd>@if (r.failoverServer; as f) { <a [routerLink]="['/servers', f.id]">{{ f.name }}</a> <app-status [value]="f.health" /> } @else { none }</dd>
                <dt>Routing policy</dt><dd>{{ r.routingPolicy ?? '—' }}</dd>
                <dt>Failover policy</dt><dd>{{ r.failoverPolicy ?? '—' }}</dd>
                <dt>Selection</dt><dd>{{ r.selectionDetail ?? '—' }}</dd>
              </dl></div>
            <div class="card"><h3>User assignment</h3>
              @if (r.userAssignment; as ua) {
                <dl class="kv"><dt>Primary</dt><dd>{{ name(ua.primary) }}</dd><dt>Secondary</dt><dd>{{ name(ua.secondary) }}</dd>
                  <dt>Effective</dt><dd>{{ ua.effectiveFrom | date: 'short' }} → {{ (ua.effectiveTo | date: 'short') ?? 'open' }}</dd>
                  <dt>Company fallback</dt><dd>{{ ua.allowCompanyRouteFallback ? 'Allowed' : 'No' }}</dd></dl>
              } @else { <p class="muted">No user-level assignment applies.</p> }</div>
            <div class="card"><h3>Company assignment</h3>
              @if (r.companyAssignment; as ca) {
                <dl class="kv"><dt>Primary</dt><dd>{{ name(ca.primary) }}</dd><dt>Secondary</dt><dd>{{ name(ca.secondary) }}</dd>
                  <dt>Cluster</dt><dd>{{ ca.clusterPoolId ? (lookups.poolName(ca.clusterPoolId) + ' · ' + (ca.clusterMode | humanize)) : 'None' }}</dd>
                  <dt>Effective</dt><dd>{{ ca.effectiveFrom | date: 'short' }} → {{ (ca.effectiveTo | date: 'short') ?? 'open' }}</dd></dl>
              } @else { <p class="muted">No company assignment applies.</p> }</div>
            <div class="card"><h3>Eligible shared pools</h3>
              @for (p of r.eligiblePools; track p.id) {
                <div><b>{{ p.name }}</b> <span class="muted small">{{ p.algorithm | humanize }} · {{ p.affinity | humanize }} · priority {{ p.priority }} · {{ p.members }} member(s)</span></div>
              } @empty { <p class="muted">None.</p> }</div>
          </div>
          <h3 class="gap">Step-by-step trace</h3>
          <ol class="trace">@for (t of r.trace; track $index) { <li class="mono">{{ t }}</li> } @empty { <li class="muted">No trace returned.</li> }</ol>
          <ng-template #srv>
            @if (r.chosenServer; as c) { <a [routerLink]="['/servers', c.id]"><b>{{ c.name }}</b></a> <app-status [value]="c.health" /> <span class="muted small">{{ c.type }} · {{ c.environment }} · {{ c.adminState }}</span> }
          </ng-template>
        </section>
      }

      <section class="panel">
        <h2>End-to-end test</h2>
        <p class="muted">Signs in through the configured gateway with a real (test) account and makes one request. The password is sent once to the admin API, never stored, logged or shown again.</p>
        <form class="filters" (ngSubmit)="e2e()" autocomplete="off">
          <mat-form-field><mat-label>Company</mat-label><input matInput name="ec" [(ngModel)]="e2eCompany" required /></mat-form-field>
          <mat-form-field><mat-label>Username</mat-label><input matInput name="eu" [(ngModel)]="e2eUser" required autocomplete="off" /></mat-form-field>
          <mat-form-field><mat-label>Password</mat-label><input matInput name="ep" type="password" [(ngModel)]="e2ePassword" required autocomplete="new-password" /></mat-form-field>
          <mat-form-field><mat-label>Path</mat-label><input matInput name="epath" [(ngModel)]="e2ePath" placeholder="/" /></mat-form-field>
          <mat-form-field><mat-label>Host header (optional)</mat-label><input matInput name="ehost" [(ngModel)]="e2eHost" placeholder="erp.example.com" /></mat-form-field>
          <button mat-flat-button [disabled]="e2eBusy() || !e2eCompany || !e2eUser || !e2ePassword"><mat-icon>science</mat-icon>Run test</button>
        </form>
        @if (e2eBusy()) { <mat-progress-bar mode="indeterminate" /> }
        @if (e2eError()) { <p class="error">{{ e2eError() }}</p> }
        @if (e2eResult(); as r) {
          <div class="callout" [class.good]="r.success" [class.bad]="!r.success"><strong>{{ r.success ? 'End-to-end test passed' : 'End-to-end test failed' }}</strong></div>
          <table class="data"><thead><tr><th>Step</th><th class="num">HTTP status</th><th class="num">Duration</th><th>Served by</th></tr></thead>
            <tbody>@for (s of r.steps; track $index) {
              <tr><td>{{ s.step }}</td><td class="num"><app-status [value]="s.status >= 200 && s.status < 400 ? 'Passed' : 'Failed'" [label]="'' + s.status" /></td>
                <td class="num">{{ s.ms }} ms</td><td>{{ s.servedBy ?? '—' }}</td></tr>
            }</tbody></table>
        }
      </section>
    </div>`,
  styles: `
    .result[data-outcome='Routed'] { border-left: 4px solid var(--good); }
    .result:not([data-outcome='Routed']) { border-left: 4px solid var(--bad); }
    .reason { font-size: 14px; margin: 4px 0 10px; }
    .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 10px; }
    .card { border: 1px solid var(--border); border-radius: 6px; padding: 10px; } .card h3 { margin-bottom: 6px; } .card .kv { grid-template-columns: 110px 1fr; }
    .gap { margin: 12px 0 4px; }
    .trace { margin: 0; padding-left: 24px; } .trace li { padding: 2px 0; border-bottom: 1px dashed var(--border); }
  `,
})
export class Simulator implements OnInit {
  private api = inject(ApiService);
  private route = inject(ActivatedRoute);
  lookups = inject(LookupService);
  envs = ENVIRONMENTS;
  company = ''; username = ''; application = ''; environment: Environment = 'Production';
  companyText = signal('');
  companyOptions = computed(() => {
    const t = this.companyText().toLowerCase();
    return this.lookups.companies().filter(c => !t || c.code.toLowerCase().includes(t) || c.name.toLowerCase().includes(t) || c.aliases.some(a => a.toLowerCase().includes(t))).slice(0, 30);
  });
  result = signal<SimulationResult | null>(null);
  busy = signal(false);
  error = signal('');
  e2eCompany = ''; e2eUser = ''; e2ePassword = ''; e2ePath = '/'; e2eHost = '';
  e2eResult = signal<E2eResult | null>(null);
  e2eBusy = signal(false);
  e2eError = signal('');

  ngOnInit() {
    this.lookups.ensure(['companies', 'applications', 'pools']);
    const q = this.route.snapshot.queryParamMap;
    this.company = q.get('company') ?? '';
    this.username = q.get('username') ?? '';
    this.application = q.get('application') ?? '';
    const env = q.get('environment') as Environment | null;
    if (env && ENVIRONMENTS.includes(env)) this.environment = env;
    this.e2eCompany = this.company; this.e2eUser = this.username;
    if (this.company && this.username) this.simulate();
  }

  name(s?: ServerSummary | null) { return s ? `${s.name} (${s.health})` : '—'; }

  async simulate() {
    this.busy.set(true); this.error.set('');
    try { this.result.set(await firstValueFrom(this.api.simulate({ company: this.company.trim(), username: this.username.trim(), application: this.application || null, environment: this.environment }))); }
    catch (e) { this.error.set(errorMessage(e)); this.result.set(null); }
    finally { this.busy.set(false); }
  }

  async e2e() {
    this.e2eBusy.set(true); this.e2eError.set(''); this.e2eResult.set(null);
    const password = this.e2ePassword;
    this.e2ePassword = ''; // never keep it around in the page
    try {
      this.e2eResult.set(await firstValueFrom(this.api.e2e({ company: this.e2eCompany.trim(), username: this.e2eUser.trim(), password,
        path: this.e2ePath.trim() || '/', host: this.e2eHost.trim() || null })));
    } catch (e) { this.e2eError.set(errorMessage(e)); }
    finally { this.e2eBusy.set(false); }
  }
}
