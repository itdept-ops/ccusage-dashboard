import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Api } from '../../core/api';
import { BetaEmptyState } from '../beta-ui';
import { FamilyMeal, FamilyMealDay } from '../../core/models';

/** What the dialog opens with: the tracker day the user is viewing (its date seeds "Today"). */
export interface LeftoversData {
  /** The currently-viewed tracker date ("YYYY-MM-DD") — only used to build the day chips. */
  date: string;
}

/**
 * What the dialog resolves with on confirm: the logged meal's title, the scaled servings, and the set of
 * ISO dates ("YYYY-MM-DD") it was logged to (successes only) + how many writes FAILED. The page snackbars the
 * result and reloads any visible affected day. `undefined` === cancelled / nothing logged.
 */
export interface LeftoversResult {
  title: string;
  servings: number;
  loggedDates: string[];
  failed: number;
}

/** A planned meal flattened with its planned day, for the pick list (most-recent first). */
interface LeftoverChoice {
  meal: FamilyMeal;
  /** The meal's planned ISO date ("YYYY-MM-DD"). */
  plannedDate: string;
  /** A friendly "Wed Jun 25" style label for the planned day. */
  plannedLabel: string;
  /** True when the meal has macros set (only these are loggable — the from-meal endpoint 400s without). */
  hasMacros: boolean;
}

/** One selectable "eat it on this day" chip. */
interface DayChip {
  iso: string;
  label: string;
}

/** The default servings to log per day (one portion); mirrors the backend's historical "log ONE serving". */
const DEFAULT_SERVINGS = 1;
/** Clamp bounds for the servings input — must match the backend clamp (0.1..99). */
const MIN_SERVINGS = 0.1;
const MAX_SERVINGS = 99;
/** How many future days (beyond today) to offer as "eat the leftover on" chips. */
const FUTURE_DAYS = 6;

/** Strip a possibly-ISO-datetime down to its "YYYY-MM-DD" date part. */
function dateOnly(s: string): string {
  return (s || '').slice(0, 10);
}

/** Local "YYYY-MM-DD" for a Date (no UTC shift — the planner + tracker are local-date keyed). */
function toIso(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/**
 * "Leftovers" dialog — pull a PLANNED meal off the household meal planner and log it onto the tracker as
 * leftovers to eat over the next day(s). On open it loads the CURRENT + PREVIOUS planner weeks (so a
 * recently-cooked dish is available), de-dupes + sorts most-recent-first, and shows each meal's title + a
 * kcal/protein-per-serving summary + its planned day. The user picks ONE meal, a SERVINGS amount (per day),
 * and one or MORE days to eat it on. On Add the dialog resolves with the picked meal/servings + the selected
 * days; the PAGE performs one `addMealToTracker(meal.id, { localDate, servings })` POST per day (reusing the
 * existing /tracker/food/from-meal endpoint — no new backend) and snackbars the result.
 *
 * Only meals WITH macros are loggable (the from-meal endpoint 400s otherwise) — macro-less meals are listed
 * disabled with a hint. Mirrors the tracker dialog conventions (tracker-dialog panel, the add-food look).
 */
@Component({
  selector: 'app-leftovers-dialog',
  imports: [
    DecimalPipe,
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    BetaEmptyState,
  ],
  templateUrl: './leftovers-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './leftovers-dialog.scss',
})
export class LeftoversDialog {
  private api = inject(Api);
  private ref = inject(MatDialogRef<LeftoversDialog, LeftoversResult>);
  readonly data = inject<LeftoversData>(MAT_DIALOG_DATA);

  readonly minServings = MIN_SERVINGS;
  readonly maxServings = MAX_SERVINGS;

  /** True while the two planner weeks are being fetched. */
  readonly loading = signal(true);
  /** True once both fetches resolved but produced no meals at all (empty planner). */
  readonly loadFailed = signal(false);

  /** Every planned meal across the loaded weeks, most-recent first (macro-less ones included, flagged). */
  readonly choices = signal<LeftoverChoice[]>([]);

  /** The free-text filter over the pick list (by meal title). */
  readonly query = signal('');

  /** The id of the picked meal, or null until one is chosen. */
  readonly pickedId = signal<number | null>(null);

  /** The servings to log PER selected day, bound to the number input (clamped on Add). */
  readonly servings = signal<number>(DEFAULT_SERVINGS);

  /** The day chips offered (Today / Tomorrow / dated), built from the viewed date forward. */
  readonly dayChips: DayChip[] = this.buildDayChips();

  /** The set of selected day ISO dates (default: Tomorrow). */
  readonly selectedDays = signal<Set<string>>(new Set(this.defaultSelectedDays()));

  /** True while the page-driven writes are in flight (latches Add against a double-tap). */
  readonly saving = signal(false);

  constructor() {
    void this.load();
  }

  // ─────────────────────────────────────────── load ────────────────────────────────────────────

  /**
   * Load the CURRENT and PREVIOUS planner weeks, flatten every meal with its planned day, de-dupe by id, and
   * sort most-recent-first so recently-cooked dishes surface at the top. Best-effort: a failed week just
   * contributes nothing; only a total wipe-out flips the empty/failed state.
   */
  private async load(): Promise<void> {
    this.loading.set(true);
    this.loadFailed.set(false);
    try {
      const prevWeekStart = toIso(this.mondayOf(new Date(), -1));
      const [current, previous] = await Promise.all([
        firstValueFrom(this.api.familyMeals()).catch(() => [] as FamilyMealDay[]),
        firstValueFrom(this.api.familyMeals(prevWeekStart)).catch(() => [] as FamilyMealDay[]),
      ]);

      const byId = new Map<number, LeftoverChoice>();
      for (const day of [...current, ...previous]) {
        const iso = dateOnly(day.localDate);
        for (const meal of day.meals ?? []) {
          if (byId.has(meal.id)) continue; // a meal id is unique to one day; first wins.
          byId.set(meal.id, {
            meal,
            plannedDate: iso,
            plannedLabel: this.dayLabel(iso),
            hasMacros: meal.macroSource !== 'none',
          });
        }
      }

      // Most-recent first (by planned date desc), then by title for stable ordering within a day.
      const list = [...byId.values()].sort((a, b) => {
        if (a.plannedDate !== b.plannedDate) return a.plannedDate < b.plannedDate ? 1 : -1;
        return a.meal.title.localeCompare(b.meal.title);
      });
      this.choices.set(list);

      // Pre-pick the most-recent meal that actually has macros (so Add can light up immediately).
      const firstLoggable = list.find((c) => c.hasMacros);
      if (firstLoggable) this.pickedId.set(firstLoggable.meal.id);
    } catch {
      this.choices.set([]);
      this.loadFailed.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  // ─────────────────────────────────────────── pick list ───────────────────────────────────────

  /** The pick list after applying the free-text title filter (case-insensitive substring). */
  readonly filteredChoices = computed<LeftoverChoice[]>(() => {
    const q = this.query().trim().toLowerCase();
    const all = this.choices();
    if (!q) return all;
    return all.filter((c) => c.meal.title.toLowerCase().includes(q));
  });

  /** Are there any loggable (macro-bearing) meals at all? Drives the "nothing to log" hint. */
  readonly hasLoggable = computed(() => this.choices().some((c) => c.hasMacros));

  /** The currently-picked choice (drives Add validity + the macro preview), or null. */
  readonly picked = computed<LeftoverChoice | null>(() => {
    const id = this.pickedId();
    if (id == null) return null;
    return this.choices().find((c) => c.meal.id === id) ?? null;
  });

  /** Pick a meal (no-op for a macro-less one — it can't be logged). */
  pick(choice: LeftoverChoice): void {
    if (!choice.hasMacros) return;
    this.pickedId.set(choice.meal.id);
  }

  /** True when the given choice is the picked one. */
  isPicked(choice: LeftoverChoice): boolean {
    return this.pickedId() === choice.meal.id;
  }

  // ─────────────────────────────────────────── servings + days ─────────────────────────────────

  /** The clamped, finite servings used for the preview + the writes. */
  readonly safeServings = computed(() => {
    const s = this.servings();
    if (!Number.isFinite(s) || s <= 0) return DEFAULT_SERVINGS;
    return Math.min(Math.max(s, MIN_SERVINGS), MAX_SERVINGS);
  });

  /**
   * Live per-day macro preview = the picked meal's per-serving macros × servings, rounded the way the server
   * rounds the logged portion (calories to int, P/C/F to 0.1, floored at 0). Null when nothing is picked.
   */
  readonly preview = computed(() => {
    const c = this.picked();
    if (!c) return null;
    const s = this.safeServings();
    const p = c.meal.perServing;
    return {
      calories: Math.max(0, Math.round(p.calories * s)),
      proteinG: this.round1(p.proteinG * s),
      carbG: this.round1(p.carbG * s),
      fatG: this.round1(p.fatG * s),
    };
  });

  /** Toggle a day chip in/out of the selection (the multi-select). */
  toggleDay(iso: string): void {
    this.selectedDays.update((set) => {
      const next = new Set(set);
      if (next.has(iso)) next.delete(iso);
      else next.add(iso);
      return next;
    });
  }

  /** True when the given day is selected. */
  isDaySelected(iso: string): boolean {
    return this.selectedDays().has(iso);
  }

  /** How many days are selected (drives the "Logging to N day(s)" note + Add validity). */
  readonly selectedCount = computed(() => this.selectedDays().size);

  /** Whether Add is enabled: a loggable meal picked AND at least one day selected AND not saving. */
  readonly canAdd = computed(
    () => !this.saving() && this.picked() != null && this.selectedCount() > 0,
  );

  // ─────────────────────────────────────────── confirm / cancel ────────────────────────────────

  /**
   * Confirm: run one `addMealToTracker(meal.id, { localDate, servings })` POST per selected day, collect
   * successes/failures, and resolve the dialog with the picked meal/servings + the days that landed. The PAGE
   * snackbars + reloads. Latched against a double-tap; a total failure still resolves (with failed > 0) so the
   * page can report it.
   */
  async add(): Promise<void> {
    if (!this.canAdd()) return;
    const choice = this.picked();
    if (!choice) return;
    const servings = this.safeServings();
    const days = [...this.selectedDays()].sort();

    this.saving.set(true);
    const logged: string[] = [];
    let failed = 0;
    for (const iso of days) {
      try {
        await firstValueFrom(
          this.api.addMealToTracker(choice.meal.id, { localDate: iso, servings }),
        );
        logged.push(iso);
      } catch {
        failed++;
      }
    }
    this.saving.set(false);
    this.ref.close({ title: choice.meal.title, servings, loggedDates: logged, failed });
  }

  cancel(): void {
    this.ref.close(undefined);
  }

  // ─────────────────────────────────────────── helpers ─────────────────────────────────────────

  private round1(n: number): number {
    return Math.max(0, Math.round((Number.isFinite(n) ? n : 0) * 10) / 10);
  }

  /** The Monday of the week containing `from`, offset by `weekOffset` weeks (-1 = previous week). */
  private mondayOf(from: Date, weekOffset = 0): Date {
    const d = new Date(from);
    d.setHours(0, 0, 0, 0);
    const dow = (d.getDay() + 6) % 7; // 0 = Monday … 6 = Sunday
    d.setDate(d.getDate() - dow + weekOffset * 7);
    return d;
  }

  /** Build the "eat it on" chips: Today, Tomorrow, then the next several dated days from the viewed date. */
  private buildDayChips(): DayChip[] {
    const base = this.startDate();
    const chips: DayChip[] = [];
    for (let i = 0; i <= 1 + FUTURE_DAYS; i++) {
      const d = new Date(base);
      d.setDate(d.getDate() + i);
      chips.push({ iso: toIso(d), label: this.dayLabel(toIso(d)) });
    }
    return chips;
  }

  /** Tomorrow (relative to the viewed date) is the default leftover day; fall back to the first chip. */
  private defaultSelectedDays(): string[] {
    const base = this.startDate();
    base.setDate(base.getDate() + 1);
    return [toIso(base)];
  }

  /** The chip anchor: the viewed tracker date when valid, else local today. */
  private startDate(): Date {
    const iso = dateOnly(this.data?.date ?? '');
    const d = new Date(`${iso}T00:00:00`);
    if (isNaN(d.getTime())) {
      const today = new Date();
      today.setHours(0, 0, 0, 0);
      return today;
    }
    return d;
  }

  /** A friendly label for an ISO date: Today / Tomorrow / Yesterday, else "Wed Jun 25". */
  private dayLabel(iso: string): string {
    const d = new Date(`${iso}T00:00:00`);
    if (isNaN(d.getTime())) return iso;
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const diff = Math.round((d.getTime() - today.getTime()) / 86_400_000);
    if (diff === 0) return 'Today';
    if (diff === 1) return 'Tomorrow';
    if (diff === -1) return 'Yesterday';
    return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }
}
