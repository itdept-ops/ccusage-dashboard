import {
  ChangeDetectionStrategy, Component, computed, effect, input, output, signal,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { FamilyMeal, HouseholdMember } from '../../../core/models';
import { BetaBottomSheet } from '../../beta-ui';
import { round1 } from '../meals-beta.model';

/** What the sheet emits on Add: servings, the chosen target's id (undefined = self), and its display name. */
export interface AddToTrackerResult {
  servings: number;
  targetUserId?: number;
  targetName: string;
}

const DEFAULT_SERVINGS = 1;
const MIN_SERVINGS = 0.1;
const MAX_SERVINGS = 99;

/**
 * Forage AddToTrackerSheet — a BetaBottomSheet mirror of the desktop AddMealToTrackerDialog (gated tracker.self
 * on the page). The user picks how many SERVINGS (default 1, 0.1..99) and WHOSE tracker (self + household
 * co-members, by display name — never an email), with a LIVE per-serving×servings macro preview rounded the
 * same way the server rounds. On Add it emits `{ servings, targetUserId, targetName }`; the PAGE performs the
 * `Api.addMealToTracker` write (keeping its busy-lock + toast). Members fall back to a synthetic Me row when
 * none are supplied (solo caller), so self-logging always works.
 */
@Component({
  selector: 'app-forage-add-to-tracker-sheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, BetaBottomSheet],
  template: `
    <app-bs-sheet [(open)]="open" detent="half" label="Add to tracker" (closed)="onClosed()">
      @if (meal(); as m) {
        <div class="at">
          <header class="at-top">
            <span class="at-ic" aria-hidden="true"><mat-icon>restaurant</mat-icon></span>
            <div class="at-txt">
              <h2 class="at-title">Add to tracker</h2>
              <span class="at-sub">{{ m.title }}</span>
            </div>
          </header>

          <label class="at-lbl">Servings</label>
          <div class="at-step" role="group" aria-label="Servings">
            <button type="button" class="at-step-btn" (click)="bump(-0.5)" aria-label="Fewer servings">
              <mat-icon aria-hidden="true">remove</mat-icon>
            </button>
            <span class="at-step-n">{{ servingsLabel() }}</span>
            <button type="button" class="at-step-btn" (click)="bump(0.5)" aria-label="More servings">
              <mat-icon aria-hidden="true">add</mat-icon>
            </button>
          </div>

          @if (members.length > 1) {
            <label class="at-lbl">Whose tracker?</label>
            <div class="at-who" role="group" aria-label="Whose tracker">
              @for (mem of members; track mem.userId) {
                <button type="button" class="at-mem" [class.is-on]="mem.userId === targetUserId()"
                        (click)="targetUserId.set(mem.userId)" [attr.aria-pressed]="mem.userId === targetUserId()">
                  <span class="at-av" aria-hidden="true">{{ initials(mem.name) }}</span>
                  <span class="at-mem-name">{{ mem.isSelf ? 'Me' : mem.name }}</span>
                </button>
              }
            </div>
          }

          <div class="at-preview">
            <span class="at-preview-h">Logs</span>
            <div class="at-macros">
              <div class="at-macro at-macro--cal"><span>{{ preview().calories }}</span><i>kcal</i></div>
              <div class="at-macro"><span>{{ preview().proteinG }}</span><i>P</i></div>
              <div class="at-macro"><span>{{ preview().carbG }}</span><i>C</i></div>
              <div class="at-macro"><span>{{ preview().fatG }}</span><i>F</i></div>
            </div>
          </div>

          <div class="at-actions">
            <button type="button" class="at-ghost" (click)="open.set(false)">Cancel</button>
            <button type="button" class="at-primary at-grow" [disabled]="!canAdd()" (click)="add()">
              <mat-icon aria-hidden="true">add</mat-icon> Add to tracker
            </button>
          </div>
        </div>
      }
    </app-bs-sheet>
  `,
  styles: [`
    :host { display: contents; }
    .at { display: flex; flex-direction: column; gap: 12px; padding-top: 4px; }
    .at-top { display: flex; gap: 12px; align-items: center; }
    .at-ic {
      flex: 0 0 auto; display: grid; place-items: center; width: 46px; height: 46px; border-radius: 15px;
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
    }
    .at-ic mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .at-txt { min-width: 0; display: flex; flex-direction: column; gap: 1px; }
    .at-title { margin: 0; font-family: var(--font-display); font-weight: 600; font-size: 21px; color: var(--ink); line-height: 1.1; }
    .at-sub { font-size: 12.5px; font-weight: 700; color: var(--ink-faint); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

    .at-lbl { font-size: 12px; font-weight: 800; letter-spacing: .04em; text-transform: uppercase; color: var(--ink-dim); }

    .at-step { display: flex; align-items: center; justify-content: center; gap: 18px; }
    .at-step-btn {
      display: grid; place-items: center; width: 48px; height: 48px; border-radius: 50%;
      border: 1px solid color-mix(in srgb, var(--accent-a) 40%, var(--hairline)); background: var(--bg-sink); color: var(--ink);
      cursor: pointer; -webkit-tap-highlight-color: transparent;
    }
    .at-step-btn:active { transform: scale(.95); }
    .at-step-btn:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .at-step-n {
      min-width: 64px; text-align: center; font-family: var(--font-display); font-variant-numeric: tabular-nums;
      font-size: 26px; font-weight: 600; color: var(--ink);
    }

    .at-who { display: flex; flex-wrap: wrap; gap: 8px; }
    .at-mem {
      display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px 6px 6px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: var(--bg-sink); color: var(--ink);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .at-mem.is-on { border-color: color-mix(in srgb, var(--accent-a) 55%, transparent); background: color-mix(in srgb, var(--accent-a) 12%, var(--bg-sink)); }
    .at-mem:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
    .at-av {
      display: grid; place-items: center; width: 28px; height: 28px; border-radius: 50%;
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
      font-size: 12px; font-weight: 800;
    }

    .at-preview {
      display: flex; flex-direction: column; gap: 8px; padding: 12px 14px; border-radius: var(--r-tile);
      background: var(--bg-rise); border: 1px solid var(--hairline);
    }
    .at-preview-h { font-size: 11px; font-weight: 800; letter-spacing: .05em; text-transform: uppercase; color: var(--ink-dim); }
    .at-macros { display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; }
    .at-macro {
      display: flex; flex-direction: column; align-items: center; gap: 1px;
      padding: 9px 4px; border-radius: var(--r-tile); background: var(--bg-sink); border: 1px solid var(--hairline);
    }
    .at-macro--cal { border-color: color-mix(in srgb, var(--accent-a) 36%, transparent); }
    .at-macro span { font-family: var(--font-display); font-variant-numeric: tabular-nums; font-size: 18px; font-weight: 600; color: var(--ink); line-height: 1; }
    .at-macro i { font-style: normal; font-size: 10px; font-weight: 800; letter-spacing: .03em; color: var(--ink-dim); }

    .at-actions { display: flex; gap: 10px; align-items: center; padding-top: 2px; }
    .at-primary {
      display: inline-flex; align-items: center; justify-content: center; gap: 8px;
      min-height: 50px; padding: 0 20px; border: none; border-radius: var(--r-pill);
      background: linear-gradient(135deg, var(--accent-a), var(--accent-b)); color: var(--tech-text-on-accent, #07140d);
      font-family: var(--font-ui); font-size: 15px; font-weight: 800; cursor: pointer;
      box-shadow: var(--lift-2); -webkit-tap-highlight-color: transparent; touch-action: manipulation;
      transition: transform 120ms var(--ease-out);
    }
    .at-primary:active { transform: scale(.97); }
    .at-primary:disabled { opacity: .55; pointer-events: none; }
    .at-primary:focus-visible { outline: 2px solid var(--focus); outline-offset: 3px; }
    .at-primary mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .at-grow { flex: 1 1 auto; }
    .at-ghost {
      min-height: 50px; padding: 0 18px; border-radius: var(--r-pill);
      border: 1px solid var(--hairline); background: var(--bg-sink); color: var(--ink-dim);
      font: inherit; font-size: 14px; font-weight: 700; cursor: pointer;
    }
    .at-ghost:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
  `],
})
export class ForageAddToTrackerSheet {
  /** Two-way open state, owned by the page. */
  readonly open = signal(false);
  /** The meal being logged (its per-serving macros drive the live preview). */
  readonly meal = input<FamilyMeal | null>(null);
  /** The household members for the "whose tracker?" picker (may be empty → synthetic Me). */
  readonly householdMembers = input<HouseholdMember[]>([]);
  /** Emitted on Add; the page performs the addMealToTracker write. */
  readonly add_ = output<AddToTrackerResult>();

  /** Ordered members (self first), with a synthetic Me fallback when none are supplied. */
  protected get members(): HouseholdMember[] {
    const list = this.householdMembers() ?? [];
    if (list.length === 0) {
      return [{ userId: -1, name: 'Me', picture: null, role: 'self', isSelf: true }];
    }
    const self = list.filter(m => m.isSelf);
    const others = list.filter(m => !m.isSelf);
    return [...self, ...others];
  }

  protected readonly targetUserId = signal<number>(-1);
  protected readonly servings = signal<number>(DEFAULT_SERVINGS);

  private readonly safeServings = computed(() => {
    const s = this.servings();
    if (!Number.isFinite(s) || s <= 0) return DEFAULT_SERVINGS;
    return Math.min(Math.max(s, MIN_SERVINGS), MAX_SERVINGS);
  });

  protected readonly servingsLabel = computed(() => {
    const s = this.safeServings();
    return Number.isInteger(s) ? String(s) : s.toFixed(1);
  });

  protected readonly canAdd = computed(() => (this.meal()?.macroSource ?? 'none') !== 'none');

  protected readonly preview = computed(() => {
    const s = this.safeServings();
    const p = this.meal()?.perServing ?? { calories: 0, proteinG: 0, carbG: 0, fatG: 0 };
    return {
      calories: Math.max(0, Math.round(p.calories * s)),
      proteinG: round1(p.proteinG * s),
      carbG: round1(p.carbG * s),
      fatG: round1(p.fatG * s),
    };
  });

  private wasOpen = false;
  constructor() {
    // Seed defaults on the closed→open transition, once the members/meal inputs have propagated.
    effect(() => {
      const isOpen = this.open();
      if (isOpen && !this.wasOpen) {
        this.servings.set(DEFAULT_SERVINGS);
        this.targetUserId.set(this.members[0]?.userId ?? -1);
      }
      this.wasOpen = isOpen;
    });
  }

  /** Public no-op kept for page symmetry; the real seed runs on the open transition (see constructor). */
  reset(): void { /* seeding happens on open via the effect */ }

  protected bump(delta: number): void {
    const next = Math.min(Math.max(round1(this.safeServings() + delta), MIN_SERVINGS), MAX_SERVINGS);
    this.servings.set(next);
  }

  protected initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  protected add(): void {
    if (!this.canAdd()) return;
    const t = this.members.find(m => m.userId === this.targetUserId()) ?? this.members[0];
    const targetUserId = t && !t.isSelf ? t.userId : undefined;
    this.add_.emit({
      servings: this.safeServings(),
      targetUserId,
      targetName: t?.isSelf ? 'your' : `${t?.name}'s`,
    });
    this.open.set(false);
  }

  protected onClosed(): void { /* page clears its own open flag via two-way */ }
}
