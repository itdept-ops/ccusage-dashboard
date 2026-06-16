import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { Api } from '../../core/api';
import { FleetDimension } from '../../core/models';

/** Which management action the dialog drives. */
export type FleetAction = 'reassign' | 'delete' | 'revoke';

/**
 * Input contract. `rawValue` is the RAW dimension value to act on (already mapped from the "local"
 * display row to the empty string by the caller). `label` is the friendly name for prose. `others`
 * are the OTHER buckets in the same board (as { rawValue, label }) — the reassign picker's options.
 */
export interface FleetActionData {
  action: FleetAction;
  dimension: FleetDimension;
  rawValue: string;
  label: string;
  records: number;
  /** Other existing buckets on this board — combine/transfer targets (reassign only). */
  others: { rawValue: string; label: string }[];
}

/** What the dialog resolves with on success — fed straight into the page's snackbar. */
export interface FleetActionResult {
  action: FleetAction;
  count: number;
}

/**
 * One dialog for all three fleet mutations (combine/move, delete, revoke key). It owns the API call
 * and closes with a {@link FleetActionResult} on success so the page can refresh + toast; on error it
 * surfaces the message inline and stays open. Reuses the shared dialog/--tech-* patterns.
 */
@Component({
  selector: 'app-fleet-action-dialog',
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatButtonModule, MatIconModule,
  ],
  templateUrl: './fleet-action-dialog.html',
  styleUrl: './fleet-action-dialog.scss',
})
export class FleetActionDialog {
  private api = inject(Api);
  private ref = inject(MatDialogRef<FleetActionDialog, FleetActionResult>);
  readonly data = inject<FleetActionData>(MAT_DIALOG_DATA);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  // ---- reassign target selection ----
  /** 'existing' = pick another bucket; 'new' = type a fresh/another name. */
  readonly targetMode = signal<'existing' | 'new'>(this.data.others.length ? 'existing' : 'new');
  /** Selected existing target (its RAW value). */
  readonly targetExisting = signal<string>(this.data.others[0]?.rawValue ?? '');
  /** Free-text new/another name. */
  readonly targetNew = signal<string>('');

  readonly kindNoun = computed(() => this.data.dimension === 'machine' ? 'machine' : 'user');

  /** The resolved RAW target value for a reassign. */
  private resolvedTarget(): string {
    return this.targetMode() === 'existing' ? this.targetExisting() : this.targetNew().trim();
  }

  /** Whether the reassign target is a valid, non-self choice. */
  readonly canReassign = computed(() => {
    if (this.targetMode() === 'existing') {
      // An existing pick is always a different bucket; still guard against an empty option list.
      return this.data.others.length > 0;
    }
    const t = this.targetNew().trim();
    // A new name must be non-empty and not just rename the bucket onto itself.
    return t.length > 0 && t.toLowerCase() !== this.data.label.toLowerCase();
  });

  /** Friendly label for the chosen reassign target (for the confirm prose). */
  readonly targetLabel = computed(() => {
    if (this.targetMode() === 'existing') {
      return this.data.others.find(o => o.rawValue === this.targetExisting())?.label ?? '';
    }
    return this.targetNew().trim();
  });

  confirm(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);

    const fail = (e: HttpErrorResponse) => {
      this.busy.set(false);
      this.error.set(e.error?.message ?? 'The action could not be completed. Please try again.');
    };

    if (this.data.action === 'reassign') {
      this.api.reassignFleet({ dimension: this.data.dimension, from: [this.data.rawValue], to: this.resolvedTarget() })
        .subscribe({ next: r => this.ref.close({ action: 'reassign', count: r.affected }), error: fail });
    } else if (this.data.action === 'delete') {
      this.api.deleteFleet({ dimension: this.data.dimension, names: [this.data.rawValue] })
        .subscribe({ next: r => this.ref.close({ action: 'delete', count: r.deleted }), error: fail });
    } else {
      this.api.revokeFleetKeys({ email: this.data.rawValue })
        .subscribe({ next: r => this.ref.close({ action: 'revoke', count: r.revoked }), error: fail });
    }
  }
}
