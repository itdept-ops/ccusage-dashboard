import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

import { Api } from '../../core/api';
import { FamilyNote, NoteDraftAiResult } from '../../core/models';
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

/** An AI draft/rewrite awaiting the user's Use / Try-again decision (never auto-applied). */
interface AiDraft {
  title: string;
  body: string;
  note: string | null;
  /** Safe, rendered preview of the draft body (renderMarkdown escapes first). */
  preview: SafeHtml;
}

/**
 * Create / edit a family note. A simple markdown textarea with a live preview side-by-side (stacked on
 * mobile), a title field, and a pin toggle. Resolves with {title, body, pinned}; the page calls the create
 * or update endpoint. Read-only notes never open this — the board opens it only when the caller can edit.
 *
 * ✨ AI assist: a small prompt box DRAFTS a fresh note (when the body is empty) or REWRITES/cleans the
 * current body (when editing). The result is shown as a PREVIEW with Use / Try again — it NEVER overwrites
 * the title/body fields until the user picks "Use", and nothing is saved until the user hits Save. Degrades
 * gracefully when AI is unavailable (a friendly aria-live line; the manual editor always works).
 */
@Component({
  selector: 'app-note-editor-dialog',
  imports: [
    FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule,
    MatSlideToggleModule, MatProgressSpinnerModule,
  ],
  templateUrl: './note-editor-dialog.html',
  styleUrl: './family.scss',
})
export class NoteEditorDialog {
  private ref = inject(MatDialogRef<NoteEditorDialog, NoteEditorResult>);
  private sanitizer = inject(DomSanitizer);
  private api = inject(Api);
  readonly data = inject<NoteEditorData>(MAT_DIALOG_DATA);

  readonly isEdit = !!this.data.note;
  readonly title = signal(this.data.note?.title ?? '');
  readonly body = signal(this.data.note?.body ?? '');
  readonly pinned = signal(this.data.note?.pinned ?? false);

  /** Live, sanitized markdown preview of the body. renderMarkdown escapes first, so this is safe HTML. */
  readonly preview = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(this.body())));

  readonly canSave = computed(() => this.title().trim().length > 0 || this.body().trim().length > 0);

  // ---- ✨ Draft / rewrite with AI ----
  /** Whether the AI prompt box is open. */
  readonly aiOpen = signal(false);
  /** The instruction box ("a weekend packing list" / "tighten this up"). */
  readonly aiPrompt = signal('');
  readonly aiBusy = signal(false);
  /** A friendly status line for aria-live (an error, or a hint). */
  readonly aiStatus = signal('');
  /** The pending draft awaiting Use / Try-again, or null. */
  readonly aiDraft = signal<AiDraft | null>(null);

  /** When the body already has content, the assist REWRITES it; otherwise it DRAFTS fresh. Drives labels. */
  readonly aiRewrites = computed(() => this.body().trim().length > 0);

  toggleAi(): void {
    this.aiOpen.update(o => !o);
    if (!this.aiOpen()) this.resetAiDraft();
  }

  private resetAiDraft(): void {
    this.aiDraft.set(null);
    this.aiStatus.set('');
  }

  /**
   * Ask Gemini to draft (empty body) or rewrite (existing body) per the prompt, and stage the result as a
   * preview. Touches NEITHER the title nor body fields — the user must pick "Use". Degrades gracefully: a 503
   * (AI unavailable / not configured) or any error shows a friendly aria-live line.
   */
  async runAi(): Promise<void> {
    const prompt = this.aiPrompt().trim();
    if (!prompt || this.aiBusy()) return;
    this.aiBusy.set(true);
    this.aiStatus.set(this.aiRewrites() ? 'Reworking your note…' : 'Drafting your note…');
    this.aiDraft.set(null);
    try {
      const rewriting = this.aiRewrites();
      const result: NoteDraftAiResult = await firstValueFrom(this.api.draftFamilyNoteAi(
        prompt,
        rewriting ? this.title() : undefined,
        rewriting ? this.body() : undefined,
      ));
      this.aiDraft.set({
        title: result.title,
        body: result.body,
        note: result.note?.trim() ? result.note.trim() : null,
        preview: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(result.body)),
      });
      this.aiStatus.set('Here’s a draft — review it, then Use it or Try again.');
    } catch (e) {
      const status = (e as { status?: number })?.status;
      this.aiStatus.set(status === 503
        ? "AI drafting isn't available right now — you can write the note yourself below."
        : this.messageOf(e, "I couldn't reach the AI just now. Please try again, or write it yourself."));
    } finally {
      this.aiBusy.set(false);
    }
  }

  /** Accept the staged draft into the editable title + body fields (still unsaved until Save). */
  useAiDraft(): void {
    const draft = this.aiDraft();
    if (!draft) return;
    // Only replace the title when the draft offers one (keep the user's title if the model returned blank).
    if (draft.title.trim()) this.title.set(draft.title);
    this.body.set(draft.body);
    this.aiDraft.set(null);
    this.aiStatus.set('Draft applied. Edit anything, then Save.');
  }

  /** Discard the staged draft and try a new instruction. */
  discardAiDraft(): void {
    this.aiDraft.set(null);
    this.aiStatus.set('');
  }

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

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
