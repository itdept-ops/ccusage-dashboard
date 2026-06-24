import { CommonModule } from '@angular/common';
import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { MAT_DIALOG_DATA, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import {
  CreateShareRequest,
  GroupBy,
  ShareAccessItem,
  ShareCreated,
  ShareListItem,
  UsageFilter,
} from '../../core/models';

@Component({
  selector: 'app-share-dialog',
  imports: [
    CommonModule,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
  ],
  templateUrl: './share-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './share-dialog.scss',
})
export class ShareDialog {
  private api = inject(Api);
  private snack = inject(MatSnackBar);
  readonly data = inject<{ filter: UsageFilter; groupBy: GroupBy }>(MAT_DIALOG_DATA);

  readonly expiryOptions = [
    { h: 1, l: '1 hour' },
    { h: 24, l: '24 hours' },
    { h: 168, l: '7 days' },
    { h: 720, l: '30 days' },
  ];
  readonly expiryHours = signal(168);
  readonly label = signal('');
  readonly creating = signal(false);
  readonly created = signal<ShareCreated | null>(null);
  readonly shares = signal<ShareListItem[]>([]);

  // Per-view detail (expanded link)
  readonly expandedId = signal<number | null>(null);
  readonly accesses = signal<ShareAccessItem[]>([]);
  readonly loadingAccesses = signal(false);

  // Inline edit (expiry / label)
  readonly editingId = signal<number | null>(null);
  readonly editExpiry = signal(168);
  readonly editLabel = signal('');
  readonly savingEdit = signal(false);

  constructor() {
    this.loadShares();
  }

  copyExisting(s: ShareListItem): void {
    if (!s.path) {
      this.snack.open('This older link can’t be re-copied — create a new one.', 'OK', {
        duration: 4000,
      });
      return;
    }
    navigator.clipboard?.writeText(this.fullUrl(s.path)).then(
      () => this.snack.open('Public link copied', 'OK', { duration: 3000 }),
      () => this.snack.open('Could not copy link', 'Dismiss', { duration: 3000 }),
    );
  }

  startEdit(s: ShareListItem): void {
    this.editingId.set(s.id);
    this.editLabel.set(s.label ?? '');
    this.editExpiry.set(168);
  }

  cancelEdit(): void {
    this.editingId.set(null);
  }

  saveEdit(s: ShareListItem): void {
    this.savingEdit.set(true);
    this.api
      .updateShare(s.id, {
        expiresInHours: this.editExpiry(),
        label: this.editLabel().trim() || null,
      })
      .subscribe({
        next: (updated) => {
          this.savingEdit.set(false);
          this.editingId.set(null);
          this.shares.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
          this.snack.open('Link updated', 'OK', { duration: 2500 });
        },
        error: () => {
          this.savingEdit.set(false);
          this.snack.open('Update failed', 'Dismiss', { duration: 4000 });
        },
      });
  }

  toggleViews(s: ShareListItem): void {
    if (this.expandedId() === s.id) {
      this.expandedId.set(null);
      return;
    }
    this.expandedId.set(s.id);
    this.accesses.set([]);
    this.loadingAccesses.set(true);
    this.api.shareAccesses(s.id).subscribe({
      next: (a) => {
        this.accesses.set(a);
        this.loadingAccesses.set(false);
      },
      error: () => this.loadingAccesses.set(false),
    });
  }

  private loadShares(): void {
    this.api.listShares().subscribe({
      next: (s) => this.shares.set(s),
      error: () => {
        /* non-critical */
      },
    });
  }

  fullUrl(path: string): string {
    return window.location.origin + path;
  }

  create(): void {
    this.creating.set(true);
    const f = this.data.filter;
    const body: CreateShareRequest = {
      label: this.label().trim() || null,
      expiresInHours: this.expiryHours(),
      from: f.from,
      to: f.to,
      projectId: f.projectIds,
      model: f.models,
      source: f.sources,
      includeSidechain: f.includeSidechain,
      groupBy: this.data.groupBy,
    };
    this.api.createShare(body).subscribe({
      next: (c) => {
        this.creating.set(false);
        this.created.set(c);
        this.loadShares();
      },
      error: (e: HttpErrorResponse) => {
        this.creating.set(false);
        this.snack.open(e.error?.message ?? 'Could not create link', 'Dismiss', { duration: 5000 });
      },
    });
  }

  copy(c: ShareCreated): void {
    navigator.clipboard?.writeText(this.fullUrl(c.path)).then(
      () => this.snack.open('Public link copied', 'OK', { duration: 3000 }),
      () => this.snack.open('Could not copy link', 'Dismiss', { duration: 3000 }),
    );
  }

  revoke(s: ShareListItem): void {
    if (!confirm('Revoke this link? Anyone holding it loses access immediately.')) return;
    this.api.deleteShare(s.id).subscribe({
      next: () => {
        this.shares.update((list) => list.filter((x) => x.id !== s.id));
        if (this.created()?.id === s.id) this.created.set(null);
      },
      error: () => this.snack.open('Could not revoke', 'Dismiss', { duration: 4000 }),
    });
  }

  reset(): void {
    this.created.set(null);
    this.label.set('');
  }
}
