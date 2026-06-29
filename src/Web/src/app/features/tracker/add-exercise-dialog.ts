import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Subject,
  catchError,
  debounceTime,
  distinctUntilChanged,
  firstValueFrom,
  of,
  switchMap,
} from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { BetaEmptyState } from '../beta-ui';
import {
  AddExerciseRequest,
  CustomExerciseDto,
  ExerciseLibraryDto,
  PERM,
  ParseExerciseResponse,
  SuggestWorkoutResponse,
  WorkoutXExerciseDto,
} from '../../core/models';

/** Curated WorkoutX filter values — the provider has no option-list endpoint, so these mirror the catalog. */
const WX_BODY_PARTS = [
  'Back',
  'Cardio',
  'Chest',
  'Lower Arms',
  'Lower Legs',
  'Neck',
  'Shoulders',
  'Upper Arms',
  'Upper Legs',
  'Waist',
];
const WX_EQUIPMENT = [
  'Body Weight',
  'Barbell',
  'Dumbbell',
  'Cable',
  'Leverage Machine',
  'Kettlebell',
  'Resistance Band',
  'Smith Machine',
];
const WX_TARGETS = [
  'Abs',
  'Biceps',
  'Triceps',
  'Pectorals',
  'Quads',
  'Glutes',
  'Hamstrings',
  'Lats',
  'Delts',
  'Calves',
];

/** Focus-area presets for the AI workout suggester (free-text is also allowed via the input). */
const AI_FOCUS_OPTIONS = [
  'Full body',
  'Upper body',
  'Lower body',
  'Core',
  'Cardio',
  'Push',
  'Pull',
  'Legs',
  'Mobility',
];

/** Equipment presets for the AI workout suggester ('' = "No equipment / bodyweight"). */
const AI_EQUIPMENT_OPTIONS = [
  'Dumbbells',
  'Barbell',
  'Kettlebell',
  'Resistance bands',
  'Pull-up bar',
  'Full gym',
];

/** Burn-target presets (kcal) for the "What burns ~N cal?" planner. */
const AI_BURN_TARGETS = [100, 200, 300, 500];

/** Opens with the active day + whether the profile has a weight (so we can estimate from duration). */
export interface AddExerciseData {
  date: string;
  goal: string;
  /** True when the profile has a weight → duration alone yields a server-side calorie estimate. */
  hasWeight: boolean;
}

/**
 * Add-exercise dialog. Three ways in: pick from the exercise LIBRARY (default = your goal, with a toggle
 * to show all goals) then enter a duration — the server estimates calories from your profile weight, or
 * you can override with a manual figure; browse/search the WORKOUTX catalog (GIF demo, instructions,
 * muscles, recommended sets/reps) and log with a duration → live calorie estimate (override available);
 * OR log a fully MANUAL exercise (free-text name + calories). Resolves with the {@link AddExerciseRequest}
 * for the page to persist via the store.
 */
@Component({
  selector: 'app-add-exercise-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatIconModule,
    MatSelectModule,
    MatProgressBarModule,
    MatProgressSpinnerModule,
    BetaEmptyState,
  ],
  templateUrl: './add-exercise-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './add-exercise-dialog.scss',
})
export class AddExerciseDialog {
  private api = inject(Api);
  private ref = inject(MatDialogRef<AddExerciseDialog, AddExerciseRequest>);
  private injector = inject(Injector);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);
  readonly data = inject<AddExerciseData>(MAT_DIALOG_DATA);

  readonly mode = signal<'library' | 'workoutx' | 'manual' | 'ai'>('library');

  /** Gate: every AI affordance (the whole "AI" tab) is hidden unless the caller holds tracker.ai. */
  readonly canUseAi = this.auth.hasPermission(PERM.trackerAi);

  // Curated filter lists for the WorkoutX tab (no option-list endpoint exists).
  readonly wxBodyPartOptions = WX_BODY_PARTS;
  readonly wxEquipmentOptions = WX_EQUIPMENT;
  readonly wxTargetOptions = WX_TARGETS;

  // ---- library ----
  readonly loading = signal(false);
  readonly library = signal<ExerciseLibraryDto[]>([]);
  readonly showAll = signal(false);
  readonly query = signal('');
  readonly selected = signal<ExerciseLibraryDto | null>(null);
  readonly durationMin = signal<number | null>(30);
  /** When true, the user overrides the server estimate with a typed calorie figure. */
  readonly overrideCals = signal(false);
  readonly manualCalsForLib = signal<number | null>(null);

  // ---- WorkoutX catalog ----
  private static readonly WX_PAGE = 24;
  readonly wxQuery = signal('');
  readonly wxBodyPart = signal('');
  readonly wxTarget = signal('');
  readonly wxEquipment = signal('');
  readonly wxLoading = signal(false);
  /** True once a request came back 503 — WorkoutX isn't configured; show a friendly steer. */
  readonly wxUnavailable = signal(false);
  readonly wxError = signal<string | null>(null);
  readonly wxResults = signal<WorkoutXExerciseDto[]>([]);
  readonly wxTotal = signal(0);
  readonly wxOffset = signal(0);
  readonly wxSelected = signal<WorkoutXExerciseDto | null>(null);
  readonly wxDuration = signal<number | null>(30);
  /** When true, the user overrides the live estimate with a typed calorie figure. */
  readonly wxOverride = signal(false);
  readonly wxManualCals = signal<number | null>(null);
  // GIF demo: object URL of the proxied blob, plus loading/error states. Revoked on change/destroy.
  readonly wxGifUrl = signal<string | null>(null);
  readonly wxGifLoading = signal(false);
  readonly wxGifError = signal(false);
  /** Detected once: when the user prefers reduced motion we don't auto-load the animated GIF. */
  readonly wxReducedMotion = signal(
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );
  /** Set true when a reduced-motion user explicitly asks to play the demo; reset on selection change. */
  readonly wxGifRequested = signal(false);

  /** Whether the loaded page has more results behind it. */
  readonly wxHasMore = computed(() => this.wxResults().length < this.wxTotal());

  /** Live estimate for the selected WorkoutX exercise: round(caloriesPerMinute * durationMin). */
  readonly wxEstimate = computed<number>(() => {
    const sel = this.wxSelected();
    const min = this.wxDuration() ?? 0;
    if (!sel || min <= 0) return 0;
    return Math.round(sel.caloriesPerMinute * min);
  });

  /** The calorie figure that will be logged for a WorkoutX pick (override beats the live estimate). */
  readonly wxEffectiveCals = computed<number>(() =>
    this.wxOverride() ? (this.wxManualCals() ?? 0) : this.wxEstimate(),
  );

  // ---- fully manual ----
  readonly mName = signal('');
  readonly mCalories = signal<number | null>(null);
  readonly mDuration = signal<number | null>(null);
  /** The manual name field — focused after a "My exercises" pick prefills the form below the list. */
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('nameInput');
  /** Brief sr-only announcement (e.g. "Loaded {name}") spoken after a saved pick prefills the form. */
  readonly pickAnnounce = signal('');

  // ---- "My exercises" quick-pick (per-user saved library, atop the Manual tab) ----
  readonly savedQuery = signal('');
  readonly savedLoading = signal(false);
  readonly saved = signal<CustomExerciseDto[]>([]);
  private readonly savedQueryStream = new Subject<string>();
  private savedLoadedOnce = false;
  /**
   * Id of the saved exercise the manual form was last prefilled from, or null for a freshly-typed
   * entry. Drives source="custom" (bump the saved row) vs no source (auto-save a new one). Cleared the
   * moment the user edits the name away from the picked one.
   */
  readonly pickedSavedId = signal<number | null>(null);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * Show the subtle "saved to My exercises" hint while typing a fresh manual entry — i.e. a non-empty
   * name that wasn't re-picked from the saved library (those bump instead of creating a new row).
   */
  readonly mWillSave = computed(
    () => this.pickedSavedId() === null && this.mName().trim().length > 0,
  );

  // ---- AI calorie estimate for a manual exercise (Gemini-backed; optional, degrades gracefully) ----
  /** True while estimate-exercise is in flight. */
  readonly aiLoading = signal(false);
  /** Set once an AI estimate prefilled the calories field → renders the "AI estimate" chip. */
  readonly aiEstimated = signal(false);
  /** The model's optional short note (e.g. "assumes a 70 kg adult"). */
  readonly aiNote = signal<string | null>(null);
  /** Polite sr-only announcement of the AI result (or its unavailability). */
  readonly aiAnnounce = signal('');

  /** Can we ask the AI? A name + a positive duration are required to estimate burn. */
  readonly canEstimateAi = computed(
    () => !this.aiLoading() && this.mName().trim().length > 0 && (this.mDuration() ?? 0) > 0,
  );

  /**
   * Ask Gemini to estimate calories burned for the typed exercise name over the typed duration, then
   * PREFILL the editable calories field. The estimate is a suggestion the user can adjust. A
   * 503/unavailable leaves the field editable and shows a snackbar.
   */
  async estimateWithAi(): Promise<void> {
    if (!this.canEstimateAi()) return;
    this.aiLoading.set(true);
    this.aiAnnounce.set('Estimating calories with AI…');
    try {
      const res = await firstValueFrom(
        this.api.estimateExercise({
          name: this.mName().trim(),
          durationMin: this.mDuration() ?? 0,
        }),
      );
      this.mCalories.set(res.caloriesBurned); // editable — never silently committed
      this.aiNote.set(res.note ?? null);
      this.aiEstimated.set(true);
      this.aiAnnounce.set(
        `AI estimate: ${res.caloriesBurned} calories burned.` + (res.note ? ` ${res.note}` : ''),
      );
    } catch {
      this.aiEstimated.set(false);
      this.aiAnnounce.set('AI estimate unavailable. Enter the calories manually.');
      this.snack.open('AI estimate unavailable — enter manually', 'OK', { duration: 4000 });
    } finally {
      this.aiLoading.set(false);
    }
  }

  /** Clear the "AI estimate" chip + note (called when the user edits the calories field themselves). */
  clearAiEstimate(): void {
    this.aiEstimated.set(false);
    this.aiNote.set(null);
    // The single polite live region renders `aiAnnounce() || pickAnnounce()`; clearing the AI text here
    // lets a later "picked saved exercise" announcement surface instead of being masked by a stale one.
    this.aiAnnounce.set('');
  }

  // ======================================================================================
  // ---- AI tab (gated by tracker.ai) — three Gemini-backed assists, each EDITABLE before logging ----
  // ======================================================================================

  // Curated presets for the workout-suggester selects (free-text focus is also accepted).
  readonly aiFocusOptions = AI_FOCUS_OPTIONS;
  readonly aiEquipmentOptions = AI_EQUIPMENT_OPTIONS;
  readonly aiBurnTargets = AI_BURN_TARGETS;

  // -- 1) Natural-language logging (the headline feature): free text → parsed, editable exercise. --
  readonly nlText = signal('');
  readonly nlLoading = signal(false);
  /** The parsed result; once set, the editable prefill card renders. Null until a successful parse. */
  readonly nlResult = signal<ParseExerciseResponse | null>(null);
  // Editable prefill fields (seeded from the parse, never auto-committed).
  readonly nlName = signal('');
  readonly nlCalories = signal<number | null>(null);
  readonly nlDuration = signal<number | null>(null);
  /** Polite sr-only announcement of the parse result (or its unavailability). */
  readonly nlAnnounce = signal('');

  readonly canParseNl = computed(() => !this.nlLoading() && this.nlText().trim().length > 0);
  readonly canLogNl = computed(
    () => !this.saving() && this.nlName().trim().length > 0 && (this.nlCalories() ?? 0) > 0,
  );

  // -- 2) Workout suggestion: focus + minutes + equipment → a routine the user can log. --
  readonly swFocus = signal('Full body');
  readonly swMinutes = signal<number | null>(30);
  readonly swEquipment = signal('');
  readonly swLoading = signal(false);
  readonly swResult = signal<SuggestWorkoutResponse | null>(null);
  /** Editable name + calories prefilled from the suggested routine (logged as one manual entry). */
  readonly swName = signal('');
  readonly swCalories = signal<number | null>(null);
  readonly swAnnounce = signal('');

  readonly canSuggestWorkout = computed(
    () => !this.swLoading() && this.swFocus().trim().length > 0 && (this.swMinutes() ?? 0) > 0,
  );

  // -- 3) Burn-target planner: "What burns ~N cal?" → options scaled to the user (reuses suggest-workout). --
  readonly btTarget = signal<number | null>(300);
  readonly btEquipment = signal('');
  readonly btLoading = signal(false);
  readonly btResult = signal<SuggestWorkoutResponse | null>(null);
  readonly btAnnounce = signal('');

  readonly canPlanBurn = computed(() => !this.btLoading() && (this.btTarget() ?? 0) > 0);

  /** A single polite live region renders the most recent AI-tab announcement. */
  readonly aiTabAnnounce = computed(
    () => this.nlAnnounce() || this.swAnnounce() || this.btAnnounce(),
  );

  /**
   * Headline feature — parse a free-text log ("3x10 squats", "jogged 2 miles") into a structured
   * exercise. Calories are computed server-side from the CALLER's own body weight. The result PREFILLS
   * editable fields; nothing is committed until the user clicks "Log this". A 503/error keeps the box
   * usable and surfaces a snackbar.
   */
  async parseNaturalLanguage(): Promise<void> {
    if (!this.canParseNl()) return;
    this.nlLoading.set(true);
    this.nlResult.set(null);
    this.nlAnnounce.set('Reading your description with AI…');
    try {
      const res = await firstValueFrom(this.api.parseExercise({ text: this.nlText().trim() }));
      this.nlResult.set(res);
      this.nlName.set(res.name);
      this.nlCalories.set(res.calories);
      this.nlDuration.set(res.durationMin ?? null);
      const bits = [`${res.calories} calories`];
      if (res.durationMin) bits.push(`${res.durationMin} minutes`);
      this.nlAnnounce.set(`Parsed ${res.name}: ${bits.join(', ')}. Review and edit, then log it.`);
    } catch {
      this.nlAnnounce.set('AI is unavailable. Switch to the Manual tab to enter it yourself.');
      this.snack.open('AI unavailable — add it manually on the Manual tab', 'OK', {
        duration: 4000,
      });
    } finally {
      this.nlLoading.set(false);
    }
  }

  /** Clear the parsed-exercise card (e.g. to re-describe). */
  clearNlResult(): void {
    this.nlResult.set(null);
    this.nlAnnounce.set('');
  }

  /** Log the (edited) natural-language result as a manual entry → backend auto-saves it to "My exercises". */
  logNaturalLanguage(): void {
    if (!this.canLogNl()) return;
    this.ref.close({
      date: this.data.date,
      name: this.nlName().trim(),
      durationMin: (this.nlDuration() ?? 0) > 0 ? this.nlDuration()! : undefined,
      caloriesBurned: this.nlCalories() ?? 0,
    });
  }

  /**
   * Suggest a workout for the chosen focus + minutes + equipment. Shows the routine; the user can then
   * log it (prefilled name + estimated calories, both editable). 503/error keeps fields usable.
   */
  async suggestWorkout(): Promise<void> {
    if (!this.canSuggestWorkout()) return;
    this.swLoading.set(true);
    this.swResult.set(null);
    this.swAnnounce.set('Building a workout with AI…');
    try {
      const res = await firstValueFrom(
        this.api.suggestWorkout({
          focus: this.swFocus().trim(),
          minutes: this.swMinutes() ?? 0,
          equipment: this.swEquipment().trim() || undefined,
        }),
      );
      this.swResult.set(res);
      this.swName.set(res.title);
      this.swCalories.set(res.estCalories);
      this.swAnnounce.set(
        `Suggested ${res.title} with ${res.items.length} exercises, about ${res.estCalories} calories. ` +
          `Review and log it.`,
      );
    } catch {
      this.swAnnounce.set('AI is unavailable. Try the Library or Manual tabs.');
      this.snack.open('AI unavailable — use the Library or Manual tabs', 'OK', { duration: 4000 });
    } finally {
      this.swLoading.set(false);
    }
  }

  /** Log the suggested workout as one manual entry (edited name + est calories). */
  logSuggestedWorkout(): void {
    if (this.saving() || this.swName().trim().length === 0 || (this.swCalories() ?? 0) <= 0) return;
    this.ref.close({
      date: this.data.date,
      name: this.swName().trim(),
      durationMin: (this.swMinutes() ?? 0) > 0 ? this.swMinutes()! : undefined,
      caloriesBurned: this.swCalories() ?? 0,
    });
  }

  /**
   * Burn-target planner — "what burns ~N cal?". Reuses suggest-workout, asking the model to build a
   * routine whose estimated burn matches the target; shows the scaled options. The user can log it just
   * like a suggestion (prefilling name + the target calories, editable).
   */
  async planBurn(): Promise<void> {
    if (!this.canPlanBurn()) return;
    const target = this.btTarget() ?? 0;
    this.btLoading.set(true);
    this.btResult.set(null);
    this.btAnnounce.set(`Finding a workout that burns about ${target} calories…`);
    try {
      // The suggester takes focus + minutes; encode the calorie target in the focus text and give it a
      // generous time budget so the model scales the routine to the target burn (clamped server-side).
      const res = await firstValueFrom(
        this.api.suggestWorkout({
          focus: `a workout that burns about ${target} calories`,
          minutes: 60,
          equipment: this.btEquipment().trim() || undefined,
        }),
      );
      this.btResult.set(res);
      this.btAnnounce.set(
        `Suggested ${res.title} burning about ${res.estCalories} calories. Review and log it.`,
      );
    } catch {
      this.btAnnounce.set('AI is unavailable. Try the Library or Manual tabs.');
      this.snack.open('AI unavailable — use the Library or Manual tabs', 'OK', { duration: 4000 });
    } finally {
      this.btLoading.set(false);
    }
  }

  /** Log a burn-target suggestion (its title + estimated calories). */
  logBurnPlan(): void {
    const res = this.btResult();
    if (this.saving() || !res || res.estCalories <= 0) return;
    this.ref.close({
      date: this.data.date,
      name: res.title,
      caloriesBurned: res.estCalories,
    });
  }

  /** Whether we can let the server estimate (profile weight + a chosen library item + a duration). */
  readonly canEstimate = computed(
    () => this.data.hasWeight && !!this.selected() && (this.durationMin() ?? 0) > 0,
  );

  readonly filtered = computed<ExerciseLibraryDto[]>(() => {
    const q = this.query().trim().toLowerCase();
    const list = this.library();
    if (!q) return list;
    return list.filter(
      (e) => e.name.toLowerCase().includes(q) || e.category.toLowerCase().includes(q),
    );
  });

  readonly canSave = computed(() => {
    if (this.saving()) return false;
    if (this.mode() === 'manual')
      return this.mName().trim().length > 0 && (this.mCalories() ?? 0) > 0;
    if (this.mode() === 'workoutx') return !!this.wxSelected() && this.wxEffectiveCals() > 0;
    if (!this.selected()) return false;
    if (this.canEstimate() && !this.overrideCals()) return true; // server estimates
    return (this.effectiveCals() ?? 0) > 0; // need an explicit figure
  });

  /** The calorie figure that will be logged when we're NOT deferring to the server estimate. */
  readonly effectiveCals = computed<number | null>(() =>
    this.overrideCals() || !this.data.hasWeight ? this.manualCalsForLib() : null,
  );

  constructor() {
    void this.loadLibrary();

    // Debounced "My exercises" filter — re-fetches the caller's saved library on each keystroke.
    this.savedQueryStream
      .pipe(
        debounceTime(250),
        distinctUntilChanged(),
        switchMap((q) => {
          this.savedLoading.set(true);
          return this.api
            .savedExercises(q.trim() || undefined)
            .pipe(catchError(() => of<CustomExerciseDto[]>([])));
        }),
        takeUntilDestroyed(),
      )
      .subscribe((list) => {
        this.savedLoading.set(false);
        this.saved.set(list);
      });

    effect(() => this.savedQueryStream.next(this.savedQuery()));

    // Load the demo GIF whenever the WorkoutX selection changes. The effect owns the object URL's whole
    // lifetime: its onCleanup (which Angular also runs on destroy) unsubscribes the in-flight request and
    // revokes the URL, so there's exactly one revoke per created URL — no leaks, no double-revoke.
    effect((onCleanup) => {
      const sel = this.wxSelected();
      const requested = this.wxGifRequested();
      this.wxGifUrl.set(null);
      this.wxGifError.set(false);
      this.wxGifLoading.set(false);
      if (!sel) return;
      // Reduced-motion users only get the animated GIF after an explicit "Show animated demo" click;
      // the static instructions/meta remain the alternative until then.
      if (this.wxReducedMotion() && !requested) return;

      let url: string | null = null;
      this.wxGifLoading.set(true);
      const sub = this.api.workoutxGif(sel.id).subscribe({
        next: (blob) => {
          url = URL.createObjectURL(blob);
          this.wxGifUrl.set(url);
          this.wxGifLoading.set(false);
        },
        error: () => {
          this.wxGifLoading.set(false);
          this.wxGifError.set(true);
        },
      });
      onCleanup(() => {
        sub.unsubscribe();
        if (url) URL.revokeObjectURL(url);
      });
    });
  }

  async loadLibrary(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const goal = this.showAll() ? undefined : this.data.goal || undefined;
      this.library.set(await firstValueFrom(this.api.exerciseLibrary(goal)));
    } catch {
      this.error.set('Could not load the exercise library.');
      this.library.set([]);
    } finally {
      this.loading.set(false);
    }
  }

  toggleShowAll(on: boolean): void {
    this.showAll.set(on);
    void this.loadLibrary();
  }

  pick(e: ExerciseLibraryDto): void {
    this.selected.set(this.selected()?.id === e.id ? null : e);
  }

  setMode(m: 'library' | 'workoutx' | 'manual' | 'ai'): void {
    this.mode.set(m);
    this.error.set(null);
    // First time into the WorkoutX tab with nothing loaded yet → fetch the opening page.
    if (
      m === 'workoutx' &&
      this.wxResults().length === 0 &&
      !this.wxLoading() &&
      !this.wxUnavailable()
    ) {
      void this.searchWorkoutx();
    }
    // Lazy-load the saved "My exercises" library the first time the Manual tab is opened.
    if (m === 'manual' && !this.savedLoadedOnce) {
      this.savedLoadedOnce = true;
      this.savedQueryStream.next(this.savedQuery());
    }
  }

  /** Pick a saved "My exercises" entry → prefill the manual form (name + calories + duration). */
  pickSaved(ex: CustomExerciseDto): void {
    this.mName.set(ex.name);
    this.mCalories.set(ex.defaultCaloriesBurned ?? null);
    this.mDuration.set(ex.defaultDurationMin ?? null);
    this.clearAiEstimate();
    // Remember it was a saved pick so save() sends source="custom" (bump, not duplicate).
    this.pickedSavedId.set(ex.id);
    // The prefilled fields render below the list, so move focus there (and announce) — otherwise
    // keyboard/SR users don't realise the form filled. Focus after the DOM reflects the model update.
    this.pickAnnounce.set(`Loaded ${ex.name}`);
    afterNextRender(() => this.nameInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Remove a saved exercise from the caller's library (× on a "My exercises" row). Optimistic. */
  deleteSaved(ex: CustomExerciseDto, event: Event): void {
    event.stopPropagation();
    const prev = this.saved();
    this.saved.update((list) => list.filter((e) => e.id !== ex.id));
    if (this.pickedSavedId() === ex.id) this.pickedSavedId.set(null);
    this.api.deleteSavedExercise(ex.id).subscribe({ error: () => this.saved.set(prev) });
  }

  /** Run a fresh WorkoutX search (resets pagination + selection). */
  async searchWorkoutx(): Promise<void> {
    this.wxOffset.set(0);
    this.wxSelected.set(null);
    await this.fetchWorkoutx(false);
  }

  /** Append the next page of WorkoutX results. */
  async loadMoreWorkoutx(): Promise<void> {
    if (this.wxLoading() || !this.wxHasMore()) return;
    this.wxOffset.set(this.wxResults().length);
    await this.fetchWorkoutx(true);
  }

  private async fetchWorkoutx(append: boolean): Promise<void> {
    this.wxLoading.set(true);
    this.wxError.set(null);
    try {
      const res = await firstValueFrom(
        this.api.workoutxExercises({
          q: this.wxQuery().trim() || undefined,
          bodyPart: this.wxBodyPart() || undefined,
          target: this.wxTarget() || undefined,
          equipment: this.wxEquipment() || undefined,
          limit: AddExerciseDialog.WX_PAGE,
          offset: this.wxOffset(),
        }),
      );
      this.wxResults.set(append ? [...this.wxResults(), ...res.data] : res.data);
      this.wxTotal.set(res.total);
    } catch (e) {
      if (e instanceof HttpErrorResponse && e.status === 503) {
        this.wxUnavailable.set(true);
      } else {
        this.wxError.set('WorkoutX search failed. Try again.');
      }
      if (!append) {
        this.wxResults.set([]);
        this.wxTotal.set(0);
      }
    } finally {
      this.wxLoading.set(false);
    }
  }

  /**
   * Apply a body-part / equipment / target filter from its compact mat-select then re-search.
   * Each select is single-select and bound via [ngModel], emitting the newly chosen value ('' = the
   * "Any" default). mat-select is keyboard-operable and labelled, so this routes click + keyboard
   * identically. We bail when the value is unchanged to avoid a redundant search.
   */
  setWxFilter(kind: 'bodyPart' | 'target' | 'equipment', value: string | null | undefined): void {
    const sig =
      kind === 'bodyPart' ? this.wxBodyPart : kind === 'target' ? this.wxTarget : this.wxEquipment;
    const next = value ?? '';
    if (sig() === next) return; // already in sync with the listbox → avoid a redundant search
    sig.set(next);
    void this.searchWorkoutx();
  }

  /** Reduced-motion users: load the animated demo on explicit request. */
  showWxGif(): void {
    this.wxGifRequested.set(true);
  }

  pickWorkoutx(e: WorkoutXExerciseDto): void {
    this.wxGifRequested.set(false);
    this.wxSelected.set(this.wxSelected()?.id === e.id ? null : e);
  }

  /** Manual-name edit: drop any saved-pick association the moment the name diverges. */
  setManualName(name: string): void {
    this.mName.set(name);
    this.pickedSavedId.set(null);
  }

  save(): void {
    if (!this.canSave()) return;
    let body: AddExerciseRequest;
    if (this.mode() === 'manual') {
      // A re-picked "My exercises" entry bumps the saved row (source="custom"); a freshly-typed entry
      // sends NO source so the backend auto-saves it to the library.
      const fromSaved = this.pickedSavedId() !== null;
      body = {
        date: this.data.date,
        name: this.mName().trim(),
        durationMin: (this.mDuration() ?? 0) > 0 ? this.mDuration()! : undefined,
        caloriesBurned: this.mCalories() ?? 0,
        source: fromSaved ? 'custom' : undefined,
      };
    } else if (this.mode() === 'workoutx') {
      // WorkoutX exercises aren't in the local library, so log by name + a concrete calorie figure
      // (the live caloriesPerMinute estimate, or the manual override) — no server-side MET estimate.
      const e = this.wxSelected()!;
      body = {
        date: this.data.date,
        name: e.name,
        durationMin: this.wxDuration() ?? undefined,
        caloriesBurned: this.wxEffectiveCals(),
        // Tag the source so the backend never auto-saves a WorkoutX log to "My exercises".
        source: 'workoutx',
      };
    } else {
      const e = this.selected()!;
      const deferToServer = this.canEstimate() && !this.overrideCals();
      body = {
        date: this.data.date,
        exerciseId: e.id,
        name: e.name,
        durationMin: this.durationMin() ?? undefined,
        // Omit caloriesBurned so the server estimates from weight + duration + MET; otherwise send it.
        caloriesBurned: deferToServer ? undefined : (this.effectiveCals() ?? undefined),
        // Library logs aren't auto-saved (the ExerciseId already classifies it; source is belt-and-braces).
        source: 'library',
      };
    }
    this.ref.close(body);
  }

  cancel(): void {
    this.ref.close();
  }
}
