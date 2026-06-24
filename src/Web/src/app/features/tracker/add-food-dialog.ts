import {
  Component,
  OnDestroy,
  computed,
  inject,
  signal,
  ChangeDetectionStrategy,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { AddFoodRequest, ImageRequest, ParsedFoodItemDto, Meal, PERM } from '../../core/models';
import { captureImage, pickImage, confirmPhotoNotice } from './ai-image';
import {
  VoiceRecording,
  confirmVoiceNotice,
  recordTranscript,
  speechSupported,
} from './voice-capture';

/** What the dialog opens with: the active day + which meal section the user tapped "Add food" on. */
export interface AddFoodData {
  date: string;
  meal: Meal;
  /**
   * Optional initial text. When set, the dialog opens in Describe mode with the box pre-seeded (e.g. from
   * an AI "What should I eat?" suggestion) — a prefill the user edits/confirms; nothing auto-logs.
   */
  prefillQuery?: string;
}

/**
 * What the dialog resolves with: an ARRAY of food logs to commit (one per confirmed item). The page logs
 * the batch (a single one resolves a 1-element array). `undefined` === cancelled / nothing to log.
 */
export type AddFoodResult = AddFoodRequest[];

/** The four tracked macros as a group — used for an item's TOTALS and its per-one-serving baseline. */
interface MacroSet {
  calories: number;
  proteinG: number;
  carbG: number;
  fatG: number;
}

/**
 * One reviewable row — an AI-parsed item OR a hand-added one. Every field is editable before the user
 * commits the batch; nothing is logged until "Add to <meal>". The macro fields are the per-item TOTALS at
 * the current {@link quantity}; {@link perUnit} holds the macros for ONE serving so changing the quantity
 * rescales the totals LIVE. Editing a macro directly sets that total at the current quantity and re-derives
 * its per-serving rate (so a later quantity change still scales from the value you typed). The commit posts
 * the TOTALS (the backend stores a manual row's macros as-is; quantity is a label there).
 */
interface ReviewItem extends MacroSet {
  description: string;
  quantity: number;
  /** Macros per ONE quantity unit — the scaling baseline. Full precision; only the totals are rounded. */
  perUnit: MacroSet;
}

/** Which input mode is active. */
type Mode = 'describe' | 'photo' | 'manual';

/** An upper sanity bound on a single item's quantity — blocks absurd typos. */
const MAX_QUANTITY = 9999;

/** Coerce a possibly-null / NaN / non-finite numeric input to a finite number (0 when unusable). */
function safeNum(n: number | null | undefined): number {
  return typeof n === 'number' && Number.isFinite(n) ? n : 0;
}

/** Round to one decimal place (the convention for macro grams, matching the server). */
const round1 = (n: number): number => Math.round(n * 10) / 10;

/** Macros for ONE serving, derived from an item's TOTALS at a given quantity (guards divide-by-zero). */
function perUnitOf(total: MacroSet, quantity: number): MacroSet {
  const q = quantity > 0 ? quantity : 1;
  return {
    calories: total.calories / q,
    proteinG: total.proteinG / q,
    carbG: total.carbG / q,
    fatG: total.fatG / q,
  };
}

/** Scale a per-serving baseline up to TOTALS for a quantity (calories whole, macro grams to 0.1, floored at 0). */
function scaleTotals(perUnit: MacroSet, quantity: number): MacroSet {
  const q = Math.max(0, quantity);
  return {
    calories: Math.max(0, Math.round(perUnit.calories * q)),
    proteinG: round1(Math.max(0, perUnit.proteinG * q)),
    carbG: round1(Math.max(0, perUnit.carbG * q)),
    fatG: round1(Math.max(0, perUnit.fatG * q)),
  };
}

/** A fresh, all-zero review row (used by "+ add item" and the manual-mode single item). */
function blankItem(): ReviewItem {
  return {
    description: '',
    quantity: 1,
    calories: 0,
    proteinG: 0,
    carbG: 0,
    fatG: 0,
    perUnit: { calories: 0, proteinG: 0, carbG: 0, fatG: 0 },
  };
}

const MEALS: { value: Meal; label: string }[] = [
  { value: 'breakfast', label: 'Breakfast' },
  { value: 'lunch', label: 'Lunch' },
  { value: 'dinner', label: 'Dinner' },
  { value: 'snack', label: 'Snacks' },
];

/**
 * Add-food dialog. Three ways in (a segmented switch): DESCRIBE/SPEAK a meal (type it or dictate it with
 * the on-device mic) and let AI split it into items; snap/upload a PHOTO and let AI identify the foods; or
 * enter ONE item by hand. The two AI paths post to the parse-only `POST /api/ai/parse-meal` (text OR image;
 * always 200, writes nothing) and surface the returned items in an EDITABLE review list — each row's
 * description/quantity/macros are editable, removable, and the user can add a manual row too. Only on
 * "Add to <meal>" does the dialog resolve with one {@link AddFoodRequest} per confirmed row (no fdcId /
 * source → manual entries the backend auto-saves to "My foods"); the page logs the batch + refreshes.
 *
 * AI off / unconfigured / empty result → the dialog drops the user straight into manual entry (graceful).
 * No food/barcode SEARCH — that UI (and its USDA/FatSecret bindings) was removed from this dialog.
 */
@Component({
  selector: 'app-add-food-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './add-food-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './add-food-dialog.scss',
})
export class AddFoodDialog implements OnDestroy {
  private api = inject(Api);
  private ref = inject(MatDialogRef<AddFoodDialog, AddFoodResult>);
  private snack = inject(MatSnackBar);
  private auth = inject(AuthService);
  readonly data = inject<AddFoodData>(MAT_DIALOG_DATA);

  /** Text AI (Describe/Speak → parse-meal {text}) is gated by tracker.ai. */
  readonly canUseAi = this.auth.hasPermission(PERM.trackerAi);
  /** Multimodal AI (Photo → parse-meal {image}) is a SEPARATE permission; gate the Photo mode on it so we
   *  never show a vision action the server will 403. */
  readonly canUseVision = this.auth.hasPermission(PERM.aiVision);
  /** On-device speech-to-text availability — drives whether the mic button is offered in Describe mode. */
  readonly canSpeak = speechSupported();

  readonly meals = MEALS;

  /** Initial mode: Describe when text AI is held, else Photo when only vision is held, else Manual. */
  readonly mode = signal<Mode>(this.canUseAi ? 'describe' : this.canUseVision ? 'photo' : 'manual');
  readonly meal = signal<Meal>(this.data.meal);

  // ---- Describe / Speak ----
  readonly describeText = signal('');
  readonly describeLoading = signal(false);
  readonly canBuildMeal = computed(
    () => !this.describeLoading() && this.describeText().trim().length > 0,
  );

  /** True while the on-device mic is listening (drives the Stop affordance + a "listening" hint). */
  readonly listening = signal(false);
  /** The live partial transcript while dictating (shown italicised under the box). */
  readonly interim = signal('');
  /** Active speech-recognition handle, so Stop/Cancel can drive it. */
  private activeRec: VoiceRecording | null = null;

  // ---- Photo ----
  readonly photoLoading = signal(false);

  // ---- Review + commit ----
  /**
   * The editable item list. Empty in the input modes (describe/photo) until a parse lands; in MANUAL mode
   * it always holds exactly one row (a hand-entered item). Non-empty in an AI mode === showing the review.
   */
  readonly items = signal<ReviewItem[]>([]);
  /** True once an AI parse produced the current list (vs. a manual single item) — tweaks the review copy. */
  readonly fromAi = signal<'describe' | 'photo' | null>(null);
  /** Polite sr-only announcement of an AI parse result / its unavailability. */
  readonly announce = signal('');
  /** True while the commit is in flight (latches the Add button against a double-tap). */
  readonly saving = signal(false);

  /** Are we showing the editable review/commit step (any items present)? */
  readonly inReview = computed(() => this.items().length > 0);

  /** How many rows are committable (a non-empty description) — drives the Add button + its disabled state. */
  readonly committableCount = computed(
    () => this.items().filter((i) => i.description.trim().length > 0).length,
  );

  constructor() {
    // Pre-seed the describe box from an AI food suggestion (editable; never auto-logs).
    const seed = this.data.prefillQuery?.trim();
    if (seed && this.canUseAi) {
      this.describeText.set(seed);
    } else if (seed) {
      // No text AI → seed a manual item's description instead so the prefill isn't lost.
      this.startManual();
      this.items.update((list) =>
        list.map((it, i) => (i === 0 ? { ...it, description: seed } : it)),
      );
    }
  }

  /** Switch input mode. Leaving a mode abandons its transient state (a pending parse / live mic). */
  setMode(m: Mode): void {
    if (m === this.mode()) return;
    this.stopListening(true);
    this.items.set([]);
    this.fromAi.set(null);
    this.announce.set('');
    this.mode.set(m);
    if (m === 'manual') this.startManual();
  }

  // ─────────────────────────────────────────── describe / speak ────────────────────────────────

  /** Begin on-device dictation: one-time voice notice, then stream the transcript into the text box. */
  async dictate(): Promise<void> {
    if (this.listening() || !this.canSpeak) return;
    if (!(await confirmVoiceNotice())) return; // declined the privacy notice → abort, nothing captured.
    try {
      const { recording, done } = recordTranscript((t) => this.interim.set(t));
      this.activeRec = recording;
      this.listening.set(true);
      this.interim.set('');
      done.then(
        (res) => this.onDictation(res?.text ?? null),
        (err) => this.onDictationError(err),
      );
    } catch (e) {
      this.onDictationError(e);
    }
  }

  /** Stop listening and keep what was transcribed (the Done affordance). */
  stopListening(discard = false): void {
    if (!this.activeRec) return;
    if (discard) this.activeRec.abort();
    else this.activeRec.stop();
    this.activeRec = null;
    this.listening.set(false);
    if (discard) this.interim.set('');
  }

  /** Fold the final transcript into the text box (appending, so a prior typed note isn't clobbered). */
  private onDictation(text: string | null): void {
    this.listening.set(false);
    this.activeRec = null;
    const heard = (text ?? '').trim();
    this.interim.set('');
    if (!heard) return;
    const prior = this.describeText().trim();
    this.describeText.set(prior ? `${prior} ${heard}` : heard);
  }

  private onDictationError(err: unknown): void {
    this.listening.set(false);
    this.activeRec = null;
    this.interim.set('');
    const msg = err instanceof Error ? err.message : 'Could not capture audio. Type instead.';
    this.snack.open(msg, 'OK', { duration: 4000 });
  }

  /** "Build meal": send the typed/dictated text to parse-meal → editable review (or graceful manual). */
  async buildMeal(): Promise<void> {
    if (!this.canBuildMeal()) return;
    this.stopListening(); // keep any in-flight transcript before we parse.
    this.describeLoading.set(true);
    this.announce.set('Building your meal with AI…');
    try {
      const res = await firstValueFrom(this.api.parseMeal({ text: this.describeText().trim() }));
      this.applyParse(res.items ?? [], 'describe');
    } catch {
      // The endpoint floors to 200, so a throw is a transport error — degrade to manual.
      this.toManualFallback('AI meal build unavailable — add it manually');
    } finally {
      this.describeLoading.set(false);
    }
  }

  // ─────────────────────────────────────────── photo ───────────────────────────────────────────

  /** 📷 Snap a meal photo (rear camera on mobile) → parse-meal {image} → review. */
  async snapPhoto(): Promise<void> {
    await this.runPhoto(captureImage);
  }

  /** 🖼️ Attach an existing photo from the gallery/files → parse-meal {image} → review. */
  async attachPhoto(): Promise<void> {
    await this.runPhoto(pickImage);
  }

  /**
   * Shared meal-photo flow: one-time privacy notice, obtain the (downscaled, in-memory) image, send it to
   * parse-meal, and route the items to review. The image is only read to identify the food — never stored.
   * AI off / empty / error degrades to manual entry.
   */
  private async runPhoto(source: () => Promise<ImageRequest | null>): Promise<void> {
    if (!this.canUseVision || this.photoLoading()) return;
    if (!(await confirmPhotoNotice())) return; // declined the privacy notice → abort, nothing sent.
    let image: ImageRequest | null;
    try {
      image = await source();
    } catch {
      this.snack.open('Could not read that image — try another photo', 'OK', { duration: 4000 });
      return;
    }
    if (!image) return; // picker cancelled.
    this.photoLoading.set(true);
    this.announce.set('Reading your meal photo with AI…');
    try {
      const res = await firstValueFrom(
        this.api.parseMeal({
          imageBase64: image.imageBase64,
          mimeType: image.mimeType,
        }),
      );
      this.applyParse(res.items ?? [], 'photo');
    } catch {
      this.toManualFallback('AI photo unavailable — add it manually');
    } finally {
      this.photoLoading.set(false);
    }
  }

  // ─────────────────────────────────────────── review / commit ─────────────────────────────────

  /**
   * Route a parse result into the editable review list. Items present → show them. Empty (AI off /
   * unparseable / nothing found) → drop into manual entry so the user is never stranded.
   */
  private applyParse(parsed: ParsedFoodItemDto[], from: 'describe' | 'photo'): void {
    if (parsed.length === 0) {
      this.toManualFallback(
        from === 'photo'
          ? 'No foods found in that photo — add it manually'
          : 'No items found — add it manually',
      );
      return;
    }
    this.items.set(
      parsed.map((i) => {
        const quantity = i.quantity > 0 ? i.quantity : 1;
        const total: MacroSet = {
          calories: Math.max(0, Math.round(safeNum(i.calories))),
          proteinG: Math.max(0, safeNum(i.proteinG)),
          carbG: Math.max(0, safeNum(i.carbG)),
          fatG: Math.max(0, safeNum(i.fatG)),
        };
        // Capture the per-serving baseline so adjusting the quantity rescales these totals live.
        return { description: i.description, quantity, ...total, perUnit: perUnitOf(total, quantity) };
      }),
    );
    this.fromAi.set(from);
    this.announce.set(
      `AI found ${parsed.length} ${parsed.length === 1 ? 'item' : 'items'}. Review and edit, then add to your day.`,
    );
  }

  /** Graceful fallback: seed a single empty manual row + steer the user there. */
  private toManualFallback(msg: string): void {
    this.snack.open(msg, 'OK', { duration: 4000 });
    this.announce.set(`${msg}.`);
    this.startManual();
  }

  /** Enter manual mode with exactly one blank, editable item (the review list is the manual form). */
  startManual(): void {
    this.fromAi.set(null);
    this.mode.set('manual');
    this.items.set([blankItem()]);
  }

  /** Add a fresh blank item to the review list (the "+ add item" affordance). */
  addItem(): void {
    this.items.update((list) => [...list, blankItem()]);
  }

  /**
   * Patch one field on a review row. Numeric fields are coerced through safeNum + floored at 0 so a
   * blanked/NaN input never reaches the commit, and quantity is capped.
   *  - Changing the QUANTITY rescales every macro TOTAL from the row's per-serving baseline (the live math).
   *  - Editing a MACRO sets that total at the current quantity AND re-derives its per-serving rate, so a
   *    later quantity change still scales from the value you typed.
   */
  updateItem(index: number, patch: Partial<ReviewItem>): void {
    this.items.update((list) =>
      list.map((it, i) => {
        if (i !== index) return it;
        const next: ReviewItem = { ...it, perUnit: { ...it.perUnit } };

        // Direct macro edits: set the total at the current quantity, then refresh that macro's per-unit rate.
        const qForRate = next.quantity > 0 ? next.quantity : 1;
        for (const k of ['calories', 'proteinG', 'carbG', 'fatG'] as const) {
          if (k in patch) {
            const v = Math.max(0, safeNum(patch[k] as number | null));
            next[k] = k === 'calories' ? Math.round(v) : v;
            next.perUnit[k] = next[k] / qForRate;
          }
        }

        // Quantity change: clamp, then rescale all totals from the (unchanged) per-serving baseline → LIVE math.
        if ('quantity' in patch) {
          next.quantity = Math.min(MAX_QUANTITY, Math.max(0, safeNum(patch.quantity as number | null)));
          const scaled = scaleTotals(next.perUnit, next.quantity);
          next.calories = scaled.calories;
          next.proteinG = scaled.proteinG;
          next.carbG = scaled.carbG;
          next.fatG = scaled.fatG;
        }

        if ('description' in patch) next.description = patch.description as string;
        return next;
      }),
    );
  }

  /**
   * Drop a review row. In an AI review, removing the last row returns to the input mode (so the user can
   * re-describe / re-shoot). In manual mode the form is one item, so a removal restarts a blank one.
   */
  removeItem(index: number): void {
    this.items.update((list) => list.filter((_, i) => i !== index));
    if (this.items().length > 0) return;
    if (this.mode() === 'manual') {
      this.startManual();
      return;
    }
    // AI review emptied → back to the input mode for that AI flow.
    this.fromAi.set(null);
    this.announce.set('');
  }

  /** Abandon the current review and return to the input mode (Discard). */
  discardReview(): void {
    this.items.set([]);
    this.fromAi.set(null);
    this.announce.set('');
    if (this.mode() === 'manual')
      this.setMode(this.canUseAi ? 'describe' : this.canUseVision ? 'photo' : 'manual');
  }

  /**
   * Commit every committable row (non-empty description) as a BATCH of manual food logs for the chosen
   * meal/date. No fdcId/source → the backend stores each as a manual entry + auto-saves it to "My foods".
   * Resolves the dialog with the array; the page logs them all + refreshes. Nothing logged until here.
   */
  add(): void {
    if (this.saving()) return; // in-flight guard against a double-tap before the overlay tears down.
    const meal = this.meal();
    const reqs: AddFoodRequest[] = this.items()
      .filter((i) => i.description.trim().length > 0)
      .map((i) => {
        const q = i.quantity > 0 ? i.quantity : 1;
        return {
          date: this.data.date,
          meal,
          description: i.description.trim(),
          quantity: q,
          servingDesc: q === 1 ? '1 serving' : `${q} servings`,
          // Macros are the per-item TOTALS shown in the row (already rescaled to the chosen quantity) —
          // commit them as-is; the backend stores a manual row's macros directly. safeNum guards NaN→0.
          calories: Math.max(0, Math.round(safeNum(i.calories))),
          proteinG: Math.max(0, safeNum(i.proteinG)),
          carbG: Math.max(0, safeNum(i.carbG)),
          fatG: Math.max(0, safeNum(i.fatG)),
          // No source / no fdcId → each is auto-saved to "My foods" like any manual log.
        };
      });
    if (reqs.length === 0) return;
    this.saving.set(true);
    this.ref.close(reqs);
  }

  cancel(): void {
    this.ref.close();
  }

  ngOnDestroy(): void {
    this.stopListening(true);
  }
}
