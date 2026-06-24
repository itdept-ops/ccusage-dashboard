import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
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
 * Input contract. For the MACHINE dimension `rawValue` is the RAW machine name to act on (already mapped
 * from the "local" display row to the empty string by the caller). For the USER dimension the client
 * holds no emails — `userId` is the row's AppUser id (or null for the local/orphan bucket) and the server
 * resolves it to the raw owner email. `label` is the friendly name for prose. `others` are the OTHER
 * buckets in the same board — the reassign picker's options (each carries `userId` for user targets).
 */
export interface FleetActionData {
  action: FleetAction;
  dimension: FleetDimension;
  /** Machine dimension: the raw machine name. Unused (empty) for the user dimension. */
  rawValue: string;
  /** User dimension: the row's AppUser id, or null for the local/orphan bucket. */
  userId?: number | null;
  label: string;
  records: number;
  /** Other existing buckets on this board — combine/transfer targets (reassign only). `userId` is set
   * for user targets (null = local); `rawValue` is the raw name for machine targets. */
  others: { rawValue: string; label: string; userId?: number | null }[];
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
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
  ],
  templateUrl: './fleet-action-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
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
  /** Index into `data.others` of the selected existing target (dimension-agnostic; user targets share an
   * empty rawValue, so we key the picker by index rather than value). */
  readonly targetExistingIdx = signal<number>(0);
  /** Free-text new/another name (MACHINE dimension only — users can't be typed since there's no email). */
  readonly targetNew = signal<string>('');

  readonly kindNoun = computed(() => (this.data.dimension === 'machine' ? 'machine' : 'user'));

  /** The chosen existing target entry, or undefined when none. */
  private chosenExisting() {
    return this.data.others[this.targetExistingIdx()];
  }

  /** The resolved RAW machine target for a machine reassign. */
  private resolvedMachineTarget(): string {
    return this.targetMode() === 'existing'
      ? (this.chosenExisting()?.rawValue ?? '')
      : this.targetNew().trim();
  }

  /** The resolved target user id for a user reassign (null = local). Existing pick only; users can't be typed. */
  private resolvedUserTargetId(): number | null {
    return this.chosenExisting()?.userId ?? null;
  }

  /** The source user id(s) for a user mutation: the row's id when present (the local/orphan bucket has none). */
  private userIds(): number[] {
    return this.data.userId != null ? [this.data.userId] : [];
  }

  /** Whether the reassign target is a valid, non-self choice. */
  readonly canReassign = computed(() => {
    if (this.targetMode() === 'existing') {
      // An existing pick is always a different bucket; still guard against an empty option list.
      return this.data.others.length > 0;
    }
    // Free-text "new name" is MACHINE-only (a user has no typeable email on the client).
    if (this.data.dimension !== 'machine') return false;
    const t = this.targetNew().trim();
    // A new name must be non-empty and not just rename the bucket onto itself.
    return t.length > 0 && t.toLowerCase() !== this.data.label.toLowerCase();
  });

  /** Friendly label for the chosen reassign target (for the confirm prose). */
  readonly targetLabel = computed(() => {
    if (this.targetMode() === 'existing') {
      return this.chosenExisting()?.label ?? '';
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

    const isUser = this.data.dimension === 'user';

    if (this.data.action === 'reassign') {
      // User dimension: source + target are user IDs (the server resolves id -> owner email). Machine
      // dimension: raw names, with a "" target meaning re-label to local.
      const body = isUser
        ? {
            dimension: 'user' as const,
            userIds: this.userIds(),
            toUserId: this.resolvedUserTargetId(),
          }
        : {
            dimension: 'machine' as const,
            from: [this.data.rawValue],
            to: this.resolvedMachineTarget(),
          };
      this.api
        .reassignFleet(body)
        .subscribe({
          next: (r) => this.ref.close({ action: 'reassign', count: r.affected }),
          error: fail,
        });
    } else if (this.data.action === 'delete') {
      const body = isUser
        ? { dimension: 'user' as const, userIds: this.userIds() }
        : { dimension: 'machine' as const, names: [this.data.rawValue] };
      this.api
        .deleteFleet(body)
        .subscribe({
          next: (r) => this.ref.close({ action: 'delete', count: r.deleted }),
          error: fail,
        });
    } else {
      // Revoke is user-only; send the row's user id (server resolves it to the owner email).
      this.api
        .revokeFleetKeys({ userId: this.data.userId ?? 0 })
        .subscribe({
          next: (r) => this.ref.close({ action: 'revoke', count: r.revoked }),
          error: fail,
        });
    }
  }
}
