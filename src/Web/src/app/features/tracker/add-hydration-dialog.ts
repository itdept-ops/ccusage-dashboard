import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

import { AddHydrationRequest, UnitSystem } from '../../core/models';
import { ozToMl } from './units';

/** Opens with the active date and the user's unit preference (oz for Imperial, ml for Metric). */
export interface AddHydrationData {
  date: string;
  unitSystem: UnitSystem;
}

/**
 * Quick "add a custom drink" dialog. Enters an amount in the user's chosen units (oz/ml) plus an
 * optional drink label (Water/Coffee/Tea/…); converts to metric ml on save. Resolves with an
 * {@link AddHydrationRequest} (date + metric ml + label) for the page to persist.
 */
@Component({
  selector: 'app-add-hydration-dialog',
  imports: [FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title class="hy-title">Add a drink</h2>
    <mat-dialog-content class="hy-body">
      <mat-form-field appearance="outline" class="hy-field">
        <mat-label>Amount</mat-label>
        <input matInput type="number" min="0" step="1" inputmode="decimal" cdkFocusInitial
               [ngModel]="amountDisp()" (ngModelChange)="amountDisp.set($event)" />
        <span matTextSuffix>{{ imperial ? 'oz' : 'ml' }}</span>
        <mat-hint>Logged for {{ data.date }}.</mat-hint>
      </mat-form-field>

      <mat-form-field appearance="outline" class="hy-field">
        <mat-label>Drink (optional)</mat-label>
        <input matInput type="text" maxlength="64" placeholder="Water, Coffee, Tea…"
               [ngModel]="label()" (ngModelChange)="label.set($event)" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions class="hy-actions" align="end">
      <button mat-stroked-button type="button" (click)="cancel()">Cancel</button>
      <button mat-flat-button type="button" color="primary" [disabled]="!canSave()" (click)="save()">Add</button>
    </mat-dialog-actions>
  `,
  styles: `
    .hy-title { font-family: var(--tech-font-ui); font-weight: 700; color: var(--tech-text); }
    .hy-body { min-width: min(320px, 80vw); padding-top: 4px !important;
      display: flex; flex-direction: column; gap: var(--tech-space-2); }
    .hy-field { width: 100%; }
    .hy-actions { padding: var(--tech-space-3) var(--tech-space-4); gap: 8px;
      button { border-radius: var(--tech-r-control); font-weight: 600; min-height: 44px; } }
  `,
})
export class AddHydrationDialog {
  private ref = inject(MatDialogRef<AddHydrationDialog, AddHydrationRequest>);
  readonly data = inject<AddHydrationData>(MAT_DIALOG_DATA);

  readonly imperial = this.data.unitSystem === 'Imperial';

  readonly amountDisp = signal<number | null>(null);
  readonly label = signal<string>('');

  /** Metric ml from the entered display value (1..5000 ml server-validated range). */
  private readonly ml = computed<number | null>(() => {
    const d = this.amountDisp();
    if (d == null || d <= 0) return null;
    const m = Math.round(this.imperial ? ozToMl(d) : d);
    return m >= 1 && m <= 5000 ? m : null;
  });

  readonly canSave = computed(() => this.ml() != null);

  save(): void {
    const ml = this.ml();
    if (ml == null) return;
    const label = this.label().trim();
    this.ref.close({ date: this.data.date, amountMl: ml, label: label || undefined });
  }

  cancel(): void {
    this.ref.close();
  }
}
