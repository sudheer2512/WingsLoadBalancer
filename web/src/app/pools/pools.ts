import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipInputEvent, MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService, P, errorList, errorMessage } from '../core/auth.service';
import { AFFINITY_MODES, ALGORITHMS, ENVIRONMENTS, Pool, PoolMember, PoolRequest } from '../core/models';
import { LookupService, UiService } from '../core/ui.service';
import { HumanizePipe, StatusChip } from '../shared/display';

@Component({
  selector: 'app-pool-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatChipsModule, MatIconModule, HumanizePipe],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit pool · ' + data.name : 'Create pool' }}</h2>
    <mat-dialog-content class="dialog-wide">
      <div class="grid-2">
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="100" /></mat-form-field>
        <mat-form-field><mat-label>Environment</mat-label>
          <mat-select [(ngModel)]="f.environment">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select>
          <mat-hint>All members must be in this environment.</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Load-balancing algorithm</mat-label>
          <mat-select [(ngModel)]="f.algorithm">@for (a of algorithms; track a) { <mat-option [value]="a">{{ a | humanize }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Session affinity</mat-label>
          <mat-select [(ngModel)]="f.affinity">@for (a of affinities; track a) { <mat-option [value]="a">{{ a | humanize }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Failover policy</mat-label>
          <mat-select [(ngModel)]="f.failoverPolicyId"><mat-option [value]="null">None</mat-option>
            @for (p of lookups.failoverPolicies(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Region</mat-label><input matInput [(ngModel)]="f.region" maxlength="100" /></mat-form-field>
        <mat-form-field><mat-label>Max capacity (sessions)</mat-label><input matInput type="number" min="0" [(ngModel)]="f.maxCapacity" /><mat-hint>Empty = sum of members</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Applications</mat-label>
          <mat-select multiple [(ngModel)]="f.applicationIds">@for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }}</mat-option> }</mat-select>
          <mat-hint>Empty = all applications</mat-hint></mat-form-field>
      </div>
      <mat-form-field class="full"><mat-label>Eligible service plans</mat-label>
        <mat-chip-grid #plans aria-label="Eligible plans">
          @for (p of f.eligiblePlans; track p) { <mat-chip-row (removed)="removePlan(p)">{{ p }}<button matChipRemove [attr.aria-label]="'Remove ' + p"><mat-icon>cancel</mat-icon></button></mat-chip-row> }
          <input placeholder="Add plan…" [matChipInputFor]="plans" [matChipInputSeparatorKeyCodes]="separators" (matChipInputTokenEnd)="addPlan($event)" />
        </mat-chip-grid><mat-hint>Empty = any explicitly assigned company may use the pool.</mat-hint></mat-form-field>
      <mat-form-field class="full"><mat-label>Description</mat-label><textarea matInput rows="2" [(ngModel)]="f.description" maxlength="1000"></textarea></mat-form-field>
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !f.name.trim()" (click)="save()">{{ data ? 'Save' : 'Create pool' }}</button>
    </mat-dialog-actions>`,
})
export class PoolDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<PoolDialog, boolean>);
  lookups = inject(LookupService);
  data = inject<Pool | null>(MAT_DIALOG_DATA, { optional: true });
  envs = ENVIRONMENTS; algorithms = ALGORITHMS; affinities = AFFINITY_MODES; separators = [ENTER, COMMA];
  f: PoolRequest = this.data ? {
    name: this.data.name, description: this.data.description ?? null, environment: this.data.environment, region: this.data.region ?? null,
    algorithm: this.data.algorithm, affinity: this.data.affinity, failoverPolicyId: this.data.failoverPolicyId ?? null, maxCapacity: this.data.maxCapacity ?? null,
    eligiblePlans: [...this.data.eligiblePlans], applicationIds: [...this.data.applicationIds], concurrencyStamp: this.data.concurrencyStamp,
  } : { name: '', description: null, environment: 'Production', region: null, algorithm: 'WeightedLeastRequests', affinity: 'PerSession',
    failoverPolicyId: null, maxCapacity: null, eligiblePlans: [], applicationIds: [] };
  busy = signal(false);
  errors = signal<string[]>([]);

  addPlan(e: MatChipInputEvent) { const v = e.value.trim(); if (v && !this.f.eligiblePlans.includes(v)) this.f.eligiblePlans = [...this.f.eligiblePlans, v]; e.chipInput.clear(); }
  removePlan(p: string) { this.f.eligiblePlans = this.f.eligiblePlans.filter(x => x !== p); }

  async save() {
    this.busy.set(true); this.errors.set([]);
    const body = { ...this.f, name: this.f.name.trim(), description: this.f.description?.trim() || null, region: this.f.region?.trim() || null };
    try {
      if (this.data) await firstValueFrom(this.api.updatePool(this.data.id, body), { defaultValue: undefined });
      else await firstValueFrom(this.api.createPool(body));
      this.ref.close(true);
    } catch (e) {
      const l = errorList(e);
      this.errors.set(l.length ? l : [(e as { error?: { error?: string } }).error?.error === 'duplicate_name' ? 'A pool with this name already exists.' : errorMessage(e)]);
    } finally { this.busy.set(false); }
  }
}

@Component({
  selector: 'app-pools',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule,
    MatTooltipModule, MatProgressBarModule, StatusChip, HumanizePipe],
  template: `
    <div class="page">
      <div class="page-head"><h1>Server pools</h1><span class="muted">{{ pools().length }}</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (canWrite) { <button mat-flat-button (click)="edit(null)"><mat-icon>add</mat-icon>Create pool</button> }
      </div>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      @for (p of pools(); track p.id) {
        <section class="panel">
          <div class="panel-head">
            <mat-icon class="muted">hub</mat-icon><h2>{{ p.name }}</h2><span class="tag">{{ p.environment }}</span>
            @if (p.region) { <span class="tag">{{ p.region }}</span> }
            <span class="muted small">{{ healthy(p) }} of {{ p.members.length }} member(s) healthy · {{ p.tenants }} tenant(s)</span>
            <span class="spacer"></span>
            @if (canWrite) {
              <button mat-button (click)="edit(p)"><mat-icon>edit</mat-icon>Edit</button>
              <button mat-button class="danger" (click)="remove(p)"><mat-icon>delete</mat-icon>Delete</button>
            }
          </div>
          @if (p.description) { <p class="muted">{{ p.description }}</p> }
          <dl class="kv props">
            <dt>Algorithm</dt><dd>{{ p.algorithm | humanize }}</dd>
            <dt>Affinity</dt><dd>{{ p.affinity | humanize }}</dd>
            <dt>Failover policy</dt><dd>{{ p.failoverPolicy ?? 'None' }}</dd>
            <dt>Capacity</dt><dd>{{ p.maxCapacity ?? 'Sum of members' }}</dd>
            <dt>Eligible plans</dt><dd>@for (x of p.eligiblePlans; track x) { <span class="tag">{{ x }}</span> } @empty { Any assigned company }</dd>
            <dt>Applications</dt><dd>@for (a of p.applicationIds; track a) { <span class="tag">{{ lookups.appCode(a) }}</span> } @empty { All }</dd>
          </dl>
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Server</th><th>Health</th><th>Admin state</th><th class="num">Effective weight</th><th class="num">Weight override</th><th class="num">Priority</th><th>Accepting new sessions</th><th></th></tr></thead>
            <tbody>
              @for (m of p.members; track m.id) {
                <tr>
                  <td><a [routerLink]="['/servers', m.serverId]">{{ m.server }}</a></td>
                  <td><app-status [value]="m.health" /></td><td><app-status [value]="m.adminState" /></td>
                  <td class="num">{{ m.effectiveWeight }}</td>
                  <td class="num">@if (canWrite) {
                      <input class="mini" type="number" min="0" max="10000" [value]="m.weightOverride ?? ''" placeholder="—" (change)="setWeight(p, m, $any($event.target).value)" [attr.aria-label]="'Weight override for ' + m.server" />
                    } @else { {{ m.weightOverride ?? '—' }} }</td>
                  <td class="num">@if (canWrite) {
                      <input class="mini" type="number" min="0" max="1000" [value]="m.priority" (change)="setPriority(p, m, $any($event.target).value)" [attr.aria-label]="'Priority for ' + m.server" />
                    } @else { {{ m.priority }} }</td>
                  <td><mat-slide-toggle [checked]="m.acceptingNewSessions" [disabled]="!canWrite" (change)="setAccepting(p, m, $event.checked)">
                    {{ m.acceptingNewSessions ? 'Accepting' : 'Paused' }}</mat-slide-toggle></td>
                  <td class="num">@if (canWrite) { <button mat-icon-button (click)="removeMember(p, m)" matTooltip="Remove from pool" [attr.aria-label]="'Remove ' + m.server"><mat-icon>remove_circle_outline</mat-icon></button> }</td>
                </tr>
              } @empty { <tr><td colspan="8" class="empty">No members yet.</td></tr> }
            </tbody>
          </table></div>
          @if (canWrite) {
            <div class="row add">
              <mat-form-field class="wide"><mat-label>Add server</mat-label>
                <mat-select [(ngModel)]="draft[p.id].serverId">
                  @for (s of candidates(p); track s.id) { <mat-option [value]="s.id">{{ s.name }} · {{ s.serverType }} · {{ s.health }}</mat-option> }
                  @if (!candidates(p).length) { <mat-option disabled>No eligible servers in {{ p.environment }}</mat-option> }
                </mat-select></mat-form-field>
              <mat-form-field class="narrow"><mat-label>Weight override</mat-label><input matInput type="number" min="0" [(ngModel)]="draft[p.id].weight" /></mat-form-field>
              <mat-form-field class="narrow"><mat-label>Priority</mat-label><input matInput type="number" min="0" [(ngModel)]="draft[p.id].priority" /></mat-form-field>
              <button mat-stroked-button [disabled]="!draft[p.id].serverId" (click)="addMember(p)"><mat-icon>add</mat-icon>Add member</button>
            </div>
          }
        </section>
      } @empty { @if (!loading()) { <div class="panel empty">No pools defined.</div> } }
    </div>`,
  styles: `
    .props { grid-template-columns: 130px 1fr 130px 1fr; margin-bottom: 8px; }
    @media (max-width: 900px) { .props { grid-template-columns: 130px 1fr; } }
    .mini { width: 72px; font: inherit; padding: 3px 6px; border: 1px solid var(--border); border-radius: 4px; background: var(--surface); color: var(--text); text-align: right; }
    .add { margin-top: 8px; flex-wrap: wrap; } .wide { width: 300px; } .narrow { width: 130px; }
  `,
})
export class Pools implements OnInit {
  private api = inject(ApiService);
  private ui = inject(UiService);
  private dialog = inject(MatDialog);
  lookups = inject(LookupService);
  auth = inject(AuthService);
  canWrite = this.auth.can(P.poolsWrite);
  pools = signal<Pool[]>([]);
  loading = signal(false);
  draft: Record<string, { serverId: string; weight: number | null; priority: number }> = {};

  ngOnInit() { this.load(); this.lookups.ensure(['servers', 'applications', 'failoverPolicies']); }

  async load() {
    this.loading.set(true);
    try {
      const list = await firstValueFrom(this.api.pools());
      for (const p of list) this.draft[p.id] ??= { serverId: '', weight: null, priority: 0 };
      this.pools.set(list);
      this.lookups.pools.set(list);
    } catch (e) { this.ui.error(e); } finally { this.loading.set(false); }
  }

  healthy(p: Pool) { return p.members.filter(m => m.health === 'Healthy').length; }
  candidates(p: Pool) {
    const have = new Set(p.members.map(m => m.serverId));
    return this.lookups.servers().filter(s => s.environment === p.environment && s.adminState !== 'Retired' && !have.has(s.id));
  }

  async edit(p: Pool | null) {
    const ok = await firstValueFrom(this.dialog.open(PoolDialog, { data: p, width: '760px', maxWidth: '96vw' }).afterClosed());
    if (ok) { this.ui.ok(p ? 'Pool saved' : 'Pool created'); this.load(); }
  }

  async remove(p: Pool) {
    const ok = await this.ui.confirm({ title: 'Delete pool', message: `Delete ${p.name}? Pools referenced by assignments or failover policies cannot be deleted.`, confirmText: 'Delete', danger: true });
    if (!ok) return;
    if (await this.ui.run(this.api.deletePool(p.id), 'Pool deleted') !== undefined) this.load();
  }

  private async upsert(p: Pool, m: PoolMember, patch: Partial<{ weightOverride: number | null; priority: number; acceptingNewSessions: boolean }>, msg: string) {
    const body = { serverId: m.serverId, weightOverride: m.weightOverride ?? null, priority: m.priority, acceptingNewSessions: m.acceptingNewSessions, ...patch };
    await this.ui.run(this.api.upsertPoolMember(p.id, body), msg);
    this.load();
  }

  setWeight(p: Pool, m: PoolMember, v: string) { this.upsert(p, m, { weightOverride: v === '' ? null : Math.max(0, Math.round(Number(v))) }, 'Weight updated'); }
  setPriority(p: Pool, m: PoolMember, v: string) { this.upsert(p, m, { priority: Math.max(0, Math.round(Number(v) || 0)) }, 'Priority updated'); }
  setAccepting(p: Pool, m: PoolMember, on: boolean) { this.upsert(p, m, { acceptingNewSessions: on }, on ? `${m.server} accepts new sessions` : `${m.server} paused for new sessions`); }

  async addMember(p: Pool) {
    const d = this.draft[p.id];
    if (await this.ui.run(this.api.upsertPoolMember(p.id, { serverId: d.serverId, weightOverride: d.weight ?? null, priority: d.priority ?? 0, acceptingNewSessions: true }), 'Member added') !== undefined) {
      this.draft[p.id] = { serverId: '', weight: null, priority: 0 };
      this.load();
    }
  }

  async removeMember(p: Pool, m: PoolMember) {
    const ok = await this.ui.confirm({ title: 'Remove member', message: `Remove ${m.server} from ${p.name}? Sessions on it are not moved immediately.`, confirmText: 'Remove', danger: true });
    if (!ok) return;
    if (await this.ui.run(this.api.removePoolMember(p.id, m.serverId), 'Member removed') !== undefined) this.load();
  }
}
