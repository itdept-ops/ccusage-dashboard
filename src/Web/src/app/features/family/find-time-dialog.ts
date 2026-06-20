import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Api } from '../../core/api';
import { FindTimeConsideredMember, FindTimeResult, FindTimeSlot, HouseholdMember } from '../../core/models';

/** Data into the find-a-time dialog: the household members to offer as chips (avatar + name, never email). */
export interface FindTimeData {
  members: HouseholdMember[];
}

/**
 * The dialog result: a chosen slot to seed a NEW event with. `startUtc`/`endUtc` are ISO UTC instants ready
 * for the event editor (which will display them in local time).
 */
export interface FindTimeResultSlot {
  startUtc: string;
  endUtc: string;
}

/** A pickable meeting length. */
const DURATIONS: { value: number; label: string }[] = [
  { value: 30, label: '30 minutes' },
  { value: 45, label: '45 minutes' },
  { value: 60, label: '1 hour' },
  { value: 90, label: '90 minutes' },
  { value: 120, label: '2 hours' },
  { value: 180, label: '3 hours' },
];

/** The workday-hour choices (local to the household timezone). */
const HOURS: { value: number; label: string }[] = Array.from({ length: 24 }, (_, h) => {
  const ampm = h < 12 ? 'AM' : 'PM';
  const twelve = h % 12 === 0 ? 12 : h % 12;
  return { value: h, label: `${twelve}:00 ${ampm}` };
});

/**
 * Family Hub F6b — FIND A TIME. Pick the household members to coordinate (chips, by userId + name — never an
 * email), a duration, a date window, and optional workday hours, then call /find-time to surface candidate
 * slots that work for every CONNECTED member. Each slot is shown in local time; members who haven't connected
 * a calendar are listed as a gentle note (their availability is unknown, so they don't constrain the search).
 * Clicking a slot closes the dialog with that slot so the caller can create the event prefilled. When nobody
 * is connected we degrade to a warm "no one's connected yet" message rather than an empty void.
 */
@Component({
  selector: 'app-find-time-dialog',
  imports: [
    FormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatSelectModule, MatProgressSpinnerModule,
  ],
  templateUrl: './find-time-dialog.html',
  styleUrls: ['./family.scss', './calendar.scss'],
})
export class FindTimeDialog {
  private api = inject(Api);
  readonly ref = inject(MatDialogRef<FindTimeDialog, FindTimeResultSlot>);
  readonly data = inject<FindTimeData>(MAT_DIALOG_DATA);

  readonly durations = DURATIONS;
  readonly hours = HOURS;
  readonly members = this.data.members;

  /** Selected member userIds — defaults to everyone. */
  readonly selected = signal<Set<number>>(new Set(this.members.map(m => m.userId)));
  readonly durationMinutes = signal(60);
  /** "YYYY-MM-DD" local window bounds (default: today → +7 days). */
  readonly fromDate = signal(this.localDate(new Date()));
  readonly toDate = signal(this.localDate(new Date(Date.now() + 7 * 24 * 60 * 60 * 1000)));
  readonly dayStartHour = signal(9);
  readonly dayEndHour = signal(17);

  readonly searching = signal(false);
  readonly error = signal('');
  /** null until a search has run. */
  readonly result = signal<FindTimeResult | null>(null);

  readonly slots = computed<FindTimeSlot[]>(() => this.result()?.slots ?? []);
  /** Members who were considered but aren't connected — surfaced as a gentle note. */
  readonly notConnected = computed<FindTimeConsideredMember[]>(
    () => (this.result()?.consideredMembers ?? []).filter(m => !m.connected));
  /** True after a search returns and NOBODY considered was connected. */
  readonly noneConnected = computed<boolean>(() => {
    const considered = this.result()?.consideredMembers ?? [];
    return considered.length > 0 && considered.every(m => !m.connected);
  });

  readonly canSearch = computed(() =>
    this.selected().size > 0
    && this.fromDate().length > 0
    && this.toDate().length > 0
    && this.dayStartHour() < this.dayEndHour()
    && !this.searching());

  isSelected(userId: number): boolean {
    return this.selected().has(userId);
  }

  toggle(userId: number): void {
    const next = new Set(this.selected());
    if (next.has(userId)) next.delete(userId); else next.add(userId);
    this.selected.set(next);
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  async search(): Promise<void> {
    if (!this.canSearch()) return;
    this.searching.set(true);
    this.error.set('');
    try {
      // The whole local day across the window: from the start date's midnight to the end date's end-of-day.
      const fromUtc = new Date(`${this.fromDate()}T00:00:00`).toISOString();
      const toUtc = new Date(`${this.toDate()}T23:59:59`).toISOString();
      const res = await firstValueFrom(this.api.findTime({
        memberUserIds: [...this.selected()],
        durationMinutes: this.durationMinutes(),
        fromUtc,
        toUtc,
        dayStartHourLocal: this.dayStartHour(),
        dayEndHourLocal: this.dayEndHour(),
      }));
      this.result.set(res);
    } catch (e) {
      this.error.set(this.messageOf(e, "We couldn't look for a time just now. Please try again."));
      this.result.set(null);
    } finally {
      this.searching.set(false);
    }
  }

  /** Pick a slot → close with it so the calendar can open the event editor prefilled. */
  pick(slot: FindTimeSlot): void {
    this.ref.close({ startUtc: slot.startUtc, endUtc: slot.endUtc });
  }

  // ---- Slot display (browser local zone) ----

  /** "Thu, Jun 20" day label for a slot. */
  slotDay(slot: FindTimeSlot): string {
    return new Date(slot.startUtc).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  /** "9:00 AM – 10:00 AM" local time range for a slot. */
  slotRange(slot: FindTimeSlot): string {
    const opts: Intl.DateTimeFormatOptions = { hour: 'numeric', minute: '2-digit' };
    const start = new Date(slot.startUtc).toLocaleTimeString(undefined, opts);
    const end = new Date(slot.endUtc).toLocaleTimeString(undefined, opts);
    return `${start} – ${end}`;
  }

  private localDate(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
