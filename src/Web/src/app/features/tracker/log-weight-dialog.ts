import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

import { LogWeightRequest, UnitSystem } from '../../core/models';
import { kgToLb, lbToKg } from './units';

/** Opens with the active date, the user's unit preference, and (optionally) the current weight to prefill. */
export interface LogWeightData {
  date: string;
  unitSystem: UnitSystem;
  /** The profile's current weight in kg, used to prefill the field. */
  currentKg?: number | null;
}

/**
 * Quick "log today's weight" dialog. Enters a weight in the user's chosen units; converts to metric kg
 * on save. Resolves with a {@link LogWeightRequest} (date + metric kg) for the page to persist.
 */
@Component({
  selector: 'app-log-weight-dialog',
  imports: [FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title class="lw-title">Log weight</h2>
    <mat-dialog-content class="lw-body">
      <mat-form-field appearance="outline" class="lw-field">
        <mat-label>Weight</mat-label>
        <input matInput type="number" min="0" step="0.1" inputmode="decimal" cdkFocusInitial
               [ngModel]="weightDisp()" (ngModelChange)="weightDisp.set($event)" />
        <span matTextSuffix>{{ imperial ? 'lb' : 'kg' }}</span>
        <mat-hint>Recorded for {{ data.date }}. One entry per day.</mat-hint>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions class="lw-actions" align="end">
      <button mat-stroked-button type="button" (click)="cancel()">Cancel</button>
      <button mat-flat-button type="button" color="primary" [disabled]="!canSave()" (click)="save()">Save</button>
    </mat-dialog-actions>
  `,
  styles: `
    .lw-title { font-family: var(--tech-font-ui); font-weight: 700; color: var(--tech-text); }
    .lw-body { min-width: min(320px, 80vw); padding-top: 4px !important; }
    .lw-field { width: 100%; }
    .lw-actions { padding: var(--tech-space-3) var(--tech-space-4); gap: 8px;
      button { border-radius: var(--tech-r-control); font-weight: 600; min-height: 44px; } }
  `,
})
export class LogWeightDialog {
  private ref = inject(MatDialogRef<LogWeightDialog, LogWeightRequest>);
  readonly data = inject<LogWeightData>(MAT_DIALOG_DATA);

  readonly imperial = this.data.unitSystem === 'Imperial';

  readonly weightDisp = signal<number | null>(
    this.data.currentKg != null
      ? (this.imperial ? Math.round(kgToLb(this.data.currentKg) * 10) / 10 : Math.round(this.data.currentKg * 10) / 10)
      : null,
  );

  /** Metric kg from the entered display value (1..1000 kg sane range). */
  private readonly kg = computed<number | null>(() => {
    const d = this.weightDisp();
    if (d == null || d <= 0) return null;
    const k = this.imperial ? lbToKg(d) : d;
    return k >= 1 && k <= 1000 ? Math.round(k * 100) / 100 : null;
  });

  readonly canSave = computed(() => this.kg() != null);

  save(): void {
    const kg = this.kg();
    if (kg == null) return;
    this.ref.close({ date: this.data.date, weightKg: kg });
  }

  cancel(): void {
    this.ref.close();
  }
}
