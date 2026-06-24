import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
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

import { MatMenuModule } from '@angular/material/menu';

import { Api } from '../../core/api';
import {
  FamilyNote,
  NoteDraftAiResult,
  NoteTransformAction,
  NoteTransformAiResult,
} from '../../core/models';
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

/** A "✨ Transform" preview awaiting Use / Try-again (a transformed body only — never the title). */
interface TransformPreview {
  action: NoteTransformAction;
  /** A friendly label for the action that produced this (e.g. "Made a checklist"). */
  label: string;
  body: string;
  /** Safe, rendered preview of the transformed body (renderMarkdown escapes first). */
  preview: SafeHtml;
}

/** The quick-action transforms in the editor row (mirrors the server vocabulary; translate uses a language). */
const TRANSFORM_ACTIONS: { action: NoteTransformAction; label: string; icon: string }[] = [
  { action: 'continue', label: 'Continue writing', icon: 'edit_note' },
  { action: 'checklist', label: 'Make a checklist', icon: 'checklist' },
  { action: 'shorten', label: 'Shorten', icon: 'compress' },
];

/** A small set of target languages for "Translate" (the server accepts any free-text language name). */
const TRANSLATE_LANGS = [
  'Spanish',
  'French',
  'German',
  'Italian',
  'Portuguese',
  'Chinese',
  'Japanese',
  'Korean',
  'Hindi',
  'Arabic',
  'Russian',
  'Vietnamese',
  'Tagalog',
  'English',
];

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
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    MatMenuModule,
  ],
  templateUrl: './note-editor-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
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
    this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(this.body())),
  );

  readonly canSave = computed(
    () => this.title().trim().length > 0 || this.body().trim().length > 0,
  );

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
    this.aiOpen.update((o) => !o);
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
      const result: NoteDraftAiResult = await firstValueFrom(
        this.api.draftFamilyNoteAi(
          prompt,
          rewriting ? this.title() : undefined,
          rewriting ? this.body() : undefined,
        ),
      );
      this.aiDraft.set({
        title: result.title,
        body: result.body,
        note: result.note?.trim() ? result.note.trim() : null,
        preview: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(result.body)),
      });
      this.aiStatus.set('Here’s a draft — review it, then Use it or Try again.');
    } catch (e) {
      const status = (e as { status?: number })?.status;
      this.aiStatus.set(
        status === 503
          ? "AI drafting isn't available right now — you can write the note yourself below."
          : this.messageOf(
              e,
              "I couldn't reach the AI just now. Please try again, or write it yourself.",
            ),
      );
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

  // ---- ✨ Transform (Continue / Make checklist / Shorten / Translate) ----

  readonly transformActions = TRANSFORM_ACTIONS;
  readonly translateLangs = TRANSLATE_LANGS;
  /** Which transform is in flight (spinner on its row), or null. */
  readonly transformBusy = signal<NoteTransformAction | null>(null);
  /** A friendly aria-live status line for the transform row (an error, or a hint). */
  readonly transformStatus = signal('');
  /** The pending transform preview awaiting Use / Try-again, or null. */
  readonly transformPreview = signal<TransformPreview | null>(null);

  /** True when there's a body to transform (the row's actions are disabled on an empty note). */
  readonly canTransform = computed(() => this.body().trim().length > 0);

  /**
   * Run one transform on the current body and stage the result as a PREVIEW (Continue / Make checklist /
   * Shorten / Translate). Touches the body field only on "Use this". Degrades gracefully: a 503 (AI
   * unavailable / not configured), a 400 (empty body), or any error shows a friendly aria-live line.
   */
  async runTransform(action: NoteTransformAction, lang?: string): Promise<void> {
    if (!this.canTransform() || this.transformBusy()) return;
    this.transformBusy.set(action);
    this.transformPreview.set(null);
    const label =
      action === 'translate'
        ? `Translated to ${lang}`
        : (TRANSFORM_ACTIONS.find((a) => a.action === action)?.label ?? 'Transformed');
    this.transformStatus.set(action === 'translate' ? `Translating to ${lang}…` : `${label}…`);
    try {
      const result: NoteTransformAiResult = await firstValueFrom(
        this.api.transformFamilyNoteAi(this.body(), action, lang),
      );
      this.transformPreview.set({
        action,
        label,
        body: result.body,
        preview: this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(result.body)),
      });
      this.transformStatus.set('Here’s the result — review it, then Use it or Try again.');
    } catch (e) {
      const status = (e as { status?: number })?.status;
      this.transformStatus.set(
        status === 503
          ? "AI isn't available right now — you can edit the note yourself below."
          : this.messageOf(
              e,
              "I couldn't reach the AI just now. Please try again, or edit it yourself.",
            ),
      );
    } finally {
      this.transformBusy.set(null);
    }
  }

  /** Accept the staged transform into the body field (still unsaved until Save). */
  useTransform(): void {
    const t = this.transformPreview();
    if (!t) return;
    this.body.set(t.body);
    this.transformPreview.set(null);
    this.transformStatus.set('Applied. Edit anything, then Save.');
  }

  /** Discard the staged transform (keeps the row open to try another action). */
  discardTransform(): void {
    this.transformPreview.set(null);
    this.transformStatus.set('');
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
