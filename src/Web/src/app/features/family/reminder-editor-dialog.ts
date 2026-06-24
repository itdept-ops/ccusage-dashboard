import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { FamilyRecurrence, FamilyReminder, HouseholdMember } from '../../core/models';

/** Data passed into the reminder editor: the reminder being edited (null = create) + the picker members. */
export interface ReminderEditorData {
  reminder: FamilyReminder | null;
  members: HouseholdMember[];
  /** The caller's own userId — the default target for a new reminder. */
  selfUserId: number;
}

/** The result the editor returns: a `dueUtc` already converted from the LOCAL picker to a UTC ISO string. */
export interface ReminderEditorResult {
  text: string;
  dueUtc: string;
  recurrence: FamilyRecurrence;
  targetUserId: number;
}

/** A recurrence choice for the select. */
const RECURRENCES: { value: FamilyRecurrence; label: string }[] = [
  { value: 'none', label: 'Does not repeat' },
  { value: 'daily', label: 'Every day' },
  { value: 'weekdays', label: 'Weekdays (Mon–Fri)' },
  { value: 'weekly', label: 'Every week' },
];

/**
 * Create / edit a family reminder. The date+time is entered in the user's LOCAL time via a native
 * `datetime-local` input, then converted to a UTC ISO instant on save (and back to local when seeding the
 * edit form). The target is a household member chosen by userId (default: the caller) and rendered by name
 * only — never an email (email-privacy).
 */
@Component({
  selector: 'app-reminder-editor-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatButtonModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.Eager,
  templateUrl: './reminder-editor-dialog.html',
})
export class ReminderEditorDialog {
  readonly ref = inject(MatDialogRef<ReminderEditorDialog, ReminderEditorResult>);
  readonly data = inject<ReminderEditorData>(MAT_DIALOG_DATA);

  readonly recurrences = RECURRENCES;
  readonly isEdit = !!this.data.reminder;

  readonly text = signal(this.data.reminder?.text ?? '');
  /** Bound to <input type="datetime-local">; value is "YYYY-MM-DDTHH:mm" in LOCAL time. */
  readonly localWhen = signal(this.initialLocalWhen());
  readonly recurrence = signal<FamilyRecurrence>(this.data.reminder?.recurrence ?? 'none');
  readonly targetUserId = signal<number>(this.data.reminder?.targetUserId ?? this.data.selfUserId);

  readonly canSave = computed(() => this.text().trim().length > 0 && this.localWhen().length > 0);

  /** Seed the picker: edit → the reminder's UTC due in local time; create → next round half-hour, local. */
  private initialLocalWhen(): string {
    const base = this.data.reminder ? new Date(this.data.reminder.dueUtc) : this.nextHalfHour();
    return this.toLocalInput(base);
  }

  private nextHalfHour(): Date {
    const d = new Date();
    d.setSeconds(0, 0);
    d.setMinutes(d.getMinutes() < 30 ? 30 : 60);
    return d;
  }

  /** A Date → "YYYY-MM-DDTHH:mm" in the browser's local zone (what datetime-local expects). */
  private toLocalInput(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  save(): void {
    if (!this.canSave()) return;
    // The datetime-local string is local wall-clock; `new Date(local)` parses it in local time, and
    // toISOString() emits the corresponding UTC instant — exactly the local→UTC conversion the API wants.
    const dueUtc = new Date(this.localWhen()).toISOString();
    this.ref.close({
      text: this.text().trim(),
      dueUtc,
      recurrence: this.recurrence(),
      targetUserId: this.targetUserId(),
    });
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }
}
