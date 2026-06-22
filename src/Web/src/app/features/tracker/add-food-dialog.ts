import { Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, debounceTime, distinctUntilChanged, switchMap, of, catchError, firstValueFrom, map, takeUntil } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { AddFoodRequest, CustomFoodDto, FoodSearchItemDto, ImageRequest, MealItemDto, Meal, PERM } from '../../core/models';
import { BarcodeScanner } from './barcode-scanner';
import { captureImage, pickImage, confirmPhotoNotice } from './ai-image';

/** What the dialog opens with: the active day + which meal section the user tapped "Add food" on. */
export interface AddFoodData {
  date: string;
  meal: Meal;
  /**
   * Optional initial name-search term. When set, the dialog opens with the search box pre-seeded (e.g.
   * from an AI "What should I eat?" suggestion) — a prefill the user edits/confirms; nothing auto-logs.
   */
  prefillQuery?: string;
}

/**
 * What the dialog resolves with: a SINGLE food log, OR an ARRAY of logs — the multi-item flows
 * (photo meal / "describe your meal") let the user commit several foods to the day at once. The caller
 * handles both shapes; `undefined` === cancelled.
 */
export type AddFoodResult = AddFoodRequest | AddFoodRequest[];

/**
 * One reviewable row in a multi-item parse (photo-meal / describe-a-meal). Each is independently
 * editable + toggleable before the user commits the batch — an AI prefill, never auto-logged. Macros
 * are the model's per-item estimate.
 */
interface ReviewItem {
  description: string;
  calories: number;
  proteinG: number;
  carbG: number;
  fatG: number;
  include: boolean;
}

/** Which sub-panel of the add-food flow is showing. */
type Mode = 'search' | 'scan' | 'saved' | 'describe' | 'recipe';

/** An upper sanity bound on a single food's quantity — blocks absurd typos (e.g. 99999 servings). */
const MAX_QUANTITY = 9999;

/** Coerce a possibly-null / NaN / non-finite numeric input to a finite number (0 when unusable). */
function safeNum(n: number | null | undefined): number {
  return typeof n === 'number' && Number.isFinite(n) ? n : 0;
}

const MEALS: { value: Meal; label: string }[] = [
  { value: 'breakfast', label: 'Breakfast' },
  { value: 'lunch', label: 'Lunch' },
  { value: 'dinner', label: 'Dinner' },
  { value: 'snack', label: 'Snacks' },
];

/**
 * Add-food dialog. Three ways in: a debounced USDA name search, a live barcode scan (native
 * BarcodeDetector or a lazily-loaded @zxing fallback) that prefills from a UPC lookup, and a manual
 * entry escape hatch (always available; the only path when USDA is unconfigured → 503). Once a food is
 * picked the user sets a quantity (servings or grams, scaled by `basis`) and a target meal; the dialog
 * snapshots the scaled calories/macros into an {@link AddFoodRequest} and resolves with it.
 */
@Component({
  selector: 'app-add-food-dialog',
  imports: [
    FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatButtonModule,
    MatButtonToggleModule, MatCheckboxModule, MatIconModule, MatProgressBarModule,
    MatProgressSpinnerModule, BarcodeScanner,
  ],
  templateUrl: './add-food-dialog.html',
  styleUrl: './add-food-dialog.scss',
})
export class AddFoodDialog {
  private api = inject(Api);
  private ref = inject(MatDialogRef<AddFoodDialog, AddFoodResult>);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);
  readonly data = inject<AddFoodData>(MAT_DIALOG_DATA);

  /** GATE: every AI affordance (photo, label scan, describe, recipe, feedback) is hidden unless held. */
  readonly canUseAi = this.auth.hasPermission(PERM.trackerAi);
  /** Multimodal (image/PDF) AI — meal photo + label scan — is a SEPARATE permission from text AI.
   *  Gate the camera/attach/scan buttons on this so we never show a vision action the server will 403. */
  readonly canUseVision = this.auth.hasPermission(PERM.aiVision);

  readonly meals = MEALS;
  readonly mode = signal<Mode>('search');

  // ---- search ----
  readonly query = signal('');
  readonly searching = signal(false);
  readonly results = signal<FoodSearchItemDto[]>([]);
  /** True once a search came back 503 — USDA isn't configured; steer to manual entry. */
  readonly searchUnavailable = signal(false);
  readonly searchError = signal<string | null>(null);
  private readonly queryStream = new Subject<string>();

  /**
   * The barcode (scanned or typed) whose lookup returned NO product, or null. Drives a distinct
   * "not found" notice in the scan panel — explicitly different from the idle/scanning state and from
   * an empty name-search — with affordances to switch to a name search or to manual entry.
   */
  readonly barcodeNotFound = signal<string | null>(null);
  /** Monotonic id of the latest barcode lookup; responses with a stale id are ignored (latest-wins). */
  private barcodeToken = 0;

  // ---- "My foods" (per-user saved library) ----
  readonly savedQuery = signal('');
  readonly savedLoading = signal(false);
  readonly saved = signal<CustomFoodDto[]>([]);
  private readonly savedQueryStream = new Subject<string>();
  private savedLoadedOnce = false;

  // ---- selection / quantity ----
  readonly selected = signal<FoodSearchItemDto | null>(null);
  /**
   * Provider the current selection came from ("usda" | "fatsecret" | "custom"), or null for a manual
   * entry. Carried into the log request so the backend knows whether to auto-save / bump a saved food.
   */
  readonly selectedSource = signal<string | null>(null);
  /** "manual" === a hand-entered food (no FDC id); the form fields below drive the snapshot. */
  readonly manual = signal(false);
  readonly meal = signal<Meal>(this.data.meal);
  readonly quantity = signal(1);

  /** The original serving text of a picked saved food (preserved verbatim at quantity 1). */
  readonly pickedServingDesc = signal<string | undefined>(undefined);

  // ---- manual-entry fields ----
  readonly mDesc = signal('');
  readonly mBrand = signal('');
  readonly mCalories = signal<number | null>(null);
  readonly mProtein = signal<number | null>(null);
  readonly mCarb = signal<number | null>(null);
  readonly mFat = signal<number | null>(null);

  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  // ---- AI macro estimate (Gemini-backed; optional, gracefully degrades) ----
  /** True while the estimate-macros call is in flight (drives the spinner + disables the button). */
  readonly aiLoading = signal(false);
  /** Set once an AI estimate has prefilled the macro fields → renders the "AI estimate" chip. */
  readonly aiEstimated = signal(false);
  /** The model's optional short note (an assumption it made), shown beside the chip. */
  readonly aiNote = signal<string | null>(null);
  /** Polite sr-only announcement of the AI result (or its unavailability). */
  readonly aiAnnounce = signal('');

  /** Can we ask the AI? A description is required (quantity is optional free text). */
  readonly canEstimateAi = computed(() => !this.aiLoading() && this.mDesc().trim().length > 0);

  // ---- Photo meal + label scan (multimodal; gated by trackerAi) ----
  /** True while a photo is being analysed (photo-meal OR read-label) — drives a spinner + disables buttons. */
  readonly photoLoading = signal(false);
  /** Polite sr-only announcement of a photo result (or its unavailability). */
  readonly photoAnnounce = signal('');

  // ---- "Describe your meal" multi-item parse (gated by trackerAi) ----
  /** Free-text meal to split into items ("Big Mac, fries, and a Coke"). */
  readonly describeText = signal('');
  /** True while the parse-meal call is in flight. */
  readonly describeLoading = signal(false);
  readonly canParseMeal = computed(() => !this.describeLoading() && this.describeText().trim().length > 0);

  /**
   * The reviewable, per-item list from a photo-meal OR parse-meal call. Each row is editable +
   * toggleable; the user commits the checked rows as a batch. Empty === no parse yet (or all removed).
   */
  readonly reviewItems = signal<ReviewItem[]>([]);
  /** Where the current review list came from, for the heading + sr announcement copy. */
  readonly reviewSource = signal<'photo' | 'text' | null>(null);
  /** Polite sr-only announcement of a multi-item parse result (or its unavailability). */
  readonly reviewAnnounce = signal('');
  /** How many review rows are currently checked (drives the "Add N items" button + its disabled state). */
  readonly reviewSelectedCount = computed(() => this.reviewItems().filter(i => i.include).length);

  // ---- Recipe → per-serving macros (gated by trackerAi) ----
  /** Free-text ingredient list for the recipe estimator. */
  readonly recipeText = signal('');
  /** Number of servings the recipe yields (the AI returns PER-serving macros). */
  readonly recipeServings = signal(1);
  readonly recipeLoading = signal(false);
  readonly canRecipeMacros = computed(() =>
    !this.recipeLoading() && this.recipeText().trim().length > 0 && this.recipeServings() > 0);

  // ---- Meal feedback ("Is this good for my goal? ✨") on the manual description ----
  readonly feedbackLoading = signal(false);
  /** The model's verdict + swap suggestions for the manual description, or null. Read-only helper text. */
  readonly feedback = signal<{ verdict: string; goodForGoal: boolean; swaps: string[] } | null>(null);
  readonly feedbackAnnounce = signal('');
  readonly canGetFeedback = computed(() => !this.feedbackLoading() && this.mDesc().trim().length > 0);

  /** Unit hint for the quantity field, driven by the selected food's basis (manual = servings). */
  readonly quantityUnit = computed(() =>
    !this.manual() && this.selected()?.basis === 'per100g' ? 'grams' : 'servings');

  /**
   * The scaled calories/macros for the current selection/manual entry + quantity. A picked food's
   * perServing scales by the serving count, per100g by grams ÷ 100. A MANUAL entry's calories/macros
   * are PER ONE serving and scale by the quantity — exactly like the picked-food path — so the logged
   * total matches what the user sees here.
   */
  readonly scaled = computed(() => {
    const q = this.quantity();
    if (this.manual()) {
      if (!(q > 0)) return { calories: 0, proteinG: 0, carbG: 0, fatG: 0 };
      // Floor each macro at 0 so a typed/pasted negative never renders a negative preview (and matches
      // the server's Math.max(0, …) flooring at log time).
      const round = (n: number | null) => Math.max(0, Math.round(safeNum(n) * q * 10) / 10);
      return {
        calories: Math.max(0, Math.round(safeNum(this.mCalories()) * q)),
        proteinG: round(this.mProtein()),
        carbG: round(this.mCarb()),
        fatG: round(this.mFat()),
      };
    }
    const f = this.selected();
    if (!f || !(q > 0)) return { calories: 0, proteinG: 0, carbG: 0, fatG: 0 };
    const factor = f.basis === 'per100g' ? q / 100 : q;
    const round = (n: number) => Math.max(0, Math.round(safeNum(n) * factor * 10) / 10);
    return {
      calories: Math.max(0, Math.round(safeNum(f.calories) * factor)),
      proteinG: round(f.proteinG),
      carbG: round(f.carbG),
      fatG: round(f.fatG),
    };
  });

  /** A short human serving description for the entry ("2 servings" / "150 grams" / "x2 · 1 bowl"). */
  readonly servingDesc = computed(() => {
    if (this.manual()) {
      const q = this.quantity();
      return `${q} ${q === 1 ? 'serving' : 'servings'}`;
    }
    const f = this.selected();
    if (!f) return undefined;
    const q = this.quantity();
    // A re-picked saved food carries its ORIGINAL unit text. At q=1 use it verbatim; at any other
    // quantity prefix the multiplier so a non-"servings" unit (e.g. "150 grams", "1 bowl") isn't
    // silently relabelled "N servings" while the macros are scaled by N.
    const orig = this.pickedServingDesc();
    if (orig) return q === 1 ? orig : `x${q} · ${orig}`;
    const unit = f.basis === 'per100g' ? 'g' : (q === 1 ? 'serving' : 'servings');
    const sizeNote = f.servingSize && f.servingUnit ? ` (${f.servingSize}${f.servingUnit})` : '';
    return `${q} ${unit}${f.basis === 'per100g' ? '' : sizeNote}`;
  });

  /** A finite, in-range quantity (the bound number can be null/NaN mid-typing). */
  private readonly validQuantity = computed(() => {
    const q = this.quantity();
    return Number.isFinite(q) && q > 0 && q <= MAX_QUANTITY;
  });

  readonly canSave = computed(() => {
    if (this.saving()) return false;
    if (!this.validQuantity()) return false;
    if (this.manual()) {
      const cal = this.mCalories();
      return this.mDesc().trim().length > 0 && cal != null && Number.isFinite(cal) && cal >= 0;
    }
    return !!this.selected();
  });

  constructor() {
    // Debounced USDA name search; a 503 flips to the manual-entry steer.
    // Trim BEFORE distinctUntilChanged so trailing-whitespace edits don't re-fire the same term.
    this.queryStream.pipe(
      map(q => q.trim()),
      debounceTime(300),
      distinctUntilChanged(),
      switchMap(term => {
        if (term.length < 2) { this.searching.set(false); return of<FoodSearchItemDto[] | null>([]); }
        this.searching.set(true);
        // Each fresh attempt clears prior transient state — a 503 latch must not outlive a recovery.
        this.searchError.set(null);
        this.searchUnavailable.set(false);
        return this.api.searchFoods({ q: term }).pipe(
          catchError((e: HttpErrorResponse) => {
            this.searching.set(false);
            if (e.status === 503) { this.searchUnavailable.set(true); }
            else { this.searchError.set('Food search failed. Try again, or enter the food manually.'); }
            return of<FoodSearchItemDto[] | null>(null);
          }),
        );
      }),
      takeUntilDestroyed(),
    ).subscribe(list => {
      this.searching.set(false);
      if (list) { this.results.set(list); }
    });

    // Keep the query stream fed from the signal.
    effect(() => this.queryStream.next(this.query()));

    // Debounced "My foods" filter — re-fetches the caller's saved library on each keystroke.
    this.savedQueryStream.pipe(
      debounceTime(250),
      distinctUntilChanged(),
      switchMap(q => {
        this.savedLoading.set(true);
        // recent:true also surfaces recently-logged foods (read-only) deduped against the saved list.
        return this.api.savedFoods(q.trim() || undefined, true).pipe(
          catchError(() => of<CustomFoodDto[]>([])),
        );
      }),
      takeUntilDestroyed(),
    ).subscribe(list => {
      this.savedLoading.set(false);
      this.saved.set(list);
    });

    effect(() => this.savedQueryStream.next(this.savedQuery()));

    // Pre-seed the name search from an AI food suggestion (editable; never auto-logs).
    const seed = this.data.prefillQuery?.trim();
    if (seed) this.query.set(seed);
  }

  setMode(m: Mode): void {
    this.mode.set(m);
    this.saveError.set(null);
    this.barcodeNotFound.set(null);
    // Leaving search clears its transient notices/results so returning to it starts clean and a
    // recovered provider isn't hidden behind a stale 503 latch from an earlier keystroke.
    this.searchError.set(null);
    this.searchUnavailable.set(false);
    if (m !== 'search') this.results.set([]);
    // Lazy-load the saved library the first time the "My foods" tab is opened.
    if (m === 'saved' && !this.savedLoadedOnce) {
      this.savedLoadedOnce = true;
      this.savedQueryStream.next(this.savedQuery());
    }
  }

  /** Pick a search/scan result → move to the quantity step, carrying its provider source. */
  pick(food: FoodSearchItemDto): void {
    this.manual.set(false);
    this.pickedServingDesc.set(undefined);
    this.selectedSource.set(food.source || null);
    this.selected.set(food);
    // Per-100g default quantity = the provider serving size, clamped to a sane minimum so bad/zero
    // provider data can't seed a 0- or sub-gram quantity (which yields an all-zero macro card).
    this.quantity.set(food.basis === 'per100g' ? Math.max(1, food.servingSize ?? 100) : 1);
  }

  /**
   * Pick a "My foods" entry → quantity step. A genuinely SAVED food (id > 0) carries source="custom"
   * so the backend bumps its use count. A RECENT food (id 0, isRecent) is NOT yet a saved row, so it
   * carries NO source — re-logging it then auto-saves it to "My foods" like any manual log.
   */
  pickSaved(food: CustomFoodDto): void {
    this.manual.set(false);
    this.pickedServingDesc.set(food.servingDesc || undefined);
    const isSaved = !food.isRecent && food.id > 0;
    this.selectedSource.set(isSaved ? 'custom' : null);
    this.selected.set({
      fdcId: 0,
      description: food.description,
      brand: food.brand,
      calories: food.calories,
      proteinG: food.proteinG,
      carbG: food.carbG,
      fatG: food.fatG,
      basis: 'perServing',
      // The selection's own `source` is informational only; save() derives the logged source from
      // selectedSource() (set to null above for a recent food → auto-saved on re-log).
      source: 'custom',
      sourceId: isSaved ? String(food.id) : undefined,
    });
    this.quantity.set(1);
  }

  /** Remove a saved food from the caller's library (× on a "My foods" row). On failure, restore the
   *  row directly (a stream re-emit would be swallowed by distinctUntilChanged when the query is
   *  unchanged) and tell the user, so the UI never silently diverges from the server. */
  deleteSaved(food: CustomFoodDto, ev: Event): void {
    ev.stopPropagation();
    this.saved.update(list => list.filter(f => f.id !== food.id));
    this.api.deleteSavedFood(food.id).subscribe({
      error: () => {
        this.api.savedFoods(this.savedQuery().trim() || undefined, true).subscribe({
          next: list => this.saved.set(list),
          // Even the refetch failed — re-insert the optimistically-removed row so it doesn't vanish.
          error: () => this.saved.update(list => list.some(f => f.id === food.id) ? list : [food, ...list]),
        });
        this.snack.open('Could not remove that food', 'OK', { duration: 4000 });
      },
    });
  }

  /** Start a fresh manual entry (used by the "enter manually" affordances). */
  startManual(): void {
    this.manual.set(true);
    this.selected.set(null);
    this.selectedSource.set(null);
    this.pickedServingDesc.set(undefined);
    this.barcodeNotFound.set(null);
    // Clear stale search transients so a prior 503/error notice can't re-show behind the manual panel.
    this.searchError.set(null);
    this.searchUnavailable.set(false);
    // Abandon any pending AI review list — otherwise its top-level @if would hide the manual fields
    // this call is about to prefill (label/recipe paths), stranding the user on the review block.
    this.reviewItems.set([]);
    this.reviewSource.set(null);
    this.quantity.set(1);
    this.clearAiEstimate();
    this.mode.set('search');
  }

  /** Clear the "AI estimate" chip + note (called when the user edits the macro fields themselves). */
  clearAiEstimate(): void {
    this.aiEstimated.set(false);
    this.aiNote.set(null);
  }

  /** Editing the description invalidates a stale meal-feedback verdict AND any AI macro estimate —
   *  the prefilled numbers were for a DIFFERENT food, so drop the "AI estimate" chip so the user
   *  doesn't log e.g. "apple pie" with "banana"'s macros still badged as an estimate. */
  onDescChange(v: string): void {
    if (v !== this.mDesc()) {
      if (this.feedback()) this.feedback.set(null);
      if (this.aiEstimated()) this.clearAiEstimate();
    }
    this.mDesc.set(v);
  }

  /**
   * 📷 Snap a meal photo (rear camera on mobile) → photo-meal review. Thin wrapper over
   * {@link runPhotoMeal} with the camera-first {@link captureImage} source.
   */
  async photoMeal(): Promise<void> {
    await this.runPhotoMeal(captureImage);
  }

  /**
   * 🖼️ Attach an existing image from the gallery / files → photo-meal review. Sibling of {@link photoMeal}
   * that uses the no-`capture` {@link pickImage} source so mobile offers the photo library, not just the
   * camera. Same downscale + review flow; no backend change.
   */
  async attachImage(): Promise<void> {
    await this.runPhotoMeal(pickImage);
  }

  /**
   * Shared meal-photo flow for both {@link photoMeal} (camera) and {@link attachImage} (gallery/files):
   * show the one-time Google notice on first use, obtain the image via `source`, then (on >=1 item) route
   * to the multi-item review list. A single item still lands in review so the user confirms before
   * committing. A 503/error degrades gracefully: snackbar steer, fields stay usable. The image is only
   * read to identify the food — it is never stored.
   */
  private async runPhotoMeal(source: () => Promise<ImageRequest | null>): Promise<void> {
    if (!this.canUseVision || this.photoLoading()) return;
    if (!(await confirmPhotoNotice())) return; // declined the privacy notice → abort, nothing sent.
    let image;
    try {
      image = await source();
    } catch {
      this.snack.open('Could not read that image — try another photo', 'OK', { duration: 4000 });
      return;
    }
    if (!image) return; // picker cancelled.
    this.photoLoading.set(true);
    this.photoAnnounce.set('Analysing your meal photo with AI…');
    try {
      const res = await firstValueFrom(this.api.photoMeal(image));
      const items = res.items ?? [];
      if (items.length === 0) {
        this.photoAnnounce.set('No foods found in that photo. Add the food manually.');
        this.snack.open('No foods found in that photo — add it manually', 'OK', { duration: 4000 });
        return;
      }
      this.showReview(items, 'photo');
    } catch {
      this.photoAnnounce.set('Photo analysis unavailable. Add the food manually.');
      this.snack.open('AI photo unavailable — add it manually', 'OK', { duration: 4000 });
    } finally {
      this.photoLoading.set(false);
    }
  }

  /**
   * 📷 Scan a nutrition label → read-label → PREFILL the manual food fields (a sibling to the barcode
   * scanner). Shows the one-time Google notice on first use. A 503/error degrades gracefully.
   */
  async scanLabel(): Promise<void> {
    if (!this.canUseVision || this.photoLoading()) return;
    if (!(await confirmPhotoNotice())) return;
    let image;
    try {
      image = await captureImage();
    } catch {
      this.snack.open('Could not read that image — try another photo', 'OK', { duration: 4000 });
      return;
    }
    if (!image) return;
    this.photoLoading.set(true);
    this.photoAnnounce.set('Reading the nutrition label with AI…');
    try {
      const res = await firstValueFrom(this.api.readLabel(image));
      // Prefill the editable manual fields — the label read is per stated serving; quantity scales it.
      this.startManual();
      this.mDesc.set(res.description);
      this.mCalories.set(res.calories);
      this.mProtein.set(res.proteinG);
      this.mCarb.set(res.carbsG);
      this.mFat.set(res.fatG);
      this.aiNote.set(res.servingSize ? `Label serving: ${res.servingSize}` : null);
      this.aiEstimated.set(true);
      this.photoAnnounce.set(
        `Label read: ${res.calories} calories, ${res.proteinG} grams protein, ${res.carbsG} grams carbs, ` +
        `${res.fatG} grams fat.` + (res.servingSize ? ` Serving: ${res.servingSize}.` : ''));
    } catch {
      this.photoAnnounce.set('Label scan unavailable. Enter the values manually.');
      this.snack.open('AI label scan unavailable — enter manually', 'OK', { duration: 4000 });
    } finally {
      this.photoLoading.set(false);
    }
  }

  /**
   * Parse the free-text "describe your meal" box into a reviewable multi-item list. A 503/error degrades
   * gracefully: snackbar steer + announcement; the text box stays usable.
   */
  async parseMeal(): Promise<void> {
    if (!this.canParseMeal()) return;
    this.describeLoading.set(true);
    this.reviewAnnounce.set('Splitting your meal into items with AI…');
    try {
      const res = await firstValueFrom(this.api.parseMeal({ text: this.describeText().trim() }));
      const items = res.items ?? [];
      if (items.length === 0) {
        this.reviewItems.set([]);
        this.reviewSource.set(null);
        this.reviewAnnounce.set('No items found. Add the food manually.');
        this.snack.open('No items found — add the food manually', 'OK', { duration: 4000 });
        return;
      }
      this.showReview(items, 'text');
    } catch {
      this.reviewAnnounce.set('Meal parsing unavailable. Add the food manually.');
      this.snack.open('AI meal parsing unavailable — add it manually', 'OK', { duration: 4000 });
    } finally {
      this.describeLoading.set(false);
    }
  }

  /** Map AI {@link MealItemDto}s into the editable review list (all checked) + announce the result. */
  private showReview(items: MealItemDto[], from: 'photo' | 'text'): void {
    this.reviewItems.set(items.map(i => ({
      description: i.description,
      calories: i.calories,
      proteinG: i.proteinG,
      carbG: i.carbsG,
      fatG: i.fatG,
      include: true,
    })));
    this.reviewSource.set(from);
    this.reviewAnnounce.set(
      `AI found ${items.length} ${items.length === 1 ? 'item' : 'items'}. Review and edit, then add to your day.`);
  }

  /** Patch one field on a review row (keeps the array reference fresh for change detection). Numeric
   *  fields are coerced through safeNum so a blanked/NaN input becomes 0 — the visible row and the
   *  logged snapshot then agree, and no NaN can reach the batch. */
  updateReviewItem(index: number, patch: Partial<ReviewItem>): void {
    const clean: Partial<ReviewItem> = { ...patch };
    for (const k of ['calories', 'proteinG', 'carbG', 'fatG'] as const) {
      if (k in clean) clean[k] = Math.max(0, safeNum(clean[k] as number | null));
    }
    this.reviewItems.update(list => list.map((it, i) => (i === index ? { ...it, ...clean } : it)));
  }

  /** Toggle whether a review row is included in the batch add. */
  toggleReviewItem(index: number): void {
    this.reviewItems.update(list => list.map((it, i) => (i === index ? { ...it, include: !it.include } : it)));
  }

  /** Drop a review row entirely. */
  removeReviewItem(index: number): void {
    this.reviewItems.update(list => list.filter((_, i) => i !== index));
    if (this.reviewItems().length === 0) this.discardReview();
  }

  /** Abandon the current review list and return to the chooser. */
  discardReview(): void {
    this.reviewItems.set([]);
    this.reviewSource.set(null);
    this.reviewAnnounce.set('');
  }

  /**
   * Commit the checked review rows as a BATCH of food logs for the active meal. Each item's macros are
   * the AI per-item estimate (quantity 1). Resolves the dialog with the array; the caller logs them all.
   */
  addReviewItems(): void {
    if (this.saving()) return; // in-flight guard against a double-tap before the overlay tears down.
    const meal = this.meal();
    const reqs: AddFoodRequest[] = this.reviewItems()
      .filter(i => i.include && i.description.trim().length > 0)
      .map(i => ({
        date: this.data.date,
        meal,
        description: i.description.trim(),
        quantity: 1,
        servingDesc: '1 serving',
        // Coerce every macro through safeNum so a blanked/NaN field logs 0, never NaN, into the snapshot.
        calories: Math.max(0, Math.round(safeNum(i.calories))),
        proteinG: Math.max(0, safeNum(i.proteinG)),
        carbG: Math.max(0, safeNum(i.carbG)),
        fatG: Math.max(0, safeNum(i.fatG)),
        // No source → each is auto-saved to "My foods" like any manual log.
      }));
    if (reqs.length === 0) return;
    this.saving.set(true);
    this.ref.close(reqs);
  }

  /**
   * Recipe mode: estimate PER-serving macros from a free-text ingredient list + servings, then PREFILL
   * the manual fields (quantity = 1 serving). A 503/error degrades gracefully.
   */
  async recipeMacros(): Promise<void> {
    if (!this.canRecipeMacros()) return;
    this.recipeLoading.set(true);
    this.aiAnnounce.set('Estimating per-serving macros with AI…');
    try {
      const res = await firstValueFrom(this.api.recipeMacros({
        recipe: this.recipeText().trim(),
        servings: this.recipeServings(),
      }));
      const m = res.perServing;
      this.startManual();
      this.mDesc.set(this.recipeFirstLine());
      this.mCalories.set(m.calories);
      this.mProtein.set(m.proteinG);
      this.mCarb.set(m.carbsG);
      this.mFat.set(m.fatG);
      this.aiNote.set(`Per serving (recipe makes ${this.recipeServings()}).`);
      this.aiEstimated.set(true);
      this.aiAnnounce.set(
        `Per-serving estimate: ${m.calories} calories, ${m.proteinG} grams protein, ${m.carbsG} grams carbs, ` +
        `${m.fatG} grams fat.`);
    } catch {
      this.aiAnnounce.set('Recipe estimate unavailable. Enter the values manually.');
      this.snack.open('AI recipe estimate unavailable — enter manually', 'OK', { duration: 4000 });
    } finally {
      this.recipeLoading.set(false);
    }
  }

  /** A short description seed for a recipe prefill: the first non-empty line, capped. */
  private recipeFirstLine(): string {
    const first = this.recipeText().split('\n').map(l => l.trim()).find(l => l.length > 0) ?? 'Recipe';
    return first.length > 60 ? first.slice(0, 60) : first;
  }

  /**
   * "Is this good for my goal? ✨" — fetch a read-only verdict + swap suggestions for the manual
   * description. A 503/error degrades gracefully: snackbar steer; the form stays usable.
   */
  async getMealFeedback(): Promise<void> {
    if (!this.canGetFeedback()) return;
    this.feedbackLoading.set(true);
    this.feedback.set(null);
    this.feedbackAnnounce.set('Checking this against your goal with AI…');
    try {
      const res = await firstValueFrom(this.api.mealFeedback({ description: this.mDesc().trim() }));
      this.feedback.set({ verdict: res.verdict, goodForGoal: res.goodForGoal, swaps: res.swaps ?? [] });
      this.feedbackAnnounce.set(
        `${res.goodForGoal ? 'Good for your goal.' : 'Could be better for your goal.'} ${res.verdict}` +
        (res.swaps?.length ? ` Swaps: ${res.swaps.join(', ')}.` : ''));
    } catch {
      this.feedbackAnnounce.set('Meal feedback unavailable right now.');
      this.snack.open('AI meal feedback unavailable', 'OK', { duration: 4000 });
    } finally {
      this.feedbackLoading.set(false);
    }
  }

  /**
   * Ask Gemini to estimate calories + macros for the typed description (+ optional quantity), then
   * PREFILL the editable fields. The estimate is per-one-serving (quantity stays the form's own scaler),
   * a suggestion the user can adjust. A 503/unavailable leaves the fields editable and steers to manual.
   */
  async estimateWithAi(): Promise<void> {
    if (!this.canEstimateAi()) return;
    this.aiLoading.set(true);
    this.aiAnnounce.set('Estimating nutrition with AI…');
    try {
      const res = await firstValueFrom(this.api.estimateMacros({
        description: this.mDesc().trim(),
        // Always request ONE serving: the macro fields are PER-serving and the form's `quantity` signal
        // already scales them (see the `scaled` computed). Passing the typed serving count here would make
        // the AI return macros for N servings, which then get scaled by N again → double-counted.
        quantity: '1 serving',
      }));
      // Prefill the editable per-serving fields — never silently authoritative; the user can adjust.
      this.mCalories.set(res.calories);
      this.mProtein.set(res.proteinG);
      this.mCarb.set(res.carbsG);
      this.mFat.set(res.fatG);
      this.aiNote.set(res.note ?? null);
      this.aiEstimated.set(true);
      this.aiAnnounce.set(
        `AI estimate: ${res.calories} calories, ${res.proteinG} grams protein, ` +
        `${res.carbsG} grams carbs, ${res.fatG} grams fat.` + (res.note ? ` ${res.note}` : ''));
    } catch {
      // 503 (unconfigured) or any failure → one consistent degraded path; fields stay editable. If a
      // PRIOR estimate's numbers are still in the fields, clear them too — otherwise dropping the chip
      // while keeping now-mismatched macros could be saved as if hand-entered for the new description.
      if (this.aiEstimated()) {
        this.mCalories.set(null);
        this.mProtein.set(null);
        this.mCarb.set(null);
        this.mFat.set(null);
      }
      this.clearAiEstimate();
      this.aiAnnounce.set('AI estimate unavailable. Enter the values manually.');
      this.snack.open('AI estimate unavailable — enter manually', 'OK', { duration: 4000 });
    } finally {
      this.aiLoading.set(false);
    }
  }

  /** Drop the current selection and go back to searching. */
  clearSelection(): void {
    this.selected.set(null);
    this.selectedSource.set(null);
    this.pickedServingDesc.set(undefined);
    this.manual.set(false);
  }

  /**
   * A scanned/typed barcode → look it up. On a hit, prefill the quantity step. On NO match (neither
   * provider matched, not a 503), stay on the scan panel and raise a distinct {@link barcodeNotFound}
   * notice with affordances to switch to name search or manual entry — never silently show nothing.
   */
  onBarcode(code: string): void {
    if (this.searching()) return; // in-flight guard: a double-emit / fast re-tap can't stack lookups.
    // Each lookup carries a monotonic token; a stale response (e.g. the scanner emitting twice, or a
    // slow earlier lookup) is ignored so it can't clobber the newest result's state.
    const token = ++this.barcodeToken;
    this.searching.set(true);
    this.searchError.set(null);
    this.barcodeNotFound.set(null);
    // Fresh attempt — never let an earlier 503 latch route this lookup to the unconfigured steer.
    this.searchUnavailable.set(false);
    this.api.searchFoods({ barcode: code }).pipe(
      catchError((e: HttpErrorResponse) => {
        if (e.status === 503) { this.searchUnavailable.set(true); }
        // Any other failure (network, 500, timeout) is a lookup error, not a "no such product".
        else { this.searchError.set('Barcode lookup failed. Try again, or enter the food manually.'); }
        return of<FoodSearchItemDto[] | null>(null);
      }),
    ).subscribe(list => {
      if (token !== this.barcodeToken) return; // a newer lookup superseded this one.
      this.searching.set(false);
      if (list && list.length > 0) {
        this.mode.set('search');
        this.pick(list[0]);
      } else if (this.searchUnavailable()) {
        // Lookup unavailable (503) — fall back to the existing "search isn't configured" steer.
        this.mode.set('search');
      } else if (this.searchError()) {
        // The lookup failed (handled above) — leave the error notice, don't claim the product is missing.
      } else {
        // A genuine empty barcode lookup: surface the explicit not-found notice (stays on scan).
        this.barcodeNotFound.set(code);
      }
    });
  }

  /** Switch from the barcode not-found notice to a fresh name search. */
  searchByName(): void {
    this.query.set('');
    // setMode() clears barcodeNotFound + the search transients (error / 503 latch / stale results).
    this.setMode('search');
  }

  /**
   * Re-run the CURRENT name search after a transient error. distinctUntilChanged on the query stream
   * would swallow a re-type of the identical term, so the "Try again" affordance runs the lookup
   * directly (latest-wins via the barcode-style token isn't needed here — switchMap-less, but a fresh
   * keystroke would supersede it through the stream anyway).
   */
  retrySearch(): void {
    const term = this.query().trim();
    if (term.length < 2 || this.searching()) return;
    this.searching.set(true);
    this.searchError.set(null);
    this.searchUnavailable.set(false);
    this.api.searchFoods({ q: term }).pipe(
      catchError((e: HttpErrorResponse) => {
        if (e.status === 503) { this.searchUnavailable.set(true); }
        else { this.searchError.set('Food search failed. Try again, or enter the food manually.'); }
        return of<FoodSearchItemDto[] | null>(null);
      }),
      // Latest-wins: a fresh keystroke (which pushes the query stream) cancels this retry so a slow
      // retry response can't land after — and clobber — a newer search result.
      takeUntil(this.queryStream),
    ).subscribe(list => {
      this.searching.set(false);
      if (list) this.results.set(list);
    });
  }

  basisLabel(f: FoodSearchItemDto): string {
    return f.basis === 'per100g' ? 'per 100 g' : 'per serving';
  }

  save(): void {
    if (!this.canSave()) return;
    this.saving.set(true); // first tap latches; canSave() now returns false so a double-tap no-ops.
    const s = this.scaled();
    const f = this.selected();
    const src = this.manual() ? null : this.selectedSource();
    const body: AddFoodRequest = {
      date: this.data.date,
      meal: this.meal(),
      // Only USDA hits carry a real FDC id; FatSecret/custom/manual logs leave it unset.
      fdcId: (!this.manual() && src === 'usda') ? f?.fdcId : undefined,
      description: this.manual() ? this.mDesc().trim() : (f?.description ?? ''),
      brand: this.manual() ? (this.mBrand().trim() || undefined) : f?.brand,
      quantity: this.quantity(),
      servingDesc: this.servingDesc(),
      calories: s.calories,
      proteinG: s.proteinG,
      carbG: s.carbG,
      fatG: s.fatG,
      // Manual entries send NO source so the backend auto-saves them to "My foods".
      source: src ?? undefined,
    };
    // Resolve with the request; the page persists it through the store and refreshes the day.
    this.ref.close(body);
  }

  cancel(): void {
    this.ref.close();
  }
}
