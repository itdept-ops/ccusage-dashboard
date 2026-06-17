import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';

import { ChatRealtime } from '../../core/chat-realtime';
import { ChatChannelDto } from '../../core/models';

/** One pickable teammate in the member/DM picker (presence roster, minus the caller). */
export interface ChatPickPerson {
  email: string;
  name: string;
  picture?: string | null;
  online: boolean;
}

/** Input contract: the candidate people to pick from (presence-derived) + which tab to open on. */
export interface ChatCreateData {
  people: ChatPickPerson[];
  /** Which mode the dialog opens in. */
  mode: 'channel' | 'direct';
}

/**
 * Create-conversation dialog: one surface with two modes — a new CHANNEL (name + topic + private
 * flag + a multi-select member picker) or a 1:1 DIRECT message (single user picker). It owns the
 * REST call through {@link ChatRealtime} (which also joins the new channel) and resolves with the
 * created/opened {@link ChatChannelDto} so the page can select it. Mirrors the shared dialog patterns.
 */
@Component({
  selector: 'app-chat-create-dialog',
  imports: [
    FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule,
    MatButtonToggleModule, MatCheckboxModule, MatIconModule,
  ],
  templateUrl: './chat-create-dialog.html',
  styleUrl: './chat-create-dialog.scss',
})
export class ChatCreateDialog {
  private chat = inject(ChatRealtime);
  private ref = inject(MatDialogRef<ChatCreateDialog, ChatChannelDto>);
  readonly data = inject<ChatCreateData>(MAT_DIALOG_DATA);

  readonly mode = signal<'channel' | 'direct'>(this.data.mode);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  // ---- channel fields ----
  readonly name = signal('');
  readonly topic = signal('');
  readonly isPrivate = signal(false);

  // ---- shared member/DM selection (set of lower-cased emails) ----
  private readonly selected = signal<Set<string>>(new Set());
  readonly query = signal('');

  /** People filtered by the search box (case-insensitive over name + email). */
  readonly filteredPeople = computed<ChatPickPerson[]>(() => {
    const q = this.query().trim().toLowerCase();
    const people = this.data.people;
    if (!q) return people;
    return people.filter(p =>
      p.name.toLowerCase().includes(q) || p.email.toLowerCase().includes(q));
  });

  /** How many members are picked (drives the channel create button + chip count). */
  readonly selectedCount = computed(() => this.selected().size);

  isSelected(email: string): boolean {
    return this.selected().has(email.toLowerCase());
  }

  toggle(email: string): void {
    const key = email.toLowerCase();
    if (this.mode() === 'direct') {
      // DM: single selection — picking one replaces any prior pick.
      this.selected.set(this.isSelected(key) ? new Set() : new Set([key]));
      return;
    }
    const next = new Set(this.selected());
    next.has(key) ? next.delete(key) : next.add(key);
    this.selected.set(next);
  }

  setMode(m: 'channel' | 'direct'): void {
    if (m === this.mode()) return;
    this.mode.set(m);
    this.selected.set(new Set()); // selection semantics differ between modes
    this.error.set(null);
  }

  /** Whether the current form can be submitted. */
  readonly canSubmit = computed(() => {
    if (this.busy()) return false;
    if (this.mode() === 'direct') return this.selectedCount() === 1;
    return this.name().trim().length > 0;
  });

  submit(): void {
    if (!this.canSubmit()) return;
    this.busy.set(true);
    this.error.set(null);

    const fail = (e: HttpErrorResponse) => {
      this.busy.set(false);
      this.error.set(e.error?.message ?? 'Could not create the conversation. Please try again.');
    };

    if (this.mode() === 'direct') {
      const email = [...this.selected()][0];
      this.chat.openDirect(email)
        .then(ch => this.ref.close(ch))
        .catch(fail);
      return;
    }

    const members = [...this.selected()];
    this.chat.createChannel(this.name().trim(), members, {
      topic: this.topic().trim() || undefined,
      isPrivate: this.isPrivate(),
    })
      .then(ch => this.ref.close(ch))
      .catch(fail);
  }

  cancel(): void {
    this.ref.close();
  }

  /** Two-letter initials for the avatar fallback. */
  initials(p: ChatPickPerson): string {
    const parts = (p.name || p.email).split(/[\s@.]+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }
}
