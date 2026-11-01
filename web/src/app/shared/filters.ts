import { ChangeDetectionStrategy, Component, OnInit, inject, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { DashboardFilter, ENVIRONMENTS } from '../core/models';
import { LookupService, fromLocalInput, toLocalInput } from '../core/ui.service';

/** Company / server / pool / application / environment / date-range filter row shared by dashboard and history. */
@Component({
  selector: 'app-decision-filter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, MatInputModule, MatButtonModule, MatIconModule],
  template: `
    <div class="filters">
      <mat-form-field><mat-label>Company</mat-label>
        <mat-select [ngModel]="value().companyId ?? ''" (ngModelChange)="set('companyId', $event)">
          <mat-option value="">All companies</mat-option>
          @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.code }} · {{ c.name }}</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field><mat-label>Server</mat-label>
        <mat-select [ngModel]="value().serverId ?? ''" (ngModelChange)="set('serverId', $event)">
          <mat-option value="">All servers</mat-option>
          @for (s of lookups.servers(); track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field><mat-label>Pool</mat-label>
        <mat-select [ngModel]="value().poolId ?? ''" (ngModelChange)="set('poolId', $event)">
          <mat-option value="">All pools</mat-option>
          @for (p of lookups.pools(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field><mat-label>Application</mat-label>
        <mat-select [ngModel]="value().applicationId ?? ''" (ngModelChange)="set('applicationId', $event)">
          <mat-option value="">All applications</mat-option>
          @for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.code }}</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field><mat-label>Environment</mat-label>
        <mat-select [ngModel]="value().environment ?? ''" (ngModelChange)="set('environment', $event)">
          <mat-option value="">All environments</mat-option>
          @for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }
        </mat-select></mat-form-field>
      @if (showUsername()) {
        <mat-form-field><mat-label>Username contains</mat-label>
          <input matInput [ngModel]="value().username ?? ''" (change)="set('username', $any($event.target).value)" /></mat-form-field>
      }
      <mat-form-field class="wide"><mat-label>From</mat-label>
        <input matInput type="datetime-local" [ngModel]="local(value().from)" (ngModelChange)="set('from', iso($event))" /></mat-form-field>
      <mat-form-field class="wide"><mat-label>To</mat-label>
        <input matInput type="datetime-local" [ngModel]="local(value().to)" (ngModelChange)="set('to', iso($event))" /></mat-form-field>
      <ng-content />
      <button mat-button type="button" (click)="clear()"><mat-icon>filter_alt_off</mat-icon>Clear</button>
    </div>`,
})
export class DecisionFilter implements OnInit {
  lookups = inject(LookupService);
  value = model<DashboardFilter>({});
  showUsername = input(false);
  changed = output<DashboardFilter>();
  envs = ENVIRONMENTS;

  ngOnInit() { this.lookups.ensure(['companies', 'servers', 'pools', 'applications']); }

  local(v?: string) { return toLocalInput(v); }
  iso(v: string) { return fromLocalInput(v) ?? undefined; }

  set<K extends keyof DashboardFilter>(key: K, v: DashboardFilter[K]) {
    const next = { ...this.value(), [key]: v || undefined };
    this.value.set(next);
    this.changed.emit(next);
  }

  clear() {
    this.value.set({});
    this.changed.emit({});
  }
}
