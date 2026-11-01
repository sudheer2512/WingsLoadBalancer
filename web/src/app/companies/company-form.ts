import { COMMA, ENTER } from '@angular/cdk/keycodes';
import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipInputEvent, MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { errorList, errorMessage } from '../core/auth.service';
import { AUTH_MODES, COMPANY_ROUTING_MODES, COMPANY_STATUSES, CompanyDetail, CompanyRequest, ENVIRONMENTS } from '../core/models';
import { LookupService } from '../core/ui.service';
import { HumanizePipe } from '../shared/display';

export function emptyCompany(): CompanyRequest {
  return { code: '', name: '', servicePlan: null, routingMode: 'Dedicated', status: 'Active', defaultEnvironment: 'Production',
    maxConcurrentUsers: null, authenticationMode: 'Local', routingPolicyId: null, notes: null, aliases: [] };
}

export function companyToRequest(c: CompanyDetail): CompanyRequest {
  return { code: c.code, name: c.name, servicePlan: c.servicePlan ?? null, routingMode: c.routingMode, status: c.status,
    defaultEnvironment: c.defaultEnvironment, maxConcurrentUsers: c.maxConcurrentUsers ?? null, authenticationMode: c.authenticationMode,
    routingPolicyId: c.routingPolicyId ?? null, notes: c.notes ?? null, aliases: [...c.aliases], concurrencyStamp: c.concurrencyStamp };
}

export function cleanCompany(r: CompanyRequest): CompanyRequest {
  const blank = (v?: string | null) => (v && v.trim() ? v.trim() : null);
  return { ...r, code: r.code.trim(), name: r.name.trim(), servicePlan: blank(r.servicePlan), notes: blank(r.notes), maxConcurrentUsers: r.maxConcurrentUsers ?? null };
}

@Component({
  selector: 'app-company-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatChipsModule, MatIconModule, HumanizePipe],
  template: `
    @let f = model();
    <div class="grid-3">
      <mat-form-field><mat-label>Company code</mat-label><input matInput [(ngModel)]="f.code" required maxlength="64" [disabled]="readonly()" />
        <mat-hint>Users sign in with this code or an alias.</mat-hint></mat-form-field>
      <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required maxlength="200" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Service plan</mat-label><input matInput [(ngModel)]="f.servicePlan" maxlength="100" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Status</mat-label>
        <mat-select [(ngModel)]="f.status" [disabled]="readonly()">@for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field><mat-label>Routing mode</mat-label>
        <mat-select [(ngModel)]="f.routingMode" [disabled]="readonly()">@for (m of modes; track m) { <mat-option [value]="m">{{ m | humanize }}</mat-option> }</mat-select>
        <mat-hint>Normally set by assignments.</mat-hint></mat-form-field>
      <mat-form-field><mat-label>Default environment</mat-label>
        <mat-select [(ngModel)]="f.defaultEnvironment" [disabled]="readonly()">@for (e of envs; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field><mat-label>Max concurrent users</mat-label><input matInput type="number" min="0" [(ngModel)]="f.maxConcurrentUsers" [disabled]="readonly()" /></mat-form-field>
      <mat-form-field><mat-label>Authentication</mat-label>
        <mat-select [(ngModel)]="f.authenticationMode" [disabled]="readonly()">@for (a of authModes; track a) { <mat-option [value]="a">{{ a | humanize }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field><mat-label>Routing policy</mat-label>
        <mat-select [(ngModel)]="f.routingPolicyId" [disabled]="readonly()"><mat-option [value]="null">Default policy</mat-option>
          @for (p of lookups.routingPolicies(); track p.id) { <mat-option [value]="p.id">{{ p.name }}{{ p.isDefault ? ' (default)' : '' }}</mat-option> }</mat-select></mat-form-field>
    </div>
    <mat-form-field class="full"><mat-label>Aliases</mat-label>
      <mat-chip-grid #chips [disabled]="readonly()" aria-label="Company aliases">
        @for (a of f.aliases; track a) {
          <mat-chip-row (removed)="removeAlias(a)" [editable]="false">{{ a }}
            @if (!readonly()) { <button matChipRemove [attr.aria-label]="'Remove ' + a"><mat-icon>cancel</mat-icon></button> }</mat-chip-row>
        }
        <input placeholder="Add alias…" [matChipInputFor]="chips" [matChipInputSeparatorKeyCodes]="separators" (matChipInputTokenEnd)="addAlias($event)" [disabled]="readonly()" />
      </mat-chip-grid>
      <mat-hint>Alternative identifiers users may type as the company. Press Enter or comma to add.</mat-hint>
    </mat-form-field>
    <mat-form-field class="full"><mat-label>Notes</mat-label><textarea matInput rows="2" [(ngModel)]="f.notes" maxlength="4000" [disabled]="readonly()"></textarea></mat-form-field>`,
})
export class CompanyForm implements OnInit {
  lookups = inject(LookupService);
  model = input.required<CompanyRequest>();
  readonly = input(false);
  statuses = COMPANY_STATUSES; modes = COMPANY_ROUTING_MODES; envs = ENVIRONMENTS; authModes = AUTH_MODES;
  separators = [ENTER, COMMA];
  ngOnInit() { this.lookups.ensure(['routingPolicies']); }

  addAlias(e: MatChipInputEvent) {
    const v = (e.value ?? '').trim();
    const f = this.model();
    if (v && !f.aliases.some(a => a.toLowerCase() === v.toLowerCase())) f.aliases = [...f.aliases, v];
    e.chipInput.clear();
  }
  removeAlias(a: string) { const f = this.model(); f.aliases = f.aliases.filter(x => x !== a); }
}

@Component({
  selector: 'app-company-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, CompanyForm],
  template: `
    <h2 mat-dialog-title>Add company</h2>
    <mat-dialog-content class="dialog-wide"><app-company-form [model]="form" />
      @if (errors().length) { <div class="callout bad"><ul>@for (e of errors(); track e) { <li>{{ e }}</li> }</ul></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="busy() || !form.code.trim() || !form.name.trim()" (click)="save()">Add company</button>
    </mat-dialog-actions>`,
})
export class CompanyDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CompanyDialog, string>);
  data = inject<CompanyRequest | null>(MAT_DIALOG_DATA, { optional: true });
  form: CompanyRequest = this.data ?? emptyCompany();
  busy = signal(false);
  errors = signal<string[]>([]);
  async save() {
    this.busy.set(true); this.errors.set([]);
    try { this.ref.close((await firstValueFrom(this.api.createCompany(cleanCompany(this.form)))).id); }
    catch (e) { const l = errorList(e); this.errors.set(l.length ? l : [errorMessage(e)]); }
    finally { this.busy.set(false); }
  }
}
