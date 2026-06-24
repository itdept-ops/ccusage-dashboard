import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { catchError, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Api } from '../../core/api';
import { ChatContactDto, FamilyShareTarget } from '../../core/models';

/**
 * What the share dialog needs to do its job: the item's friendly label (for the header), its current share
 * targets (so already-shared contacts can be removed inline + filtered out of the picker), and the two
 * callbacks that talk to the right endpoint (notes vs lists). The callbacks return the re-projected
 * shareWith list so the dialog stays in sync without knowing about the item type.
 */
export interface ShareDialogData {
  /** A short label like the note's title or the list's name, shown in the header. */
  itemLabel: string;
  /** Current shares (userId + name + canEdit). */
  shares: FamilyShareTarget[];
  /** Add/update a share; resolves to the item's fresh share list. */
  onShare: (userId: number, canEdit: boolean) => Promise<FamilyShareTarget[]>;
  /** Remove a share; resolves to the item's fresh share list. */
  onUnshare: (userId: number) => Promise<FamilyShareTarget[]>;
}

/**
 * A warm, reusable "Share with family & friends" dialog used by both Notes and Lists. It lists who an item
 * is already shared with (avatar-free, name + a can-edit pill, each with a remove ×) and lets you add a
 * CONTACT from the caller's circle (GET /api/chat/contacts/me) by userId — never an email — with an
 * optional "can edit" toggle. All identity here is userId + display name (email-privacy). The dialog never
 * closes itself on each change; it mutates the live list and the page re-reads the result, so you can add
 * several people in one sitting and just hit Done.
 */
@Component({
  selector: 'app-family-share-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatSelectModule,
    MatSlideToggleModule,
    MatButtonModule,
    MatIconModule,
    MatTooltipModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './share-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './family.scss',
})
export class FamilyShareDialog {
  private api = inject(Api);
  private ref = inject(MatDialogRef<FamilyShareDialog, boolean>);
  readonly data = inject<ShareDialogData>(MAT_DIALOG_DATA);

  /** Live share list (seeded from the item, kept in sync as we add/remove). */
  readonly shares = signal<FamilyShareTarget[]>([...this.data.shares]);
  /** True once we've changed anything — tells the caller to refresh on close. */
  private dirty = false;

  /** The caller's contacts (the share candidate pool). */
  readonly contacts = signal<ChatContactDto[]>([]);
  readonly contactsLoading = signal(true);

  readonly pickedUserId = signal<number | null>(null);
  readonly canEdit = signal(false);
  readonly busy = signal(false);
  /** userId currently being removed (per-row spinner), or null. */
  readonly removingId = signal<number | null>(null);
  readonly errorMsg = signal<string | null>(null);

  /** Contacts not already shared with — the addable pool, name-sorted by the server. */
  readonly addable = computed<ChatContactDto[]>(() => {
    const taken = new Set(this.shares().map((s) => s.userId));
    return this.contacts().filter((c) => !taken.has(c.userId));
  });

  constructor() {
    this.api
      .myContacts()
      .pipe(
        catchError(() => of<ChatContactDto[]>([])),
        takeUntilDestroyed(),
      )
      .subscribe((list) => {
        this.contacts.set(list);
        this.contactsLoading.set(false);
      });
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  /** Add (or update) the picked contact as a share. */
  async add(): Promise<void> {
    const userId = this.pickedUserId();
    if (userId == null || this.busy()) return;
    this.busy.set(true);
    this.errorMsg.set(null);
    try {
      const fresh = await this.data.onShare(userId, this.canEdit());
      this.shares.set(fresh);
      this.dirty = true;
      this.pickedUserId.set(null);
      this.canEdit.set(false);
    } catch (e) {
      this.errorMsg.set(this.messageOf(e, "Couldn't share with that person."));
    } finally {
      this.busy.set(false);
    }
  }

  /** Flip a current share's can-edit flag (re-shares with the new flag). */
  async toggleCanEdit(s: FamilyShareTarget): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    this.errorMsg.set(null);
    try {
      const fresh = await this.data.onShare(s.userId, !s.canEdit);
      this.shares.set(fresh);
      this.dirty = true;
    } catch (e) {
      this.errorMsg.set(this.messageOf(e, "Couldn't update that share."));
    } finally {
      this.busy.set(false);
    }
  }

  /** Remove a share. */
  async remove(s: FamilyShareTarget): Promise<void> {
    if (this.removingId() != null) return;
    this.removingId.set(s.userId);
    this.errorMsg.set(null);
    try {
      const fresh = await this.data.onUnshare(s.userId);
      this.shares.set(fresh);
      this.dirty = true;
    } catch (e) {
      this.errorMsg.set(this.messageOf(e, "Couldn't remove that share."));
    } finally {
      this.removingId.set(null);
    }
  }

  done(): void {
    this.ref.close(this.dirty);
  }

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
