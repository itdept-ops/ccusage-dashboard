import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { catchError, firstValueFrom, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

import { Api } from '../../core/api';
import { FamilyNote, FamilyShareTarget, Household } from '../../core/models';
import { renderMarkdown } from './markdown';
import { NoteEditorDialog, NoteEditorData, NoteEditorResult } from './note-editor-dialog';
import { FamilyShareDialog, ShareDialogData } from './share-dialog';
import { FamilyConfirmDialog, ConfirmData } from './confirm-dialog';

/**
 * Family Notes — a warm board of shared note cards. Pinned notes float to the top, then most-recently
 * updated. Each card shows the title, a markdown-rendered body, the author (name + initials avatar; never
 * an email), and who it's shared with. Members can create / edit (markdown editor + live preview), pin,
 * delete (with a gentle confirm), and share to a contact. A note the caller only has a view-only share to
 * renders read-only — no edit / pin / delete / share controls.
 *
 * All people are rendered by display name + initials avatar only; an email is never shown (email-privacy).
 */
@Component({
  selector: 'app-family-notes',
  imports: [
    RouterLink, MatIconModule, MatButtonModule, MatTooltipModule, MatProgressSpinnerModule,
    MatMenuModule, MatSnackBarModule,
  ],
  templateUrl: './notes.html',
  styleUrl: './family.scss',
})
export class FamilyNotes {
  private api = inject(Api);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private sanitizer = inject(DomSanitizer);

  readonly notes = signal<FamilyNote[]>([]);
  readonly loading = signal(true);
  readonly error = signal(false);
  /** Per-note busy id (pin/delete spinner), or null. */
  readonly busyId = signal<number | null>(null);

  /**
   * The caller's household member userIds — used to tell a household note (manageable: share/delete) from a
   * note merely shared IN from another household (a shared-in author is never one of my members). The
   * server enforces management regardless; this just keeps the menu honest.
   */
  private readonly memberIds = signal<Set<number>>(new Set());

  /** The board, ordered pinned-first then most-recently-updated (the server already sorts, we keep it stable). */
  readonly board = computed(() => this.notes());

  constructor() {
    this.reload(true);
    this.api.getHousehold()
      .pipe(catchError(() => of<Household | null>(null)), takeUntilDestroyed())
      .subscribe(h => { if (h) this.memberIds.set(new Set(h.members.map(m => m.userId))); });
  }

  /**
   * True when the caller may MANAGE this note (share / delete) — i.e. they're a household member of the
   * note's household. A note authored by one of my household members is a household note; a shared-in note
   * (author in another household) is not. Mirrors the server's "creator or household member" rule.
   */
  canManage(note: FamilyNote): boolean {
    return note.isMine || this.memberIds().has(note.createdByUserId);
  }

  private reload(initial = false): void {
    if (initial) this.loading.set(true);
    this.api.familyNotes()
      .pipe(catchError(() => { this.error.set(true); return of<FamilyNote[]>([]); }), takeUntilDestroyed())
      .subscribe(list => { this.notes.set(list); this.loading.set(false); });
  }

  /** Render a note body to safe HTML (renderMarkdown escapes the source first). */
  bodyHtml(body: string): SafeHtml {
    return this.sanitizer.bypassSecurityTrustHtml(renderMarkdown(body));
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  /** Replace one note in the board with its fresh copy (after edit/pin/share). */
  private upsert(note: FamilyNote): void {
    this.notes.update(list => {
      const next = list.some(n => n.id === note.id)
        ? list.map(n => (n.id === note.id ? note : n))
        : [...list, note];
      return this.sort(next);
    });
  }

  /** Pinned-first, then most-recently-updated (mirrors the server's order so it stays stable on local upserts). */
  private sort(list: FamilyNote[]): FamilyNote[] {
    return [...list].sort((a, b) =>
      (Number(b.pinned) - Number(a.pinned)) || (b.updatedUtc.localeCompare(a.updatedUtc)));
  }

  /** Open the editor to create a new note. */
  async create(): Promise<void> {
    const result = await this.openEditor(null);
    if (!result) return;
    try {
      const note = await firstValueFrom(this.api.createFamilyNote(result));
      this.upsert(note);
    } catch {
      this.snack.open("Couldn't save that note. Please try again.", 'OK', { duration: 4000 });
    }
  }

  /** Open the editor to edit an existing note (members / canEdit-shares only). */
  async edit(note: FamilyNote): Promise<void> {
    if (!note.canEdit) return;
    const result = await this.openEditor(note);
    if (!result) return;
    try {
      const updated = await firstValueFrom(this.api.updateFamilyNote(note.id, result));
      this.upsert(updated);
    } catch {
      this.snack.open("Couldn't save that note. Please try again.", 'OK', { duration: 4000 });
    }
  }

  private openEditor(note: FamilyNote | null): Promise<NoteEditorResult | undefined> {
    const ref = this.dialog.open<NoteEditorDialog, NoteEditorData, NoteEditorResult>(NoteEditorDialog, {
      data: { note }, width: '720px', maxWidth: '94vw', autoFocus: false,
    });
    return firstValueFrom(ref.afterClosed());
  }

  /** Toggle a note's pinned state (members / canEdit-shares). */
  async togglePin(note: FamilyNote): Promise<void> {
    if (!note.canEdit || this.busyId() != null) return;
    this.busyId.set(note.id);
    try {
      const updated = await firstValueFrom(this.api.updateFamilyNote(note.id, {
        title: note.title, body: note.body, pinned: !note.pinned,
      }));
      this.upsert(updated);
    } catch {
      this.snack.open("Couldn't update that note.", 'OK', { duration: 4000 });
    } finally {
      this.busyId.set(null);
    }
  }

  /** Delete a note with a warm confirm (creator or any household member). */
  async remove(note: FamilyNote): Promise<void> {
    const ok = await this.confirm({
      title: 'Delete this note?',
      message: note.title ? `“${note.title}” will be removed for everyone it’s shared with.`
        : 'This note will be removed for everyone it’s shared with.',
      destructive: true,
    });
    if (!ok || this.busyId() != null) return;
    this.busyId.set(note.id);
    try {
      await firstValueFrom(this.api.deleteFamilyNote(note.id));
      this.notes.update(list => list.filter(n => n.id !== note.id));
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't delete that note."), 'OK', { duration: 4000 });
    } finally {
      this.busyId.set(null);
    }
  }

  /** Open the share dialog for a note (only the manager — a household member — gets here). */
  async share(note: FamilyNote): Promise<void> {
    const data: ShareDialogData = {
      itemLabel: note.title || 'Untitled note',
      shares: note.sharedWith,
      onShare: async (userId, canEdit) => {
        const updated = await firstValueFrom(this.api.shareFamilyNote(note.id, userId, canEdit));
        this.upsert(updated);
        return updated.sharedWith;
      },
      onUnshare: async (userId) => {
        const updated = await firstValueFrom(this.api.unshareFamilyNote(note.id, userId));
        this.upsert(updated);
        return updated.sharedWith;
      },
    };
    const ref = this.dialog.open<FamilyShareDialog, ShareDialogData, boolean>(FamilyShareDialog, {
      data, width: '460px', maxWidth: '94vw', autoFocus: false,
    });
    await firstValueFrom(ref.afterClosed());
  }

  private confirm(data: ConfirmData): Promise<boolean | undefined> {
    const ref = this.dialog.open<FamilyConfirmDialog, ConfirmData, boolean>(FamilyConfirmDialog, {
      data, width: '420px', maxWidth: '92vw',
    });
    return firstValueFrom(ref.afterClosed());
  }

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }

  /** First three share targets for the avatar stack; the rest collapse into a "+N". */
  visibleShares(shares: FamilyShareTarget[]): FamilyShareTarget[] {
    return shares.slice(0, 3);
  }
  extraShares(shares: FamilyShareTarget[]): number {
    return Math.max(0, shares.length - 3);
  }
}
