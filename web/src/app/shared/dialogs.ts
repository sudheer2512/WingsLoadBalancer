import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';

export interface ConfirmData {
  title: string;
  message: string;
  confirmText?: string;
  danger?: boolean;
  /** Shows an optional "reason" field (sent as X-Audit-Reason). */
  reason?: boolean;
  reasonRequired?: boolean;
  /** Optional checkbox (e.g. "also revoke sessions"). */
  checkbox?: string;
  checkboxDefault?: boolean;
  items?: string[];
}
export interface ConfirmResult { reason: string; checked: boolean; }

@Component({
  selector: 'app-confirm-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatCheckboxModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="pre">{{ data.message }}</p>
      @if (data.items?.length) { <ul>@for (i of data.items; track $index) { <li>{{ i }}</li> }</ul> }
      @if (data.checkbox) { <p><mat-checkbox [(ngModel)]="checked">{{ data.checkbox }}</mat-checkbox></p> }
      @if (data.reason) {
        <mat-form-field class="full">
          <mat-label>Reason {{ data.reasonRequired ? '' : '(optional, recorded in the audit log)' }}</mat-label>
          <textarea matInput rows="2" [(ngModel)]="reason" maxlength="500" cdkFocusInitial></textarea>
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger]="data.danger" [disabled]="data.reasonRequired && !reason.trim()"
              (click)="ref.close({ reason: reason.trim(), checked })">{{ data.confirmText ?? 'Confirm' }}</button>
    </mat-dialog-actions>`,
})
export class ConfirmDialog {
  data = inject<ConfirmData>(MAT_DIALOG_DATA);
  ref = inject(MatDialogRef<ConfirmDialog, ConfirmResult>);
  reason = '';
  checked = this.data.checkboxDefault ?? false;
}

export interface PromptData {
  title: string; label: string; value?: string; required?: boolean; multiline?: boolean; hint?: string; confirmText?: string;
  type?: 'text' | 'password' | 'number'; minLength?: number;
}

@Component({
  selector: 'app-prompt-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <mat-form-field class="full">
        <mat-label>{{ data.label }}</mat-label>
        @if (data.multiline) {
          <textarea matInput rows="4" [(ngModel)]="value" cdkFocusInitial></textarea>
        } @else {
          <input matInput [type]="data.type ?? 'text'" [(ngModel)]="value" (keydown.enter)="valid() && ref.close(value.trim())" cdkFocusInitial
                 [attr.autocomplete]="data.type === 'password' ? 'new-password' : 'off'" />
        }
        @if (data.hint) { <mat-hint>{{ data.hint }}</mat-hint> }
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [disabled]="!valid()" (click)="ref.close(value.trim())">{{ data.confirmText ?? 'OK' }}</button>
    </mat-dialog-actions>`,
})
export class PromptDialog {
  data = inject<PromptData>(MAT_DIALOG_DATA);
  ref = inject(MatDialogRef<PromptDialog>);
  value = this.data.value ?? '';
  valid() {
    const v = String(this.value ?? '').trim();
    if (this.data.required !== false && !v) return false;
    return !this.data.minLength || v.length >= this.data.minLength;
  }
}

/** Shows sensitive values exactly once, with a warning and copy buttons. */
@Component({
  selector: 'app-secret-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <div class="callout warn"><strong>Shown once.</strong> {{ data.warning }}</div>
      @for (f of data.fields; track f.label) {
        <div class="secret">
          <div class="muted small">{{ f.label }}</div>
          <div class="row"><code class="mono value">{{ f.value }}</code>
            <button mat-icon-button type="button" (click)="copy(f.value)" aria-label="Copy {{ f.label }}"><mat-icon>content_copy</mat-icon></button></div>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-flat-button mat-dialog-close>I have stored these values</button></mat-dialog-actions>`,
  styles: `.secret { margin: 10px 0; } .value { flex: 1; word-break: break-all; padding: 6px 8px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 4px; }`,
})
export class SecretDialog {
  data = inject<{ title: string; warning: string; fields: { label: string; value: string }[] }>(MAT_DIALOG_DATA);
  copy(v: string) { navigator.clipboard?.writeText(v).catch(() => undefined); }
}
