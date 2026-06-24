import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Api } from '../../core/api';
import { QuickAddKind, QuickAddResult } from '../../core/models';

/** A kind choice for the toggle. `auto` lets the server route by the text; the rest force a kind. */
const KINDS: { value: QuickAddKind; label: string; icon: string }[] = [
  { value: 'auto', label: 'Auto', icon: 'auto_awesome' },
  { value: 'list', label: 'List', icon: 'checklist' },
  { value: 'reminder', label: 'Reminder', icon: 'notifications' },
  { value: 'note', label: 'Note', icon: 'sticky_note_2' },
];

/**
 * Compact family Quick-Add: one text line, a kind toggle (Auto / List / Reminder / Note, default Auto),
 * an optional list name (shown only when List is chosen), and Save. Saving POSTs to /api/family/quick-add
 * and closes with the {@link QuickAddResult} so the shell can toast the warm summary. The dialog posts
 * itself (it owns the in-flight + error state) and is intentionally tiny + mobile-friendly. Enter saves.
 */
@Component({
  selector: 'app-quick-add-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonToggleModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './quick-add-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './quick-add-dialog.scss',
})
export class QuickAddDialog {
  private api = inject(Api);
  readonly ref = inject(MatDialogRef<QuickAddDialog, QuickAddResult>);

  readonly kinds = KINDS;

  readonly text = signal('');
  readonly kind = signal<QuickAddKind>('auto');
  readonly listName = signal('');

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly showListName = computed(() => this.kind() === 'list');
  readonly canSave = computed(() => this.text().trim().length > 0 && !this.saving());

  save(): void {
    if (!this.canSave()) return;
    this.saving.set(true);
    this.error.set(null);
    const listName = this.kind() === 'list' ? this.listName() : undefined;
    this.api.quickAdd(this.text().trim(), this.kind(), listName).subscribe({
      next: (result) => this.ref.close(result),
      error: () => {
        this.saving.set(false);
        this.error.set("Couldn't save that just now — please try again.");
      },
    });
  }
}
