import { Component, inject } from '@angular/core';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/** A small, warm yes/no confirm used for destructive family actions (delete a note / list / item). */
export interface ConfirmData {
  title: string;
  message: string;
  confirmLabel?: string;
  /** When true, the confirm button reads as destructive (warn colour). */
  destructive?: boolean;
}

@Component({
  selector: 'app-family-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title class="confirm__title">{{ data.title }}</h2>
    <mat-dialog-content class="confirm__body">{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end" class="confirm__actions">
      <button mat-stroked-button type="button" (click)="ref.close(false)">Keep it</button>
      <button mat-flat-button type="button" [color]="data.destructive ? 'warn' : 'primary'"
              cdkFocusInitial (click)="ref.close(true)">
        {{ data.confirmLabel ?? 'Delete' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .confirm__title { font-family: var(--tech-font-ui); font-weight: 700; color: var(--tech-text); }
    .confirm__body { min-width: min(360px, 80vw); color: var(--tech-text-secondary);
      font-size: var(--tech-fs-body); line-height: 1.5; }
    .confirm__actions { padding: var(--tech-space-3, 12px) var(--tech-space-4, 16px); gap: 8px;
      button { border-radius: var(--tech-r-control); font-weight: 600; min-height: 42px; } }
  `,
})
export class FamilyConfirmDialog {
  readonly ref = inject(MatDialogRef<FamilyConfirmDialog, boolean>);
  readonly data = inject<ConfirmData>(MAT_DIALOG_DATA);
}
