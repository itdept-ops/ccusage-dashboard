import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';

import { ChallengeStore } from '../../core/challenge-store';
import { AuthService } from '../../core/auth';
import { HardChallengeDto, HardDayDto, HardSharedPersonDto, UpsertHardDayRequest } from '../../core/models';

/** One row in the six-task checklist, describing how it's scored + a deep-link hint for the auto ones. */
interface TaskRow {
  key: 'diet' | 'water' | 'workout1' | 'workout2' | 'read' | 'photo';
  label: string;
  icon: string;
  /** AUTO tasks read their pass/fail live from the tracker; MANUAL ones are user checkboxes. */
  auto: boolean;
}

const TASK_ROWS: TaskRow[] = [
  { key: 'diet', label: 'Follow your diet', icon: 'restaurant', auto: true },
  { key: 'water', label: 'Drink one gallon of water', icon: 'local_drink', auto: true },
  { key: 'workout1', label: 'First 45-min workout', icon: 'fitness_center', auto: true },
  { key: 'workout2', label: 'Second 45-min workout (outdoors)', icon: 'directions_run', auto: true },
  { key: 'read', label: 'Read 10 pages', icon: 'menu_book', auto: false },
  { key: 'photo', label: 'Take a progress photo', icon: 'photo_camera', auto: false },
];

/** Max future cheat days the backend accepts (kept in sync with HardChallengeEndpoints.MaxCheatDays). */
const MAX_CHEAT_DAYS = 10;

/**
 * 75 Hard challenge page (the Relaxed ruleset) — a six-task daily challenge layered on the food/fitness
 * tracker. Renders the active {@link HardChallengeDto} from {@link ChallengeStore}: a start empty-state,
 * a hero (Day N of 75 + streaks + a finisher state), the six daily tasks (the five AUTO ones read their
 * LIVE pass/fail from the tracker with a deep link to /tracker to log; the manual ones are checkboxes),
 * the diet override + workout-2 outdoor toggles, a no-alcohol toggle + a short confession, a pre-declare
 * cheat-day picker, and a "shared with me" list + a read-only viewer (?user={id}, NEVER an email).
 *
 * All edit controls are hidden when viewing someone else (store.readOnly). The auto task bits are never
 * computed here — they come from the server, recomputed live from the tracker on each read.
 */
@Component({
  selector: 'app-challenge',
  standalone: true,
  imports: [
    FormsModule, RouterLink, MatIconModule, MatButtonModule, MatMenuModule, MatTooltipModule,
    MatProgressBarModule, MatProgressSpinnerModule, MatCheckboxModule, MatSlideToggleModule,
    MatFormFieldModule, MatInputModule, MatButtonToggleModule, MatSnackBarModule,
  ],
  templateUrl: './challenge.html',
  styleUrl: './challenge.scss',
})
export class Challenge {
  readonly store = inject(ChallengeStore);
  readonly auth = inject(AuthService);
  private snack = inject(MatSnackBar);
  private destroyRef = inject(DestroyRef);

  readonly taskRows = TASK_ROWS;
  readonly totalDays = 75;

  /** True while a start-challenge POST is in flight (guards a double submit). */
  readonly starting = signal(false);

  /** The chosen start date for a new challenge (ISO "YYYY-MM-DD"); defaults to today. */
  readonly startDate = signal<string>(this.todayIso());

  /** The confession draft for the selected day (bound to the textarea); resynced when the day changes. */
  readonly confessionDraft = signal<string>('');

  /** A future date to add as a cheat day (ISO "YYYY-MM-DD"); '' until picked. */
  readonly cheatPick = signal<string>('');

  /** True while a read-only auto-refresh is in flight (subtle spinner). */
  readonly refreshing = signal(false);

  /** The active read-only auto-refresh interval handle, or null when not running. */
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  /** Screen-reader-only polite status line. */
  readonly statusMsg = signal('');

  /** The selected day's grid row (auto bits live from the server), or null. */
  readonly day = computed<HardDayDto | null>(() => this.store.selectedDay());

  /** Whether the loaded challenge is finished (day 75 complete). Drives the finisher state. */
  readonly finished = computed(() => this.store.challenge()?.status === 'Completed');

  /** Completion progress (0..100) toward 75 completed days, for the hero meter. */
  readonly completionPct = computed(() => {
    const c = this.store.challenge();
    if (!c) return 0;
    return Math.min(100, Math.round((c.completedDays / this.totalDays) * 100));
  });

  /** The future cheat days already declared (within the loaded window), oldest-first, for the chip list. */
  readonly cheatDays = computed<HardDayDto[]>(() => {
    const today = this.todayIso();
    return this.store.days().filter(d => d.isCheatDay && d.date > today);
  });

  constructor() {
    void this.store.load();
    void this.store.loadShared();

    // Keep the confession textarea in sync with whichever day is selected (own view only — a viewer never
    // sees confessions, so the draft stays empty there).
    effect(() => {
      const d = this.day();
      this.confessionDraft.set(this.store.readOnly() ? '' : (d?.confession ?? ''));
    });

    // Announce day/challenge reloads to assistive tech.
    effect(() => {
      const c = this.store.challenge();
      if (!c) return;
      this.statusMsg.set(`Day ${c.currentDay} of ${this.totalDays}, current streak ${c.currentStreak}`);
    });

    // Read-only auto-refresh: a gentle 30s re-fetch ONLY while viewing someone else's challenge. Keyed on
    // the view target so switching users restarts the timer; switching back (null) stops it.
    effect(() => {
      const target = this.store.viewUser();
      this.stopAutoRefresh();
      if (target !== null) {
        this.refreshTimer = setInterval(() => void this.refreshReadOnly(), 30_000);
      }
    });

    this.destroyRef.onDestroy(() => this.stopAutoRefresh());
  }

  private stopAutoRefresh(): void {
    if (this.refreshTimer !== null) {
      clearInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  async refreshReadOnly(): Promise<void> {
    if (this.store.viewUser() === null) return;
    this.refreshing.set(true);
    try {
      await this.store.load();
    } finally {
      this.refreshing.set(false);
    }
  }

  // ---- start ----

  /** Start a 75 Hard run (own; one active at a time). A 409 (already active) reloads to show it. */
  async start(): Promise<void> {
    if (this.starting()) return;
    this.starting.set(true);
    try {
      await this.store.start({ startDate: this.startDate() || undefined });
      this.snack.open('Your 75 Hard has begun — day 1!', 'OK', { duration: 2600 });
      this.store.goToday();
    } catch (e) {
      // One-active invariant: surface the conflict, then reload so the existing run renders.
      const msg = this.messageOf(e, 'Could not start your challenge.');
      this.snack.open(msg, 'OK', { duration: 4000 });
      await this.store.load();
    } finally {
      this.starting.set(false);
    }
  }

  // ---- day navigation (in-grid; no reload — the whole grid is loaded) ----

  prevDay(): void { this.store.shiftDate(-1); }
  nextDay(): void { this.store.shiftDate(1); }
  goToday(): void { this.store.goToday(); }
  onDateInput(value: string): void { if (value) this.store.setDate(value); }

  /** A friendly heading for the selected date (Today / Yesterday / weekday). */
  readonly dateHeading = computed(() => {
    const d = new Date(this.store.date() + 'T00:00:00');
    const today = new Date(); today.setHours(0, 0, 0, 0);
    const diff = Math.round((d.getTime() - today.getTime()) / 86_400_000);
    if (diff === 0) return 'Today';
    if (diff === -1) return 'Yesterday';
    if (diff === 1) return 'Tomorrow';
    return d.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' });
  });

  // ---- six-task helpers ----

  /** Whether an AUTO task passes for the selected day (read from the server-computed grid row). */
  autoPass(key: TaskRow['key']): boolean {
    const d = this.day();
    if (!d) return false;
    switch (key) {
      case 'diet': return d.dietOk;
      case 'water': return d.waterGallonOk;
      case 'workout1': return d.workout1Ok;
      case 'workout2': return d.workout2Ok;
      default: return false;
    }
  }

  /** Whether a MANUAL task is checked for the selected day. */
  manualPass(key: 'read' | 'photo'): boolean {
    const d = this.day();
    return key === 'read' ? !!d?.readOk : !!d?.photoTaken;
  }

  /** A short hint under each AUTO task explaining how it's scored. */
  autoHint(key: TaskRow['key']): string {
    switch (key) {
      case 'diet': return 'within your goals';
      case 'water': return '1 of 1 gal';
      case 'workout1': return '≥ 45 min';
      case 'workout2': return '≥ 45 min, second of the day';
      default: return '';
    }
  }

  /** Whether the diet override is forcing a result (true/false), or null when using the auto value. */
  dietOverride(): boolean | null {
    return this.day()?.dietOverride ?? null;
  }

  // ---- manual edits (owner only) ----

  /** Toggle a manual checkbox (read / photo-boolean) for the selected day. */
  toggleManual(key: 'readOk' | 'photoTaken', checked: boolean): void {
    void this.saveDay({ [key]: checked });
  }

  /** Toggle the workout-2 outdoor attestation. */
  toggleOutdoor(checked: boolean): void {
    void this.saveDay({ workout2Outdoor: checked });
  }

  /** Toggle the no-alcohol rule for the day. */
  toggleNoAlcohol(checked: boolean): void {
    void this.saveDay({ noAlcohol: checked });
  }

  /**
   * Set the diet override (On plan / Off plan). The backend persists true/false and WINS over the live
   * auto computation. Note: the PUT treats a null payload field as "leave as-is", so an override can't be
   * cleared back to "use auto" from here — it's a deliberate, sticky manual decision (see the endpoint).
   */
  setDietOverride(mode: 'pass' | 'fail'): void {
    if (this.store.readOnly()) return;
    void this.saveDay({ dietOverride: mode === 'pass' });
  }

  /** Save the confession draft for the selected day (owner). Trimmed; empty leaves it as-is server-side. */
  saveConfession(): void {
    if (this.store.readOnly()) return;
    void this.saveDay({ confession: this.confessionDraft().trim() });
  }

  /** Upsert the manual portion of the selected day, then a gentle confirmation. */
  private async saveDay(patch: Partial<UpsertHardDayRequest>): Promise<void> {
    if (this.store.readOnly()) return;
    try {
      await this.store.upsertDay({ date: this.store.date(), ...patch });
    } catch (e) {
      this.snack.open(this.messageOf(e, 'Could not save — please try again.'), 'OK', { duration: 4000 });
    }
  }

  // ---- cheat days (future-only, owner) ----

  /** Add the picked future date as a cheat day. */
  async addCheatDay(): Promise<void> {
    const date = this.cheatPick();
    if (this.store.readOnly() || !date) return;
    if (date <= this.todayIso()) {
      this.snack.open('Cheat days must be in the future.', 'OK', { duration: 3500 });
      return;
    }
    if (this.cheatDays().length >= MAX_CHEAT_DAYS) {
      this.snack.open(`You can declare at most ${MAX_CHEAT_DAYS} cheat days.`, 'OK', { duration: 3500 });
      return;
    }
    try {
      await this.store.setCheatDays({ add: [date] });
      this.cheatPick.set('');
      this.snack.open('Cheat day added.', 'OK', { duration: 2000 });
    } catch (e) {
      this.snack.open(this.messageOf(e, 'Could not add that cheat day.'), 'OK', { duration: 4000 });
    }
  }

  /** Clear a previously-declared future cheat day. */
  async removeCheatDay(d: HardDayDto): Promise<void> {
    if (this.store.readOnly()) return;
    try {
      await this.store.setCheatDays({ remove: [d.date] });
      this.snack.open('Cheat day cleared.', 'OK', { duration: 2000 });
    } catch (e) {
      this.snack.open(this.messageOf(e, 'Could not clear that cheat day.'), 'OK', { duration: 4000 });
    }
  }

  /** The earliest a cheat day may be (tomorrow), for the date input's min. */
  readonly minCheatDate = computed(() => {
    const d = new Date(); d.setDate(d.getDate() + 1);
    return this.toLocalDate(d);
  });

  /** The challenge window end (start + 74 days), for the cheat-day input's max. */
  readonly maxChallengeDate = computed(() => {
    const c = this.store.challenge();
    if (!c) return null;
    const d = new Date(c.startDate + 'T00:00:00');
    d.setDate(d.getDate() + this.totalDays - 1);
    return this.toLocalDate(d);
  });

  // ---- shared view ----

  get viewingUser(): HardSharedPersonDto | null {
    const userId = this.store.viewUser();
    if (userId == null) return null;
    return this.store.shared().find(s => s.userId === userId)
      ?? { userId, name: this.store.challenge()?.userName ?? 'Unknown user' };
  }

  viewSelf(): void { void this.store.viewUserTracker(null); }
  viewOther(userId: number): void { void this.store.viewUserTracker(userId); }

  /** Two-letter initials for the shared-user avatar fallback (name only; no email — email-privacy). */
  initials(u: { name?: string }): string {
    const parts = (u.name || '').split(/[\s@.]+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }

  /** "Mon, Jun 22" friendly label from a plain ISO date. */
  friendlyDate(iso: string): string {
    const d = new Date(iso + 'T00:00:00');
    return isNaN(d.getTime())
      ? iso : d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  // ---- misc ----

  private toLocalDate(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  private todayIso(): string {
    return this.toLocalDate(new Date());
  }

  private messageOf(e: unknown, fallback: string): string {
    const err = e as { error?: { message?: string; detail?: string; title?: string } };
    const msg = err?.error?.message || err?.error?.detail || err?.error?.title;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
