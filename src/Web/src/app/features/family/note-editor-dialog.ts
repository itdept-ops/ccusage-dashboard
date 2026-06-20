import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

import { FamilyNote } from '../../core/models';
import { renderMarkdown } from './markdown';

/** Open with an existing note to edit, or null/undefined to create a fresh one. */
export interface NoteEditorData {
  note: FamilyNote | null;
}

/** The values the editor resolves with (the page persists via create/update). */
export interface NoteEditorResult {
  title: string;
  body: string;
  pinned: boolean;
}

/**
 * Create / edit a family note. A simple markdown textarea with a live preview side-by-side (stacked on
 * mobile), a title field, and a pin toggle. Resolves with {title, body, pinned}; the page calls the create
 * or update endpoint. Read-only notes never open this — the board opens it only when the caller can edit.
 */
@Component({
  selector: 'app-note-editor-dialog',
  imports: [
    FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule,
    MatSlideToggleModule,
  ],
  templateUrl: './note-editor-dialog.html',
  styleUrl: './family.scss',
})
export class NoteEditorDialog {
  private ref = inject(MatDialogRef<NoteEditorDialog, NoteEditorResult>);
  private sanitizer = inject(DomSanitizer);
  readonly data = inject<NoteEditorData>(MAT_DIALOG_DATA);

  readonly isEdit = !!this.data.note;
  readonly title = signal(this.data.note?.title ?? '');
  readonly body = signal(this.data.note?.body ?? '');
  readonly pinned = signal(this.data.note?.pinned ?? false);

  /** Live, sanitized markdown preview of the body. renderMarkdown escapes first, so this is safe HTML. */
  readonly preview = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(this.body())));

  readonly canSave = computed(() => this.title().trim().length > 0 || this.body().trim().length > 0);

  save(): void {
    if (!this.canSave()) return;
    this.ref.close({
      title: this.title().trim(),
      body: this.body(),
      pinned: this.pinned(),
    });
  }

  cancel(): void {
    this.ref.close();
  }
}
