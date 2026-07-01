import {
  ChangeDetectionStrategy, Component, computed, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { firstValueFrom } from 'rxjs';

import { Api } from '../../../core/api';
import { FamilyMeal, RefineMealResponse } from '../../../core/models';
import { BetaBottomSheet } from '../../beta-ui';
import { round1 } from '../meals-beta.model';

/** The phases of the refine flow: type a preference → previewing → applying the PATCH. */
type Phase = 'form' | 'loading' | 'preview' | 'applying';

const EXAMPLE_CHIPS: readonly string[] = [
  'make it vegetarian', 'lower the carbs', 'higher protein', 'swap a main ingredient',
];

/**
 * Forage RefineMealSheet — a BetaBottomSheet mirror of the desktop RefineMealDialog (gated tracker.ai). The
 * user writes a free-text preference; ✨ Refine asks Gemini (reuse-only `Api.refineMeal`, which WRITES NOTHING)
 * for a rewrite of THIS one dish and PREVIEWS the proposal (new title / ingredients / per-serving macros).
 * "Apply" persists it via the existing `Api.patchFamilyMeal` (per-serving → dish-total conversion, exactly like
 * the desktop) then emits `wrote` so the page reloads; the always-200 floor (`aiUsed:false`) shows a friendly
 * note and changes nothing. The sheet owns its own network (mirrors the desktop dialog); the page owns open +
 * the reload on `wrote`.
 */
@Component({
  selector: 'app-forage-refine-meal-sheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatIconModule, BetaBottomSheet],
  template: `
    <app-bs-sheet [(open)]="open" detent="full" label="Refine with AI"
                  [dismissable]="phase() !== 'applying'" (closed)="onClosed()">
      @if (meal(); as m) {
        <div class="rf">
          <header class="rf-top">
            <span class="rf-ic" aria-hidden="true"><mat-icon>auto_awesome</mat-icon></span>
            <div class="rf-txt">
              <h2 class="rf-title">Refine with AI</h2>
              <span class="rf-sub">{{ m.title }}</span>
            </div>
          </header>

          @if (phase() === 'form' || phase() === 'loading') {
            <label class="rf-lbl" for="rf-pref">What would you like changed?</label>
            <textarea id="rf-pref" class="rf-input rf-area" rows="3" [(ngModel)]="preference"
                      maxlength="300" placeholder="e.g. make it vegetarian and higher protein"
                      [disabled]="phase() === 'loading'"></textarea>
            <div class="rf-chips">
              @for (c of exampleChips; track c) {
                <button type="button" class="rf-chip" (click)="useExample(c)" [disabled]="phase() === 'loading'">{{ c }}</button>
              }
            </div>
            <button type="button" class="rf-primary" [disabled]="!canRefine() || phase() === 'loading'" (click)="refine()">
              @if (phase() === 'loading') { <span class="rf-spin" aria-hidden="true"></span> Refining… }
              @else { <mat-icon aria-hidden="true">auto_awesome</mat-icon> Refine }
            </button>
          }

          @if (phase() === 'preview' || phase() === 'applying') {
            @if (proposal(); as p) {
              <div class="rf-prop">
                <h3 class="rf-prop-title">{{ p.title }}</h3>
                <div class="rf-macros">
                  <div class="rf-macro rf-macro--cal"><span>{{ perServingCal(p) }}</span><i>kcal</i></div>
                  <div class="rf-macro"><span>{{ p.proteinG }}</span><i>P</i></div>
                  <div class="rf-macro"><span>{{ p.carbG }}</span><i>C</i></div>
                  <div class="rf-macro"><span>{{ p.fatG }}</span><i>F</i></div>
                </div>
                <p class="rf-per">per serving · makes {{ p.servings }}</p>
                @if (ingredientLines(p).length) {
                  <ul class="rf-ings" role="list">
                    @for (line of ingredientLines(p); track $index) { <li>{{ line }}</li> }
                  </ul>
                }
              </div>
            }
            <div class="rf-actions">
              <button type="button" class="rf-ghost" [disabled]="phase() === 'applying'" (click)="backToForm()">Try again</button>
              <button type="button" class="rf-primary rf-grow" [disabled]="phase() === 'applying'" (click)="apply()">
                @if (phase() === 'applying') { <span class="rf-spin" aria-hidden="true"></span> Applying… }
                @else { <mat-icon aria-hidden="true">check</mat-icon> Apply }
              </button>
            </div>
          }

          <p class="rf-live" role="status" aria-live="polite">{{ announce() }}</p>
        </div>
      }
    </app-bs-sheet>
  `,
  styles: [`
    :host { display: contents; }
    .rf { display: flex; flex-direction: column; gap: 12px; padding-top: 4px; }
    .rf-top { display: flex; gap: 12px; align-items: center; }
    .rf-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 46px; height: 46px; border-radius: 15px;
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
    }
    .rf-ic mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .rf-txt { min-width: 0; display: flex; flex-direction: column; gap: 1px; }
    .rf-title { margin: 0; font-family: var(--font-display); font-weight: 600; font-size: 21px; color: var(--ink); line-height: 1.1; }
    .rf-sub {
      font-size: 12.5px; font-weight: 700; color: var(--ink-faint);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }

    .rf-lbl { font-size: 12px; font-weight: 800; letter-spacing: .04em; text-transform: uppercase; color: var(--ink-dim); margin-top: 4px; }
    .rf-input {
      width: 100%; box-sizing: border-box; padding: 12px 14px; min-height: 46px;
      border-radius: var(--r-tile); border: 1px solid var(--hairline); background: var(--bg-sink);
      color: var(--ink); font: inherit; font-size: 15px;
    }
    .rf-input::placeholder { color: var(--ink-faint); }
    .rf-input:focus-visible { outline: 2px solid var(--focus); outline-offset: 1px; }
    .rf-area { resize: vertical; min-height: 78px; line-height: 1.4; }

    .rf-chips { display: flex; flex-wrap: wrap; gap: 8px; }
    .rf-chip {
      padding: 7px 12px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: var(--bg-sink); color: var(--ink-dim);
      font: inherit; font-size: 13px; font-weight: 700; cursor: pointer;
    }
    .rf-chip:disabled { opacity: .55; pointer-events: none; }
    .rf-chip:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .rf-primary {
      display: inline-flex; align-items: center; justify-content: center; gap: 8px;
      min-height: 50px; padding: 0 20px; border: none; border-radius: var(--r-pill);
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
      font-family: var(--font-ui); font-size: 15px; font-weight: 800; cursor: pointer;
      box-shadow: var(--lift-2); -webkit-tap-highlight-color: transparent; touch-action: manipulation;
      transition: transform 120ms var(--ease-out);
    }
    .rf-primary:active { transform: scale(.97); }
    .rf-primary:disabled { opacity: .55; pointer-events: none; }
    .rf-primary:focus-visible { outline: 2px solid var(--focus); outline-offset: 3px; }
    .rf-primary mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .rf-grow { flex: 1 1 auto; }
    .rf-ghost {
      min-height: 50px; padding: 0 18px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: var(--bg-sink); color: var(--ink-dim);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .rf-ghost:disabled { opacity: .55; pointer-events: none; }
    .rf-ghost:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .rf-prop {
      display: flex; flex-direction: column; gap: 10px; padding: 14px; border-radius: var(--r-tile);
      background: var(--bg-rise); border: 1px solid color-mix(in srgb, var(--accent-a) 28%, var(--hairline));
    }
    .rf-prop-title { margin: 0; font-family: var(--font-display); font-weight: 600; font-size: 18px; color: var(--ink); }
    .rf-macros { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
    .rf-macro {
      display: flex; flex-direction: column; align-items: center; gap: 1px;
      padding: 9px 4px; border-radius: var(--r-tile); background: var(--bg-sink); border: 1px solid var(--hairline);
    }
    .rf-macro--cal { border-color: color-mix(in srgb, var(--accent-a) 36%, transparent); }
    .rf-macro span { font-family: var(--font-display); font-variant-numeric: tabular-nums; font-size: 18px; font-weight: 600; color: var(--ink); line-height: 1; }
    .rf-macro i { font-style: normal; font-size: 10px; font-weight: 800; letter-spacing: .03em; color: var(--ink-dim); }
    .rf-per { margin: 0; font-size: 12px; font-weight: 700; color: var(--ink-faint); text-align: center; }
    .rf-ings {
      margin: 0; padding: 0; list-style: none;
      border: 1px solid var(--hairline); border-radius: var(--r-tile); overflow: hidden;
    }
    .rf-ings li { font-size: 14px; font-weight: 600; color: var(--ink); padding: 8px 12px; background: var(--bg-sink); }
    .rf-ings li + li { border-top: 1px solid var(--hairline); }

    .rf-actions { display: flex; gap: 10px; align-items: center; }
    .rf-live { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); }

    .rf-spin {
      width: 16px; height: 16px; border-radius: 50%;
      border: 2px solid color-mix(in srgb, var(--tech-text-on-accent, #07140d) 35%, transparent); border-top-color: var(--tech-text-on-accent, #07140d);
      animation: rf-spin .7s linear infinite;
    }
    @keyframes rf-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) { .rf-spin { animation: none; } }
  `],
})
export class ForageRefineMealSheet {
  private readonly api = inject(Api);

  /** Two-way open state, owned by the page. */
  readonly open = signal(false);
  /** The meal being refined (null when none). */
  readonly meal = input<FamilyMeal | null>(null);
  /** Emitted when the refined meal was applied (PATCHed), so the page reloads the week. */
  readonly wrote = output<string>();

  protected readonly exampleChips = EXAMPLE_CHIPS;
  protected readonly preference = signal('');
  protected readonly phase = signal<Phase>('form');
  protected readonly announce = signal('');
  protected readonly proposal = signal<RefineMealResponse | null>(null);
  private inFlight = false;

  protected readonly canRefine = computed(() => this.preference().trim().length > 0);

  /** Reset to the empty form — call before (re)opening. */
  reset(): void {
    this.preference.set('');
    this.phase.set('form');
    this.proposal.set(null);
    this.announce.set('');
    this.inFlight = false;
  }

  protected useExample(text: string): void {
    const current = this.preference().trim();
    this.preference.set(current ? `${current}, ${text}` : text);
  }

  protected perServingCal(p: RefineMealResponse): number {
    const s = p.servings > 0 ? p.servings : 1;
    return Math.round(p.calories / s);
  }

  protected ingredientLines(p: RefineMealResponse): string[] {
    return (p.ingredients ?? '').split('\n').map(s => s.trim()).filter(Boolean);
  }

  protected async refine(): Promise<void> {
    const m = this.meal();
    if (!m || this.inFlight || !this.canRefine()) return;
    this.inFlight = true;
    this.phase.set('loading');
    this.announce.set('Refining your meal with AI…');
    try {
      const res = await firstValueFrom(this.api.refineMeal({
        title: m.title,
        ingredients: m.ingredients,
        servings: m.servings,
        calories: m.calories,
        proteinG: m.perServing.proteinG,
        carbG: m.perServing.carbG,
        fatG: m.perServing.fatG,
        preference: this.preference().trim(),
      }));
      if (!res.aiUsed) {
        this.phase.set('form');
        this.announce.set('AI is unavailable right now — nothing changed.');
        return;
      }
      this.proposal.set(res);
      this.phase.set('preview');
      this.announce.set(`Here's a refined version: ${res.title}.`);
    } catch {
      this.phase.set('form');
      this.announce.set('Couldn’t refine that meal — please try again.');
    } finally {
      this.inFlight = false;
    }
  }

  protected backToForm(): void {
    if (this.phase() === 'applying') return;
    this.proposal.set(null);
    this.phase.set('form');
  }

  protected async apply(): Promise<void> {
    const m = this.meal();
    const res = this.proposal();
    if (!m || !res || this.inFlight) return;
    this.inFlight = true;
    this.phase.set('applying');
    try {
      await firstValueFrom(this.api.patchFamilyMeal(m.id, {
        title: res.title,
        ingredients: res.ingredients,
        servings: res.servings,
        calories: res.calories,
        proteinG: round1(res.proteinG * res.servings),
        carbG: round1(res.carbG * res.servings),
        fatG: round1(res.fatG * res.servings),
        macroSource: 'ai',
      }));
      this.open.set(false);
      this.wrote.emit(res.title);
    } catch {
      this.phase.set('preview');
      this.announce.set('Couldn’t apply that refine — please try again.');
    } finally {
      this.inFlight = false;
    }
  }

  protected onClosed(): void { /* page clears its own open flag via two-way */ }
}
