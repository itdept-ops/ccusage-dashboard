import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { catchError, firstValueFrom, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatMenuModule } from '@angular/material/menu';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { FamilyRecurrence, FamilyReminder, Household, HouseholdMember } from '../../core/models';
import { FamilyConfirmDialog, ConfirmData } from './confirm-dialog';
import {
  ReminderEditorDialog, ReminderEditorData, ReminderEditorResult,
} from './reminder-editor-dialog';

/** Friendly labels for the recurrence chip. */
const RECURRENCE_LABEL: Record<FamilyRecurrence, string> = {
  none: 'One-time',
  daily: 'Daily',
  weekdays: 'Weekdays',
  weekly: 'Weekly',
};

/** Snooze options offered on the menu (minutes). */
const SNOOZE_OPTIONS = [
  { label: '10 minutes', minutes: 10 },
  { label: '1 hour', minutes: 60 },
  { label: 'Tomorrow (24h)', minutes: 24 * 60 },
];

/**
 * Family Reminders — the household's upcoming nudges, next-due first. Each row shows the text, when (in the
 * viewer's LOCAL time), a recurrence chip, and who it pings (target member avatar + name; never an email).
 * Members can create / edit (text, a local date+time converted to UTC, a recurrence, and a household-member
 * target), snooze, and delete. When a reminder fires it arrives through the existing notification bell/toast
 * — this page just reflects recurrence advancing (a light refresh on focus picks up the new due time).
 *
 * Everyone is rendered by display name + initials avatar only; an email is never shown (email-privacy).
 */
@Component({
  selector: 'app-family-reminders',
  imports: [
    RouterLink, DatePipe, MatIconModule, MatButtonModule, MatTooltipModule, MatProgressSpinnerModule,
    MatMenuModule, MatSnackBarModule,
  ],
  templateUrl: './reminders.html',
  styleUrls: ['./family.scss', './reminders.scss'],
})
export class FamilyReminders {
  private api = inject(Api);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);

  readonly snoozeOptions = SNOOZE_OPTIONS;

  readonly reminders = signal<FamilyReminder[]>([]);
  readonly members = signal<HouseholdMember[]>([]);
  readonly loading = signal(true);
  readonly error = signal(false);
  readonly busyId = signal<number | null>(null);

  /** The caller's own userId (default target for a new reminder). */
  private readonly selfUserId = computed(() => this.members().find(m => m.isSelf)?.userId ?? 0);

  /** Active reminders, next-due first. */
  readonly upcoming = computed(() =>
    this.reminders().filter(r => r.active).sort((a, b) => a.dueUtc.localeCompare(b.dueUtc)));

  /** Fired one-time reminders that haven't been deleted yet (kept visible so they can be re-scheduled). */
  readonly past = computed(() =>
    this.reminders().filter(r => !r.active).sort((a, b) => b.dueUtc.localeCompare(a.dueUtc)));

  constructor() {
    this.reload(true);
    this.api.getHousehold()
      .pipe(catchError(() => of<Household | null>(null)), takeUntilDestroyed())
      .subscribe(h => { if (h) this.members.set(h.members); });
  }

  private reload(initial = false): void {
    if (initial) this.loading.set(true);
    this.api.familyReminders()
      .pipe(catchError(() => { if (initial) this.error.set(true); return of<FamilyReminder[]>([]); }),
        takeUntilDestroyed())
      .subscribe(list => { this.reminders.set(list); this.loading.set(false); });
  }

  recurrenceLabel(r: FamilyRecurrence): string {
    return RECURRENCE_LABEL[r] ?? 'One-time';
  }

  /** True when the reminder's next fire is in the past (a recurring one mid-advance, or a not-yet-fired late one). */
  isOverdue(r: FamilyReminder): boolean {
    return r.active && Date.parse(r.dueUtc) < Date.now();
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  private upsert(reminder: FamilyReminder): void {
    this.reminders.update(list =>
      list.some(r => r.id === reminder.id)
        ? list.map(r => (r.id === reminder.id ? reminder : r))
        : [...list, reminder]);
  }

  /** Open the editor to create a new reminder. */
  async create(): Promise<void> {
    const result = await this.openEditor(null);
    if (!result) return;
    try {
      const created = await firstValueFrom(this.api.createFamilyReminder(result));
      this.upsert(created);
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't save that reminder. Please try again."), 'OK', { duration: 4000 });
    }
  }

  /** Open the editor to edit an existing reminder. */
  async edit(reminder: FamilyReminder): Promise<void> {
    const result = await this.openEditor(reminder);
    if (!result) return;
    try {
      const updated = await firstValueFrom(this.api.updateFamilyReminder(reminder.id, result));
      this.upsert(updated);
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't save that reminder. Please try again."), 'OK', { duration: 4000 });
    }
  }

  private openEditor(reminder: FamilyReminder | null): Promise<ReminderEditorResult | undefined> {
    const ref = this.dialog.open<ReminderEditorDialog, ReminderEditorData, ReminderEditorResult>(
      ReminderEditorDialog, {
        data: { reminder, members: this.members(), selfUserId: this.selfUserId() },
        width: '460px', maxWidth: '94vw', autoFocus: false,
      });
    return firstValueFrom(ref.afterClosed());
  }

  /** Snooze a reminder out by N minutes from now. */
  async snooze(reminder: FamilyReminder, minutes: number): Promise<void> {
    if (this.busyId() != null) return;
    this.busyId.set(reminder.id);
    try {
      const updated = await firstValueFrom(this.api.snoozeFamilyReminder(reminder.id, minutes));
      this.upsert(updated);
      this.snack.open('Snoozed.', undefined, { duration: 1800 });
    } catch {
      this.snack.open("Couldn't snooze that reminder.", 'OK', { duration: 4000 });
    } finally {
      this.busyId.set(null);
    }
  }

  /** Delete a reminder with a warm confirm. */
  async remove(reminder: FamilyReminder): Promise<void> {
    const ok = await this.confirm({
      title: 'Delete this reminder?',
      message: `“${reminder.text}” will stop nudging ${reminder.targetName}.`,
      destructive: true,
    });
    if (!ok || this.busyId() != null) return;
    this.busyId.set(reminder.id);
    try {
      await firstValueFrom(this.api.deleteFamilyReminder(reminder.id));
      this.reminders.update(list => list.filter(r => r.id !== reminder.id));
    } catch {
      this.snack.open("Couldn't delete that reminder.", 'OK', { duration: 4000 });
    } finally {
      this.busyId.set(null);
    }
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
}
