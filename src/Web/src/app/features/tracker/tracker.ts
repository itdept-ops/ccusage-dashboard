import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { TrackerStore } from '../../core/tracker-store';
import {
  ActivityCalorieMode, AddExerciseRequest, AddFoodRequest, AddHydrationRequest, DailyCoachResponse, ExerciseEntryDto, FoodEntryDto,
  FoodSuggestionDto, HydrationEntryDto, LogWeightRequest, Meal, PERM, SharedUserDto, TrackerProfileDto, UpsertActivityRequest,
  WeeklyReviewResponse, WeightPointDto, WeightStatsDto,
} from '../../core/models';
import { CalorieRing } from './calorie-ring';
import { HydrationRing } from './hydration-ring';
import { ActivityRing } from './activity-ring';
import { AddFoodDialog, AddFoodData } from './add-food-dialog';
import { AddExerciseDialog, AddExerciseData } from './add-exercise-dialog';
import { AddHydrationDialog, AddHydrationData, AddHydrationResult } from './add-hydration-dialog';
import { AddActivityDialog, AddActivityData } from './add-activity-dialog';
import { ProfileDialog, ProfileData } from './profile-dialog';
import { LogWeightDialog, LogWeightData } from './log-weight-dialog';
import { OnboardingCard, OnboardingResult } from './onboarding-card';
import { WeightTrend } from './weight-trend';
import { WeightStats } from './weight-stats';
import { formatDistance, formatVolume, formatWeight, kgToLb } from './units';

/** UI default daily step goal when the profile hasn't set one. */
const DEFAULT_STEP_GOAL = 10_000;

/** Re-fetch the viewed tracker every this many ms while in a read-only (someone else's) view. */
const READONLY_REFRESH_MS = 30_000;

interface MealSection { meal: Meal; label: string; icon: string }

const MEAL_SECTIONS: MealSection[] = [
  { meal: 'breakfast', label: 'Breakfast', icon: 'bakery_dining' },
  { meal: 'lunch', label: 'Lunch', icon: 'lunch_dining' },
  { meal: 'dinner', label: 'Dinner', icon: 'dinner_dining' },
  { meal: 'snack', label: 'Snacks', icon: 'cookie' },
];

/**
 * Food & fitness tracker dashboard. Renders the active {@link TrackerDayDto} from {@link TrackerStore}:
 * a date navigator, the headline calorie ring + macro bars, the four meal sections (with add/delete),
 * the exercise section, and a read-only shared-view selector. All add/delete controls are hidden when
 * viewing someone else's tracker (store.readOnly). Dialogs resolve with request bodies that the page
 * persists through the store, which then refreshes the day.
 */
@Component({
  selector: 'app-tracker',
  imports: [
    DecimalPipe, FormsModule, MatIconModule, MatButtonModule, MatProgressBarModule, MatMenuModule,
    MatTooltipModule, MatDialogModule, MatSnackBarModule, MatProgressSpinnerModule,
    CalorieRing, HydrationRing, ActivityRing, WeightTrend, WeightStats, OnboardingCard,
  ],
  templateUrl: './tracker.html',
  styleUrl: './tracker.scss',
})
export class Tracker {
  readonly store = inject(TrackerStore);
  readonly auth = inject(AuthService);
  private api = inject(Api);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private destroyRef = inject(DestroyRef);

  readonly mealSections = MEAL_SECTIONS;

  // ---- AI assists (gated by tracker.ai; Gemini-backed, gracefully degrade on 503/error) ----

  /**
   * Whether the AI affordances may render at all. Gated on the trackerAi permission AND the OWN tracker
   * (these endpoints read the caller's own day server-side — they're meaningless / hidden in read-only
   * views of someone else). Everything AI checks this first.
   */
  readonly aiEnabled = computed(() => this.auth.hasPermission(PERM.trackerAi) && !this.store.readOnly());

  /** True once the OWN day has something logged — we only spend the rate-limited key when there's data. */
  readonly hasDataForAi = computed(() => {
    const day = this.store.day();
    if (!day) return false;
    return day.foods.length > 0 || day.exercises.length > 0 || day.hydration.length > 0
      || (day.activity?.steps ?? 0) > 0;
  });

  // Daily coach card (GET daily-coach; cached server-side per day). Lazy: fetched on demand / once per
  // view via the Refresh affordance, never automatically on load, so we don't spam the rate-limited key.
  readonly coachLoading = signal(false);
  readonly coach = signal<DailyCoachResponse | null>(null);
  /** True once a coach fetch failed (503/unavailable) — we show a quiet "unavailable" steer, not an error. */
  readonly coachUnavailable = signal(false);
  /** Polite sr-only announcement for the coach result / its unavailability. */
  readonly coachAnnounce = signal('');

  // Weekly review panel (GET weekly-review; cached server-side).
  readonly weeklyLoading = signal(false);
  readonly weekly = signal<WeeklyReviewResponse | null>(null);
  readonly weeklyUnavailable = signal(false);
  readonly weeklyAnnounce = signal('');

  // "What should I eat?" — suggest foods for the remaining macros (POST suggest-foods; on-demand only).
  readonly suggestLoading = signal(false);
  readonly suggestions = signal<FoodSuggestionDto[] | null>(null);
  readonly suggestUnavailable = signal(false);
  readonly suggestAnnounce = signal('');

  /** Screen-reader-only live status: announces day reloads and entry deletions. */
  readonly statusMsg = signal('');

  /** True while a read-only auto/manual refresh is in flight (for the subtle spinner). */
  readonly refreshing = signal(false);

  /** When the read-only view was last successfully re-fetched (epoch ms), for the "updated" label. */
  readonly lastRefreshed = signal<number | null>(null);

  /** True while the blocking baseline onboarding result is being persisted. */
  readonly savingBaseline = signal(false);

  /** The active read-only auto-refresh interval handle, or null when not running. */
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  /** The caller's OWN weight history (oldest-first) for the trend chart. Empty when none / read-only. */
  readonly weightHistory = signal<WeightPointDto[]>([]);

  /** The caller's OWN per-slot weight stats (averages + morning→evening delta). Null when none / read-only. */
  readonly weightStats = signal<WeightStatsDto | null>(null);

  /** Computed body stats for the current day (server-supplied; null when read-only). */
  readonly stats = computed(() => this.store.day()?.stats ?? null);

  /** True when displaying in imperial units (per the profile preference). */
  readonly imperial = computed(() => this.store.profile()?.unitSystem === 'Imperial');

  constructor() {
    void this.store.load();
    void this.store.loadShared();

    // Announce day reloads (date change / viewing another user) to assistive tech.
    effect(() => {
      const day = this.store.day();
      if (!day) return;
      this.statusMsg.set(`Showing ${this.dateHeading()}, ${Math.round(day.netCalories)} net calories`);
    });

    // Refresh the weight trend whenever a fresh OWN day loads (weight history is private — own only).
    effect(() => {
      const day = this.store.day();
      if (!day || day.readOnly) {
        this.weightHistory.set([]);
        this.weightStats.set(null);
        return;
      }
      void this.loadWeightHistory();
    });

    // Read-only auto-refresh lifecycle: run a gentle 30s re-fetch ONLY while viewing someone else's
    // tracker. Keyed on the view target so switching users restarts a fresh timer for the new target;
    // switching back to your own view (viewUser === null) stops it. Never runs for your own tracker.
    effect(() => {
      const target = this.store.viewUser();
      this.stopAutoRefresh();
      if (target !== null) {
        this.lastRefreshed.set(Date.now());
        this.refreshTimer = setInterval(() => void this.refreshReadOnly(), READONLY_REFRESH_MS);
      }
    });

    // Belt-and-suspenders: clear any interval if the component is torn down.
    this.destroyRef.onDestroy(() => this.stopAutoRefresh());
  }

  private stopAutoRefresh(): void {
    if (this.refreshTimer !== null) {
      clearInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  /**
   * Manually/auto re-fetch the currently-viewed (read-only) tracker. No-ops if we're no longer in a
   * read-only view (e.g. the user switched back), so a queued tick can't fire against your own tracker.
   * The store's load() refreshes signals in place — it doesn't move focus or reset scroll.
   */
  async refreshReadOnly(): Promise<void> {
    if (this.store.viewUser() === null) return;
    this.refreshing.set(true);
    try {
      await this.store.load();
      this.lastRefreshed.set(Date.now());
    } finally {
      this.refreshing.set(false);
    }
  }

  private async loadWeightHistory(): Promise<void> {
    // Trend points + per-slot stats are independent fetches; one failing must not blank the other.
    try {
      this.weightHistory.set(await this.store.weightHistory(90));
    } catch {
      this.weightHistory.set([]);
    }
    try {
      this.weightStats.set(await this.store.weightStats(90));
    } catch {
      this.weightStats.set(null);
    }
  }

  // ---- stats / unit display helpers ----

  /** Format a metric kg weight in the user's chosen units (or '—' when null). */
  weightLabel(kg: number | null | undefined, dp = 1): string {
    return formatWeight(kg, this.imperial(), dp) ?? '—';
  }

  /** Progress toward goal weight as 0..100 (relative to current vs goal, monotonic toward goal). */
  goalProgressPct(): number | null {
    const p = this.store.profile();
    const w = p?.weightKg, g = p?.goalWeightKg;
    if (w == null || g == null || g <= 0 || w <= 0) return null;
    if (w === g) return 100;
    // Distance remaining as a share of the current gap; we just show "how far from goal" inverted.
    const diff = Math.abs(w - g);
    const denom = Math.max(w, g);
    return Math.max(0, Math.min(100, (1 - diff / denom) * 100));
  }

  /** Weight remaining to goal, formatted with direction, in display units (null when not computable). */
  goalDelta(): { text: string; toLose: boolean } | null {
    const p = this.store.profile();
    const w = p?.weightKg, g = p?.goalWeightKg;
    if (w == null || g == null || g <= 0 || w <= 0) return null;
    const diffKg = w - g;
    if (Math.abs(diffKg) < 0.05) return { text: 'at goal', toLose: false };
    const mag = this.imperial() ? Math.abs(kgToLb(diffKg)) : Math.abs(diffKg);
    const unit = this.imperial() ? 'lb' : 'kg';
    return { text: `${mag.toFixed(1)} ${unit} to ${diffKg > 0 ? 'lose' : 'gain'}`, toLose: diffKg > 0 };
  }

  /** CSS class for the BMI category chip. */
  bmiClass(): string {
    const cat = this.stats()?.bmiCategory;
    switch (cat) {
      case 'Underweight': return 'is-under';
      case 'Normal': return 'is-normal';
      case 'Overweight': return 'is-over';
      case 'Obese': return 'is-obese';
      default: return '';
    }
  }

  // ---- date navigation ----
  prevDay(): void { void this.store.shiftDate(-1); }
  nextDay(): void { void this.store.shiftDate(1); }
  today(): void { void this.store.goToday(); }
  onDateInput(value: string): void { if (value) void this.store.setDate(value); }

  /** A friendly heading for the viewed date (Today / Yesterday / weekday). */
  readonly dateHeading = computed(() => {
    const iso = this.store.date();
    const d = new Date(iso + 'T00:00:00');
    const today = new Date(); today.setHours(0, 0, 0, 0);
    const diff = Math.round((d.getTime() - today.getTime()) / 86_400_000);
    if (diff === 0) return 'Today';
    if (diff === -1) return 'Yesterday';
    if (diff === 1) return 'Tomorrow';
    return d.toLocaleDateString(undefined, { weekday: 'long', month: 'short', day: 'numeric' });
  });

  /** True before any data exists for the OWN tracker and no goal is set — show the setup prompt. */
  readonly needsSetup = computed(() => {
    const day = this.store.day();
    if (!day || day.readOnly) return false;
    const p = day.profile;
    const empty = day.foods.length === 0 && day.exercises.length === 0;
    return empty && !p.dailyCalorieGoal && (!p.goal || p.goal === 'Maintain') && !p.weightKg && !p.shareWithContacts;
  });

  /**
   * True when the caller's OWN baseline is incomplete: any of current weight, height, date of birth, or
   * an explicit biological sex is missing. Drives the BLOCKING onboarding step that replaces the
   * dashboard until saved. Never true when viewing someone else (read-only) — their metrics aren't
   * exposed and we never gate their tracker.
   */
  readonly needsBaseline = computed(() => {
    const day = this.store.day();
    if (!day || day.readOnly) return false;
    const p = day.profile;
    return p.weightKg == null || p.heightCm == null || !p.dateOfBirth || p.sex === 'Unspecified';
  });

  // ---- macro helpers ----
  foodsFor(meal: Meal): FoodEntryDto[] {
    return this.store.day()?.foods.filter(f => f.meal === meal) ?? [];
  }

  caloriesFor(meal: Meal): number {
    return this.foodsFor(meal).reduce((s, f) => s + f.calories, 0);
  }

  /** Width % for a macro bar against its (optional) target; falls back to a soft scale when no target. */
  macroPct(value: number, target?: number): number {
    if (target && target > 0) return Math.min(100, (value / target) * 100);
    // No target: scale against a nominal 200 g so the bar still reads as "more/less".
    return Math.min(100, (value / 200) * 100);
  }

  // ---- shared view ----
  get viewingUser(): SharedUserDto | null {
    const userId = this.store.viewUser();
    if (userId == null) return null;
    // Fall back to the server-resolved name on the loaded day when the picker list lacks the row.
    return this.store.shared().find(s => s.userId === userId)
      ?? { userId, name: this.store.day()?.userName ?? 'Unknown user' };
  }

  viewSelf(): void { void this.store.viewUserTracker(null); }
  viewOther(userId: number): void { void this.store.viewUserTracker(userId); }

  // ---- dialogs ----
  openAddFood(meal: Meal): void {
    if (this.store.readOnly()) return;
    const data: AddFoodData = { date: this.store.date(), meal };
    this.dialog.open(AddFoodDialog, { data, width: '500px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((req: AddFoodRequest | AddFoodRequest[] | undefined) => {
        if (!req) return;
        // The AI multi-item flows (photo / "describe your meal") resolve with an ARRAY; log them in order.
        const reqs = Array.isArray(req) ? req : [req];
        if (reqs.length === 0) return;
        const run = reqs.reduce<Promise<unknown>>((p, r) => p.then(() => this.store.addFood(r)), Promise.resolve());
        run
          .then(() => {
            if (reqs.length > 1) {
              this.snack.open(`Added ${reqs.length} foods`, 'OK', { duration: 2500 });
            } else {
              // A single manual log (no provider source + no FDC id) is auto-saved to "My foods".
              const r = reqs[0];
              const wasManual = !r.source && r.fdcId == null;
              this.snack.open(
                wasManual ? `Added ${r.description} · saved to My foods` : `Added ${r.description}`,
                'OK', { duration: 2500 });
            }
          })
          .catch(() => this.snack.open('Could not add food', 'Dismiss', { duration: 4000 }));
      });
  }

  openAddExercise(): void {
    if (this.store.readOnly()) return;
    const p = this.store.profile();
    const data: AddExerciseData = {
      date: this.store.date(),
      goal: p?.goal ?? '',
      hasWeight: (p?.weightKg ?? 0) > 0,
    };
    this.dialog.open(AddExerciseDialog, { data, width: '820px', maxWidth: '94vw', autoFocus: false })
      .afterClosed().subscribe((req: AddExerciseRequest | undefined) => {
        if (!req) return;
        this.store.addExercise(req)
          .then(() => this.snack.open('Exercise logged', 'OK', { duration: 2000 }))
          .catch(() => this.snack.open('Could not log exercise', 'Dismiss', { duration: 4000 }));
      });
  }

  openProfile(): void {
    if (this.store.readOnly()) return;
    const profile: TrackerProfileDto = this.store.profile()
      ?? { goal: 'Maintain', shareWithContacts: false, sex: 'Unspecified', activityLevel: 'Sedentary', unitSystem: 'Imperial' };
    const data: ProfileData = { profile };
    this.dialog.open(ProfileDialog, { data, width: '460px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((req: TrackerProfileDto | undefined) => {
        if (!req) return;
        this.store.saveProfile(req)
          .then(() => this.snack.open('Goals saved', 'OK', { duration: 2000 }))
          .catch(() => this.snack.open('Could not save goals', 'Dismiss', { duration: 4000 }));
      });
  }

  /**
   * Complete the blocking baseline onboarding (own tracker only). Persists the profile, and — if no
   * weight has been logged for today yet — records the entered current weight as today's WeightEntry so
   * the trend/BMI/BMR have a day-one baseline. Both calls reload the day; the gate (needsBaseline) then
   * clears and the normal dashboard renders.
   */
  async onBaselineComplete(result: OnboardingResult): Promise<void> {
    if (this.store.readOnly() || this.savingBaseline()) return;
    this.savingBaseline.set(true);
    try {
      await this.store.saveProfile(result.profile);

      // Seed today's weigh-in only if today doesn't already have one (avoid clobbering an existing entry).
      const today = this.store.date();
      const history = await this.store.weightHistory(7).catch(() => []);
      const hasToday = history.some(w => w.date === today);
      if (!hasToday) {
        await this.store.logWeight({ date: today, weightKg: result.weightKg });
      }

      await this.store.load();
      void this.loadWeightHistory();
      this.snack.open('Baseline saved', 'OK', { duration: 2000 });
    } catch {
      this.snack.open('Could not save your baseline', 'Dismiss', { duration: 4000 });
    } finally {
      this.savingBaseline.set(false);
    }
  }

  openLogWeight(): void {
    if (this.store.readOnly()) return;
    const p = this.store.profile();
    const data: LogWeightData = {
      date: this.store.date(),
      unitSystem: (p?.unitSystem ?? 'Imperial'),
      currentKg: p?.weightKg ?? null,
    };
    this.dialog.open(LogWeightDialog, { data, width: '360px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((req: LogWeightRequest | undefined) => {
        if (!req) return;
        this.store.logWeight(req)
          .then(() => {
            this.snack.open('Weight logged', 'OK', { duration: 2000 });
            void this.loadWeightHistory();
          })
          .catch(() => this.snack.open('Could not log weight', 'Dismiss', { duration: 4000 }));
      });
  }

  // ---- hydration ----

  /** Format a metric volume (ml) in the user's chosen units (or '—' when null). */
  volumeLabel(ml: number | null | undefined): string {
    return formatVolume(ml, this.imperial()) ?? '—';
  }

  /** Quick-add a fixed amount of water (Glass/Bottle/Large) to today's hydration. */
  quickHydration(amountMl: number): void {
    if (this.store.readOnly()) return;
    this.store.addHydration({ date: this.store.date(), amountMl })
      .then(() => this.announceHydration(`Added ${this.volumeLabel(amountMl)}`))
      .catch(() => this.snack.open('Could not log drink', 'Dismiss', { duration: 4000 }));
  }

  /**
   * Announce a hydration change to the single existing SR live region (statusMsg), suffixed with the
   * running total vs goal so the user hears their progress (e.g. "Added 8 oz, 24 oz of 64 oz").
   */
  private announceHydration(prefix: string): void {
    const day = this.store.day();
    const total = this.volumeLabel(day?.hydrationMl);
    const goal = this.volumeLabel(day?.hydrationGoalMl);
    this.statusMsg.set(`${prefix}, ${total} of ${goal}`);
  }

  /**
   * Open the custom-drink dialog (amount in the user's units + optional drink label). For tracker.ai
   * users the dialog can also resolve a list of AI-parsed drinks, or an accepted AI-suggested daily
   * hydration goal to persist on the profile.
   */
  openAddHydration(): void {
    if (this.store.readOnly()) return;
    const p = this.store.profile();
    const data: AddHydrationData = { date: this.store.date(), unitSystem: p?.unitSystem ?? 'Imperial' };
    this.dialog.open(AddHydrationDialog, { data, width: '400px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((res: AddHydrationResult | undefined) => {
        if (!res) return;
        if (res.kind === 'goal') {
          this.applyHydrationGoal(res.targetMl);
          return;
        }
        this.logDrinks(res.requests, res.kind === 'parsed');
      });
  }

  /** Log one or more drinks sequentially (each refreshes the day), with a single summary snackbar. */
  private async logDrinks(requests: AddHydrationRequest[], parsed: boolean): Promise<void> {
    let ok = 0;
    for (const req of requests) {
      try {
        await this.store.addHydration(req);
        ok++;
      } catch { /* keep going; report the shortfall at the end */ }
    }
    if (ok === 0) {
      this.snack.open('Could not log drink', 'Dismiss', { duration: 4000 });
    } else if (parsed || requests.length > 1) {
      this.snack.open(`Added ${ok} drink${ok === 1 ? '' : 's'}`, 'OK', { duration: 2000 });
    } else {
      this.snack.open(`Added ${requests[0].label || 'drink'}`, 'OK', { duration: 2000 });
    }
  }

  /** Persist an AI-accepted daily hydration target (ml) onto the profile, then refresh. */
  private applyHydrationGoal(targetMl: number): void {
    const current = this.store.profile();
    if (!current) return;
    this.store.saveProfile({ ...current, hydrationGoalMl: targetMl })
      .then(() => this.snack.open('Hydration goal updated', 'OK', { duration: 2000 }))
      .catch(() => this.snack.open('Could not update hydration goal', 'Dismiss', { duration: 4000 }));
  }

  removeHydration(h: HydrationEntryDto): void {
    if (this.store.readOnly()) return;
    this.store.deleteHydration(h.id)
      .then(() => this.announceHydration('Removed drink'))
      .catch(() => this.snack.open('Could not remove entry', 'Dismiss', { duration: 4000 }));
  }

  /** Short local time (e.g. "3:24 PM") from an ISO-8601 UTC string, for a hydration entry. */
  entryTime(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  // ---- watch activity (steps / distance / active calories) ----

  /** The day's watch stats, or null when no row exists. */
  readonly activity = computed(() => this.store.day()?.activity ?? null);

  /** The resolved daily step goal: the day's stepGoal, else the UI default (~10000). */
  readonly stepGoal = computed(() => this.store.day()?.stepGoal ?? DEFAULT_STEP_GOAL);

  /** The active calorie mode for the toggle (defaults to "add" when there's no row yet). */
  readonly calorieMode = computed<ActivityCalorieMode>(() => this.activity()?.calorieMode ?? 'add');

  /** Format a metric distance (metres) in the user's chosen units (or '—' when null). */
  distanceLabel(meters: number | null | undefined): string {
    return formatDistance(meters, this.imperial()) ?? '—';
  }

  /**
   * A short hint describing how the watch active calories shaped the resolved burn, or null when there's
   * nothing to add/override (no watch row or no active-calories value). Drives the small note under the
   * activity card so the user understands the calorie ring's "burned".
   */
  readonly burnHint = computed<string | null>(() => {
    const day = this.store.day();
    const act = this.activity();
    if (!day || !act || act.activeCalories == null) return null;
    const active = Math.round(act.activeCalories).toLocaleString();
    if (act.calorieMode === 'override') {
      return `${day.caloriesOut.toLocaleString()} burned — watch active, replaces workouts`;
    }
    return `+${active} watch active on top of ${day.exerciseCalories.toLocaleString()} from workouts`;
  });

  /**
   * Flip the add/override calorie mode (segmented toggle) and persist it, carrying the day's existing
   * steps/distance/active calories so the upsert only changes the mode. No-op in read-only views or when
   * the mode is unchanged. The store reloads the day so the calorie ring / burned figure updates.
   */
  setCalorieMode(mode: ActivityCalorieMode): void {
    if (this.store.readOnly() || mode === this.calorieMode()) return;
    const act = this.activity();
    const body: UpsertActivityRequest = {
      date: this.store.date(),
      steps: act?.steps ?? null,
      distanceMeters: act?.distanceMeters ?? null,
      activeCalories: act?.activeCalories ?? null,
      calorieMode: mode,
    };
    this.store.upsertActivity(body)
      .then(() => this.snack.open(
        mode === 'override' ? 'Watch replaces workouts' : 'Watch adds to workouts', 'OK', { duration: 2000 }))
      .catch(() => this.snack.open('Could not update', 'Dismiss', { duration: 4000 }));
  }

  /** Open the edit/add watch-stats dialog (steps, distance in user units, active calories, mode). */
  openActivity(): void {
    if (this.store.readOnly()) return;
    const p = this.store.profile();
    const data: AddActivityData = {
      date: this.store.date(),
      unitSystem: p?.unitSystem ?? 'Imperial',
      activity: this.activity(),
    };
    this.dialog.open(AddActivityDialog, { data, width: '380px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((res: UpsertActivityRequest | 'clear' | undefined) => {
        if (!res) return;
        if (res === 'clear') {
          this.store.clearActivity(this.store.date())
            .then(() => this.snack.open('Watch stats cleared', 'OK', { duration: 2000 }))
            .catch(() => this.snack.open('Could not clear', 'Dismiss', { duration: 4000 }));
          return;
        }
        this.store.upsertActivity(res)
          .then(() => this.snack.open('Watch stats saved', 'OK', { duration: 2000 }))
          .catch(() => this.snack.open('Could not save watch stats', 'Dismiss', { duration: 4000 }));
      });
  }

  // ---- AI assists ----

  /**
   * Fetch the daily coach insight + tips on demand (button / Refresh). The endpoint caches per day
   * server-side, but we still only call it lazily — never on load — and only when there's data, so we
   * don't spam the rate-limited key. A 503/error flips to a quiet "unavailable" steer; the dashboard
   * is never blocked.
   */
  async loadCoach(): Promise<void> {
    if (!this.aiEnabled() || this.coachLoading()) return;
    this.coachLoading.set(true);
    this.coachUnavailable.set(false);
    this.coachAnnounce.set('Getting your daily coaching…');
    try {
      const res = await firstValueFrom(this.api.dailyCoach());
      this.coach.set(res);
      this.coachAnnounce.set(`Coach: ${res.insight}` + (res.tips.length ? ` ${res.tips.length} tips.` : ''));
    } catch {
      this.coach.set(null);
      this.coachUnavailable.set(true);
      this.coachAnnounce.set('AI coaching is unavailable right now.');
    } finally {
      this.coachLoading.set(false);
    }
  }

  /** Fetch the weekly review on demand. Same graceful-degrade contract as {@link loadCoach}. */
  async loadWeekly(): Promise<void> {
    if (!this.aiEnabled() || this.weeklyLoading()) return;
    this.weeklyLoading.set(true);
    this.weeklyUnavailable.set(false);
    this.weeklyAnnounce.set('Reviewing your week…');
    try {
      const res = await firstValueFrom(this.api.weeklyReview());
      this.weekly.set(res);
      this.weeklyAnnounce.set(`This week: ${res.summary} ${res.suggestion}`);
    } catch {
      this.weekly.set(null);
      this.weeklyUnavailable.set(true);
      this.weeklyAnnounce.set('The weekly review is unavailable right now.');
    } finally {
      this.weeklyLoading.set(false);
    }
  }

  /**
   * Ask the AI what to eat for the remaining calories/macros today (reads the caller's own day
   * server-side). On-demand only. Each suggestion is a prompt the user acts on through the normal,
   * editable Add Food flow — nothing is auto-logged. A 503/error degrades gracefully.
   */
  async suggestFoods(): Promise<void> {
    if (!this.aiEnabled() || this.suggestLoading()) return;
    this.suggestLoading.set(true);
    this.suggestUnavailable.set(false);
    this.suggestAnnounce.set('Finding foods for your remaining macros…');
    try {
      const res = await firstValueFrom(this.api.suggestFoods());
      this.suggestions.set(res.suggestions);
      this.suggestAnnounce.set(res.suggestions.length
        ? `${res.suggestions.length} food ${res.suggestions.length === 1 ? 'idea' : 'ideas'} for your remaining macros.`
        : 'No suggestions right now.');
    } catch {
      this.suggestions.set(null);
      this.suggestUnavailable.set(true);
      this.suggestAnnounce.set('Food suggestions are unavailable right now — add food manually.');
    } finally {
      this.suggestLoading.set(false);
    }
  }

  /** Dismiss the food-suggestions list (clears the panel; the button can re-fetch). */
  clearSuggestions(): void {
    this.suggestions.set(null);
    this.suggestUnavailable.set(false);
  }

  /**
   * Act on an AI food suggestion: open the standard Add Food dialog with the name pre-seeded into the
   * search box. It's an editable prefill — the user searches/confirms and sets quantity; nothing logs
   * automatically. Defaults to the snack meal as a neutral target.
   */
  addSuggestedFood(s: FoodSuggestionDto): void {
    if (this.store.readOnly()) return;
    const data: AddFoodData = { date: this.store.date(), meal: 'snack', prefillQuery: s.food };
    this.dialog.open(AddFoodDialog, { data, width: '500px', maxWidth: '95vw', autoFocus: false })
      .afterClosed().subscribe((req: AddFoodRequest | AddFoodRequest[] | undefined) => {
        if (!req) return;
        const reqs = Array.isArray(req) ? req : [req];
        const run = reqs.reduce<Promise<unknown>>((p, r) => p.then(() => this.store.addFood(r)), Promise.resolve());
        run
          .then(() => this.snack.open(`Added ${reqs.length === 1 ? reqs[0].description : reqs.length + ' foods'}`, 'OK', { duration: 2500 }))
          .catch(() => this.snack.open('Could not add food', 'Dismiss', { duration: 4000 }));
      });
  }

  removeFood(f: FoodEntryDto): void {
    if (this.store.readOnly()) return;
    this.store.deleteFood(f.id)
      .then(() => this.statusMsg.set(`Removed ${f.description}`))
      .catch(() => this.snack.open('Could not remove entry', 'Dismiss', { duration: 4000 }));
  }

  removeExercise(e: ExerciseEntryDto): void {
    if (this.store.readOnly()) return;
    this.store.deleteExercise(e.id)
      .then(() => this.statusMsg.set(`Removed ${e.name}`))
      .catch(() => this.snack.open('Could not remove entry', 'Dismiss', { duration: 4000 }));
  }

  /** Two-letter initials for the shared-user avatar fallback (name only; no email — email-privacy). */
  initials(u: { name?: string }): string {
    const parts = (u.name || '').split(/[\s@.]+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || 'U';
  }
}
