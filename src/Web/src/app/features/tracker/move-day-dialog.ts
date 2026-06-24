import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';

import { MoveDayCategory, MoveDayRequest } from '../../core/models';

/** Opens with the currently-viewed date (the source the move pulls entries OFF of). */
export interface MoveDayData {
  /** The current viewed local date (YYYY-MM-DD) — the move's source (`fromDate`). */
  fromDate: string;
}

/** A movable category with its default-checked state and a friendly label. */
interface CategoryRow {
  key: MoveDayCategory;
  label: string;
  icon: string;
}

const CATEGORIES: CategoryRow[] = [
  { key: 'food', label: 'Food', icon: 'restaurant' },
  { key: 'exercise', label: 'Exercise', icon: 'fitness_center' },
  { key: 'hydration', label: 'Hydration', icon: 'water_drop' },
  { key: 'weight', label: 'Weight', icon: 'monitor_weight' },
  { key: 'activity', label: 'Activity', icon: 'directions_walk' },
];

/** The day before `iso` as a local YYYY-MM-DD (the default target — "moved to the wrong day" usually means yesterday). */
function dayBefore(iso: string): string {
  const d = new Date(iso + 'T00:00:00');
  d.setDate(d.getDate() - 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/**
 * "Move day" dialog. Picks a target date (defaults to the day before the viewed date) and which
 * categories to move (all checked by default). Resolves with a {@link MoveDayRequest} (fromDate = the
 * viewed date, toDate, the selected categories) for the page to POST. The one-per-day note warns that
 * weight/activity replace any existing entry on the target day.
 */
@Component({
  selector: 'app-move-day-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCheckboxModule,
    MatIconModule,
  ],
  template: `
    <h2 mat-dialog-title class="md-title">Move day</h2>
    <mat-dialog-content class="md-body">
      <p class="md-intro">
        Move this day's entries to another date. Handy when a day was logged on the wrong date.
      </p>

      <mat-form-field appearance="outline" class="md-field">
        <mat-label>Move to</mat-label>
        <input
          matInput
          type="date"
          cdkFocusInitial
          [ngModel]="toDate()"
          (ngModelChange)="toDate.set($event)"
        />
        <mat-hint>From {{ data.fromDate }} → {{ toDate() || '—' }}</mat-hint>
      </mat-form-field>

      <fieldset class="md-cats">
        <legend class="micro-label">What to move</legend>
        @for (c of categories; track c.key) {
          <mat-checkbox
            class="md-cat"
            [checked]="isChecked(c.key)"
            (change)="toggle(c.key, $event.checked)"
          >
            <mat-icon class="md-cat-icon" aria-hidden="true">{{ c.icon }}</mat-icon> {{ c.label }}
          </mat-checkbox>
        }
      </fieldset>

      <p class="md-note">
        <mat-icon aria-hidden="true">info</mat-icon>
        Weight and activity will replace any existing entry on the target day.
      </p>

      @if (sameDate()) {
        <p class="md-warn" role="alert">
          Pick a different date — the target can't be the same day.
        </p>
      }
    </mat-dialog-content>
    <mat-dialog-actions class="md-actions" align="end">
      <button mat-stroked-button type="button" (click)="cancel()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        color="primary"
        [disabled]="!canMove()"
        (click)="move()"
      >
        Move
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    .md-title {
      font-family: var(--tech-font-ui);
      font-weight: 700;
      color: var(--tech-text);
    }
    .md-body {
      display: flex;
      flex-direction: column;
      gap: var(--tech-space-3);
      min-width: min(340px, 82vw);
      padding-top: 4px !important;
    }
    .md-intro {
      margin: 0;
      color: var(--tech-text-dim);
      font-size: 0.9rem;
      line-height: 1.4;
    }
    .md-field {
      width: 100%;
    }
    .md-cats {
      display: flex;
      flex-direction: column;
      gap: var(--tech-space-1);
      border: none;
      margin: 0;
      padding: 0;
    }
    .md-cats legend {
      margin-bottom: var(--tech-space-1);
    }
    .md-cat {
      min-height: 40px;
    }
    .md-cat-icon {
      font-size: 18px;
      height: 18px;
      width: 18px;
      vertical-align: text-bottom;
      margin-right: 2px;
      color: var(--tech-text-dim);
    }
    .md-note,
    .md-warn {
      display: flex;
      align-items: flex-start;
      gap: 6px;
      margin: 0;
      font-size: 0.82rem;
      line-height: 1.35;
    }
    .md-note {
      color: var(--tech-text-dim);
    }
    .md-note mat-icon,
    .md-warn mat-icon {
      font-size: 18px;
      height: 18px;
      width: 18px;
      flex: none;
    }
    .md-warn {
      color: var(--tech-error, #ff5c6c);
    }
    .md-actions {
      padding: var(--tech-space-3) var(--tech-space-4);
      gap: 8px;
      button {
        border-radius: var(--tech-r-control);
        font-weight: 600;
        min-height: 44px;
      }
    }
  `,
})
export class MoveDayDialog {
  private ref = inject(MatDialogRef<MoveDayDialog, MoveDayRequest>);
  readonly data = inject<MoveDayData>(MAT_DIALOG_DATA);

  readonly categories = CATEGORIES;

  /** Target date — defaults to the day before the viewed date. */
  readonly toDate = signal<string>(dayBefore(this.data.fromDate));

  /** The set of selected category keys; all checked by default. */
  private readonly selected = signal<Set<MoveDayCategory>>(new Set(CATEGORIES.map((c) => c.key)));

  isChecked(key: MoveDayCategory): boolean {
    return this.selected().has(key);
  }

  toggle(key: MoveDayCategory, checked: boolean): void {
    const next = new Set(this.selected());
    if (checked) next.add(key);
    else next.delete(key);
    this.selected.set(next);
  }

  /** True when the picked target equals the source (an invalid no-op move). */
  readonly sameDate = computed(() => !!this.toDate() && this.toDate() === this.data.fromDate);

  /** Enabled only with a valid, different target date and at least one category selected. */
  readonly canMove = computed(
    () => !!this.toDate() && !this.sameDate() && this.selected().size > 0,
  );

  move(): void {
    if (!this.canMove()) return;
    // Order the categories canonically; if all are selected, omit (server treats null/empty as all).
    const all = CATEGORIES.length;
    const chosen = CATEGORIES.map((c) => c.key).filter((k) => this.selected().has(k));
    this.ref.close({
      fromDate: this.data.fromDate,
      toDate: this.toDate(),
      categories: chosen.length === all ? undefined : chosen,
    });
  }

  cancel(): void {
    this.ref.close();
  }
}
