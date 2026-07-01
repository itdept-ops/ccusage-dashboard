import {
  ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../../core/api';
import {
  FamilyMeal, FamilyMealMacroProposal, FamilyMealMacroSource, FamilyMealSlot,
} from '../../../core/models';
import { BetaBottomSheet, BetaSegmentedControl, Segment } from '../../beta-ui';
import { round1, SLOT_ORDER, slotMeta } from '../meals-beta.model';

/** What the sheet emits on Save — the meal fields ready for create/patch (ingredients is raw newline text). */
export interface EditMealResult {
  slot: FamilyMealSlot;
  title: string;
  ingredients: string;
  servings: number;
  calories: number;
  proteinG: number;
  carbG: number;
  fatG: number;
  macroSource: FamilyMealMacroSource;
}

/**
 * Forage EditMealSheet — a BetaBottomSheet mirror of the desktop MealEditorDialog for CREATE + EDIT of one
 * planned meal on a fixed day. The user picks the slot (segmented), types a title + newline ingredients, and
 * optionally enters dish-TOTAL macros (servings + kcal/P/C/F) with a live per-serving readout. On an EXISTING
 * meal two ✨ assists preview a proposal before it fills the fields: "Estimate with AI" (canTrackerAi, reuses
 * `Api.estimateMealMacros`) and "Refine with food DB" (reuses `Api.refineMealMacros`); "Use these" applies the
 * staged proposal, nothing persists until Save. Presentational + AI-preview only — the page owns the
 * create/patch write (via the emitted result) so the reload/toast stays there.
 */
@Component({
  selector: 'app-forage-edit-meal-sheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatIconModule, BetaBottomSheet, BetaSegmentedControl],
  template: `
    <app-bs-sheet [(open)]="open" detent="full" [label]="isEdit() ? 'Edit meal' : 'Add meal'" (closed)="onClosed()">
      <div class="em">
        <header class="em-top">
          <span class="em-ic" aria-hidden="true"><mat-icon>{{ isEdit() ? 'edit_note' : 'restaurant_menu' }}</mat-icon></span>
          <div class="em-txt">
            <h2 class="em-title">{{ isEdit() ? 'Edit meal' : 'Add a meal' }}</h2>
            <span class="em-sub">{{ dayLabel() }}</span>
          </div>
        </header>

        <label class="em-lbl">Meal</label>
        <app-bs-segmented [segments]="slotSegs" [(value)]="slotKey" label="Meal of day" />

        <label class="em-lbl" for="em-title">Dish</label>
        <input id="em-title" class="em-input" type="text" [(ngModel)]="titleModel"
               placeholder="e.g. Sheet-pan salmon" maxlength="120" />

        <label class="em-lbl" for="em-ings">Ingredients <i>(one per line — feeds your grocery list)</i></label>
        <textarea id="em-ings" class="em-input em-area" rows="4" [(ngModel)]="ingredientsModel"
                  placeholder="2 salmon fillets&#10;1 lb asparagus&#10;olive oil"></textarea>
        @if (ingredientCount() > 0) {
          <span class="em-hint">{{ ingredientCount() }} ingredient{{ ingredientCount() === 1 ? '' : 's' }}</span>
        }

        <!-- Macros (dish TOTALS; per-serving derived). -->
        <div class="em-macros-h">
          <span class="em-lbl em-lbl--row">Macros <i>· {{ macroSourceLabel() }}</i></span>
          @if (hasMacros()) {
            <button type="button" class="em-clear" (click)="clearMacros()">Clear</button>
          }
        </div>

        <div class="em-grid">
          <label class="em-cell">
            <span>Servings</span>
            <input class="em-input" type="number" inputmode="numeric" min="1" [(ngModel)]="servingsModel"
                   (ngModelChange)="onMacroEdited()" />
          </label>
          <label class="em-cell">
            <span>Calories</span>
            <input class="em-input" type="number" inputmode="numeric" min="0" [(ngModel)]="caloriesModel"
                   (ngModelChange)="onMacroEdited()" placeholder="kcal" />
          </label>
          <label class="em-cell">
            <span>Protein (g)</span>
            <input class="em-input" type="number" inputmode="decimal" min="0" [(ngModel)]="proteinModel"
                   (ngModelChange)="onMacroEdited()" />
          </label>
          <label class="em-cell">
            <span>Carbs (g)</span>
            <input class="em-input" type="number" inputmode="decimal" min="0" [(ngModel)]="carbModel"
                   (ngModelChange)="onMacroEdited()" />
          </label>
          <label class="em-cell">
            <span>Fat (g)</span>
            <input class="em-input" type="number" inputmode="decimal" min="0" [(ngModel)]="fatModel"
                   (ngModelChange)="onMacroEdited()" />
          </label>
        </div>

        @if (hasMacros()) {
          <p class="em-per">Per serving · {{ perServing().calories }} kcal ·
            {{ perServing().proteinG }}P / {{ perServing().carbG }}C / {{ perServing().fatG }}F</p>
        }

        <!-- ✨ macro assists (existing meal only). -->
        @if (isEdit()) {
          <div class="em-assist">
            @if (canAi()) {
              <button type="button" class="em-assist-btn" [disabled]="!!macroBusy()" (click)="estimate()">
                @if (macroBusy() === 'ai') { <span class="em-spin" aria-hidden="true"></span> }
                @else { <mat-icon aria-hidden="true">auto_awesome</mat-icon> }
                Estimate with AI
              </button>
            }
            <button type="button" class="em-assist-btn" [disabled]="!!macroBusy()" (click)="refineDb()">
              @if (macroBusy() === 'database') { <span class="em-spin" aria-hidden="true"></span> }
              @else { <mat-icon aria-hidden="true">database</mat-icon> }
              From food DB
            </button>
          </div>
          @if (macroStatus()) { <p class="em-status" role="status" aria-live="polite">{{ macroStatus() }}</p> }
          @if (proposal(); as p) {
            <div class="em-prop">
              <span class="em-prop-h">Proposed · {{ p.data.calories }} kcal total ·
                {{ p.data.perServing.proteinG }}P / {{ p.data.perServing.carbG }}C / {{ p.data.perServing.fatG }}F per serving</span>
              <div class="em-prop-actions">
                <button type="button" class="em-prop-use" (click)="useProposal()">Use these</button>
                <button type="button" class="em-prop-no" (click)="dismissProposal()">Dismiss</button>
              </div>
            </div>
          }
        }

        <div class="em-actions">
          <button type="button" class="em-ghost" (click)="open.set(false)">Cancel</button>
          <button type="button" class="em-primary em-grow" [disabled]="!canSave()" (click)="save()">
            <mat-icon aria-hidden="true">check</mat-icon> {{ isEdit() ? 'Save' : 'Add meal' }}
          </button>
        </div>
      </div>
    </app-bs-sheet>
  `,
  styles: [`
    :host { display: contents; }
    .em { display: flex; flex-direction: column; gap: 10px; padding-top: 4px; }
    .em-top { display: flex; gap: 12px; align-items: center; margin-bottom: 2px; }
    .em-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 46px; height: 46px; border-radius: 15px;
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
    }
    .em-ic mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .em-txt { min-width: 0; display: flex; flex-direction: column; gap: 1px; }
    .em-title { margin: 0; font-family: var(--font-display); font-weight: 600; font-size: 21px; color: var(--ink); line-height: 1.1; }
    .em-sub { font-size: 12.5px; font-weight: 700; color: var(--ink-faint); }

    .em-lbl { font-size: 12px; font-weight: 800; letter-spacing: .04em; text-transform: uppercase; color: var(--ink-dim); margin-top: 4px; }
    .em-lbl i { font-style: normal; font-weight: 600; text-transform: none; letter-spacing: 0; color: var(--ink-faint); }
    .em-lbl--row { margin: 0; }
    .em-input {
      width: 100%; box-sizing: border-box; padding: 12px 14px; min-height: 46px;
      border-radius: var(--r-tile); border: 1px solid var(--hairline); background: var(--bg-sink);
      color: var(--ink); font: inherit; font-size: 15px;
    }
    .em-input::placeholder { color: var(--ink-faint); }
    .em-input:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
    .em-area { resize: vertical; min-height: 92px; line-height: 1.4; }
    .em-hint { font-size: 12px; font-weight: 600; color: var(--ink-faint); margin-top: -4px; }

    .em-macros-h { display: flex; align-items: center; justify-content: space-between; margin-top: 6px; }
    .em-clear {
      border: none; background: none; color: var(--accent-a); font: inherit; font-size: 13px; font-weight: 700;
      cursor: pointer; padding: 4px 2px;
    }
    .em-clear:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .em-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
    .em-cell { display: flex; flex-direction: column; gap: 4px; }
    .em-cell > span { font-size: 12px; font-weight: 700; color: var(--ink-dim); }
    .em-per { margin: 0; font-size: 12.5px; font-weight: 700; color: var(--ink-faint); text-align: center; }

    .em-assist { display: flex; gap: 10px; flex-wrap: wrap; }
    .em-assist-btn {
      flex: 1 1 auto; display: inline-flex; align-items: center; justify-content: center; gap: 7px;
      min-height: 44px; padding: 0 14px; border-radius: var(--r-pill);
      border: 1px solid color-mix(in srgb, var(--accent-a) 40%, var(--hairline)); background: transparent; color: var(--ink);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .em-assist-btn:disabled { opacity: .55; pointer-events: none; }
    .em-assist-btn:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .em-assist-btn mat-icon { font-size: 18px; width: 18px; height: 18px; color: var(--accent-a); }
    .em-status { margin: 0; font-size: 13px; font-weight: 600; color: var(--ink-dim); }

    .em-prop {
      display: flex; flex-direction: column; gap: 8px; padding: 12px 14px; border-radius: var(--r-tile);
      background: color-mix(in srgb, var(--accent-a) 10%, var(--bg-sink)); border: 1px solid color-mix(in srgb, var(--accent-a) 30%, transparent);
    }
    .em-prop-h { font-size: 13px; font-weight: 700; color: var(--ink); }
    .em-prop-actions { display: flex; gap: 10px; }
    .em-prop-use {
      flex: 1 1 auto; min-height: 40px; border: none; border-radius: var(--r-pill);
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
      font: inherit; font-size: 14px; font-weight: 800; cursor: pointer;
    }
    .em-prop-no {
      min-height: 40px; padding: 0 16px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: transparent; color: var(--ink-dim);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .em-prop-use:focus-visible, .em-prop-no:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .em-actions { display: flex; gap: 10px; align-items: center; padding-top: 6px; }
    .em-primary {
      display: inline-flex; align-items: center; justify-content: center; gap: 8px;
      min-height: 50px; padding: 0 20px; border: none; border-radius: var(--r-pill);
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
      font-family: var(--font-ui); font-size: 15px; font-weight: 800; cursor: pointer;
      box-shadow: var(--lift-2); -webkit-tap-highlight-color: transparent; touch-action: manipulation;
      transition: transform 120ms var(--ease-out);
    }
    .em-primary:active { transform: scale(.97); }
    .em-primary:disabled { opacity: .55; pointer-events: none; }
    .em-primary:focus-visible { outline: 2px solid var(--focus); outline-offset: 3px; }
    .em-primary mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .em-grow { flex: 1 1 auto; }
    .em-ghost {
      min-height: 50px; padding: 0 18px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: var(--bg-sink); color: var(--ink-dim);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .em-ghost:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .em-spin {
      width: 15px; height: 15px; border-radius: 50%;
      border: 2px solid color-mix(in srgb, var(--accent-a) 35%, transparent); border-top-color: var(--accent-a);
      animation: em-spin .7s linear infinite;
    }
    @keyframes em-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .em-spin { animation: none; } }
  `],
})
export class ForageEditMealSheet {
  private readonly api = inject(Api);

  /** Two-way open state, owned by the page. */
  readonly open = signal(false);
  /** The meal being edited (null = create). Set by the page before opening. */
  readonly meal = input<FamilyMeal | null>(null);
  /** The fixed day ("YYYY-MM-DD") a NEW meal lands on. */
  readonly localDate = input<string>('');
  /** A friendly label for the target day ("Monday, Jun 23"). */
  readonly dayLabel = input<string>('');
  /** The slot to pre-select on create (defaults to dinner). */
  readonly defaultSlot = input<FamilyMealSlot>('dinner');
  /** Whether the caller holds tracker.ai (gates the "Estimate with AI" assist). */
  readonly canAi = input<boolean>(false);
  /** Emitted on Save with the meal fields; the page performs the create/patch write. */
  readonly saved = output<EditMealResult>();

  protected readonly isEdit = computed(() => !!this.meal());

  protected readonly slotSegs: Segment[] = SLOT_ORDER.map(s => ({ key: s, label: slotMeta(s).label }));
  protected readonly slotKey = signal<string>('dinner');

  protected readonly titleModel = signal('');
  protected readonly ingredientsModel = signal('');
  protected readonly servingsModel = signal<string>('1');
  protected readonly caloriesModel = signal<string>('');
  protected readonly proteinModel = signal<string>('');
  protected readonly carbModel = signal<string>('');
  protected readonly fatModel = signal<string>('');
  protected readonly macroSource = signal<FamilyMealMacroSource>('none');

  protected readonly canSave = computed(() => this.titleModel().trim().length > 0);

  protected readonly ingredientCount = computed(() =>
    this.ingredientsModel().split('\n').map(s => s.trim()).filter(Boolean).length);

  private readonly servingsNum = computed(() => {
    const n = Math.floor(Number(this.servingsModel()));
    return Number.isFinite(n) && n >= 1 ? n : 1;
  });

  protected readonly hasMacros = computed(() =>
    [this.caloriesModel(), this.proteinModel(), this.carbModel(), this.fatModel()]
      .some(v => v.trim().length > 0));

  protected readonly perServing = computed(() => {
    const s = this.servingsNum();
    const num = (v: string) => { const n = Number(v); return Number.isFinite(n) && n > 0 ? n : 0; };
    return {
      calories: Math.round(num(this.caloriesModel()) / s),
      proteinG: round1(num(this.proteinModel()) / s),
      carbG: round1(num(this.carbModel()) / s),
      fatG: round1(num(this.fatModel()) / s),
    };
  });

  protected readonly macroSourceLabel = computed(() => {
    switch (this.macroSource()) {
      case 'ai': return 'AI estimate';
      case 'database': return 'from food DB';
      case 'manual': return 'manual';
      default: return 'not set';
    }
  });

  // ---- ✨ assists (existing meal only) ----
  protected readonly macroBusy = signal<'' | 'ai' | 'database'>('');
  protected readonly macroStatus = signal('');
  protected readonly proposal = signal<{ source: 'ai' | 'database'; data: FamilyMealMacroProposal } | null>(null);

  private wasOpen = false;
  constructor() {
    // Seed all fields from the (now-propagated) inputs on the closed→open transition. Doing it here — rather
    // than when the page sets `open` — avoids reading stale input signals before change detection runs.
    effect(() => {
      const isOpen = this.open();
      if (isOpen && !this.wasOpen) this.seed();
      this.wasOpen = isOpen;
    });
  }

  /** Public no-op kept for page symmetry; the real seed runs on the open transition (see constructor). */
  reset(): void { /* seeding happens on open via the effect */ }

  private seed(): void {
    const m = this.meal();
    this.slotKey.set(m?.slot ?? this.defaultSlot());
    this.titleModel.set(m?.title ?? '');
    this.ingredientsModel.set(m?.ingredients ?? '');
    this.servingsModel.set(String(m?.servings ?? 1));
    const set = m && m.macroSource !== 'none';
    this.caloriesModel.set(set ? String(m.calories) : '');
    this.proteinModel.set(set ? String(round1(m.proteinG)) : '');
    this.carbModel.set(set ? String(round1(m.carbG)) : '');
    this.fatModel.set(set ? String(round1(m.fatG)) : '');
    this.macroSource.set(m?.macroSource ?? 'none');
    this.macroBusy.set('');
    this.macroStatus.set('');
    this.proposal.set(null);
  }

  protected onMacroEdited(): void {
    if (this.macroSource() !== 'manual') this.macroSource.set('manual');
    if (this.proposal()) { this.proposal.set(null); this.macroStatus.set(''); }
  }

  protected clearMacros(): void {
    this.caloriesModel.set('');
    this.proteinModel.set('');
    this.carbModel.set('');
    this.fatModel.set('');
    this.servingsModel.set('1');
    this.macroSource.set('none');
    this.proposal.set(null);
    this.macroStatus.set('');
  }

  protected async estimate(): Promise<void> {
    const m = this.meal();
    if (!m || this.macroBusy()) return;
    this.macroBusy.set('ai');
    this.macroStatus.set('Estimating macros…');
    this.proposal.set(null);
    try {
      const data = await firstValueFrom(this.api.estimateMealMacros(m.id));
      this.proposal.set({ source: 'ai', data });
      this.macroStatus.set(data.note?.trim() || 'Here’s an AI estimate — review it, then tap “Use these”.');
    } catch (e) {
      this.proposal.set(null);
      this.macroStatus.set(this.assistError(e, 'AI'));
    } finally {
      this.macroBusy.set('');
    }
  }

  protected async refineDb(): Promise<void> {
    const m = this.meal();
    if (!m || this.macroBusy()) return;
    this.macroBusy.set('database');
    this.macroStatus.set('Looking up your ingredients…');
    this.proposal.set(null);
    try {
      const data = await firstValueFrom(this.api.refineMealMacros(m.id));
      this.proposal.set({ source: 'database', data });
      const matched = data.matched?.length ?? 0;
      this.macroStatus.set(matched > 0
        ? `Matched ${matched} ${matched === 1 ? 'ingredient' : 'ingredients'} — review the totals, then “Use these”.`
        : 'Couldn’t match any ingredients — try editing them, or enter macros manually.');
    } catch (e) {
      this.proposal.set(null);
      this.macroStatus.set(this.assistError(e, 'The food database'));
    } finally {
      this.macroBusy.set('');
    }
  }

  protected useProposal(): void {
    const p = this.proposal();
    if (!p) return;
    this.servingsModel.set(String(Math.max(1, Math.floor(p.data.servings) || 1)));
    this.caloriesModel.set(String(Math.round(p.data.calories)));
    this.proteinModel.set(String(round1(p.data.proteinG)));
    this.carbModel.set(String(round1(p.data.carbG)));
    this.fatModel.set(String(round1(p.data.fatG)));
    this.macroSource.set(p.source);
    this.proposal.set(null);
    this.macroStatus.set('Applied — adjust if you like, then Save.');
  }

  protected dismissProposal(): void {
    this.proposal.set(null);
    this.macroStatus.set('');
  }

  protected save(): void {
    if (!this.canSave()) return;
    const num = (v: string) => { const n = Number(v); return Number.isFinite(n) && n > 0 ? n : 0; };
    this.saved.emit({
      slot: this.slotKey() as FamilyMealSlot,
      title: this.titleModel().trim(),
      ingredients: this.ingredientsModel().trim(),
      servings: this.servingsNum(),
      calories: Math.round(num(this.caloriesModel())),
      proteinG: round1(num(this.proteinModel())),
      carbG: round1(num(this.carbModel())),
      fatG: round1(num(this.fatModel())),
      macroSource: this.macroSource(),
    });
    this.open.set(false);
  }

  private assistError(e: unknown, who: string): string {
    const status = (e as { status?: number })?.status;
    return status === 503
      ? `${who} isn’t available right now — you can enter macros manually.`
      : this.messageOf(e, `${who} couldn’t estimate the macros just now. Try again, or enter them manually.`);
  }

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }

  protected onClosed(): void { /* page clears its own open flag via two-way */ }
}
