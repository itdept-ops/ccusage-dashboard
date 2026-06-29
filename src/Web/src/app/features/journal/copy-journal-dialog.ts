import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/**
 * Opens with the source day (the entry being copied) so the dialog can default to a sensible target and
 * forbid copying a day onto itself. Identity is the day label only — the entry id is held by the page.
 */
export interface CopyJournalData {
  /** The source day (yyyy-MM-dd) being copied — used for the default target + the same-day guard. */
  sourceDate: string;
  /** A friendly label for the source day ("Today" / "Wed Jun 25") for the dialog copy. */
  sourceLabel: string;
}

/** What the dialog resolves with on confirm: the target date (undefined === cancelled). */
export interface CopyJournalResult {
  /** The day (yyyy-MM-dd) to copy the entry onto. */
  targetDate: string;
}

/** Local "yyyy-MM-dd" for a Date (no UTC shift — the journal is local-date keyed). */
function toIso(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

/** Today as a local yyyy-MM-dd — the default target (copying yesterday's entry onto today is the common case). */
function todayIso(): string {
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  return toIso(d);
}

/** A reasonable forward cap (today + 1 year) so the date picker can't wander absurdly far out. */
function maxDate(): string {
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  d.setFullYear(d.getFullYear() + 1);
  return toIso(d);
}

/**
 * "Copy to day" dialog — re-create the caller's OWN journal entry (mood + energy + tags + free-text) onto
 * another day (a COPY: the source day is untouched server-side; the target day is upserted in place). Picks
 * a TARGET DATE (date input, default today, forward-capped) and forbids the source day itself. Resolves with
 * a {@link CopyJournalResult} the page POSTs via the copyJournalEntry endpoint; the page snackbars the result
 * and refreshes if the copy landed on the viewed day. Mirrors the tracker copy-food dialog's chrome (the same
 * --tech-* tokens + the tracker-dialog panel).
 */
@Component({
  selector: 'app-copy-journal-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
  ],
  template: `
    <h2 mat-dialog-title class="cj-title">Copy to another day</h2>
    <mat-dialog-content class="cj-body">
      <p class="cj-intro">
        Copy {{ data.sourceLabel }}'s entry — its mood, energy, tags and notes — onto another day. The
        original stays where it is; the day you pick is overwritten with the same entry.
      </p>

      <mat-form-field appearance="outline" class="cj-field">
        <mat-label>Copy to</mat-label>
        <input
          matInput
          type="date"
          cdkFocusInitial
          [max]="maxDate"
          [ngModel]="targetDate()"
          (ngModelChange)="targetDate.set($event)"
        />
        <mat-hint>{{ targetDate() || '—' }}</mat-hint>
      </mat-form-field>

      @if (isSameDay()) {
        <p class="cj-warn">Pick a different day — you can't copy a day onto itself.</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions class="cj-actions" align="end">
      <button mat-stroked-button type="button" (click)="cancel()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        color="primary"
        [disabled]="!canCopy()"
        (click)="copy()"
      >
        <mat-icon aria-hidden="true">content_copy</mat-icon> Copy
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    .cj-title {
      font-family: var(--tech-font-ui);
      font-weight: 700;
      color: var(--tech-text);
    }
    .cj-body {
      display: flex;
      flex-direction: column;
      gap: var(--tech-space-3);
      min-width: min(340px, 82vw);
      padding-top: 4px !important;
    }
    .cj-intro {
      margin: 0;
      color: var(--tech-text-dim);
      font-size: 0.9rem;
      line-height: 1.4;
    }
    .cj-field {
      width: 100%;
    }
    .cj-warn {
      margin: 0;
      color: var(--tech-warn, #f2b340);
      font-size: 0.82rem;
    }
    .cj-actions {
      padding: var(--tech-space-3) var(--tech-space-4);
      gap: 8px;
      button {
        border-radius: var(--tech-r-control);
        font-weight: 600;
        min-height: 44px;
      }
      mat-icon {
        font-size: 18px;
        height: 18px;
        width: 18px;
        margin-right: 4px;
        vertical-align: text-bottom;
      }
    }
  `,
})
export class CopyJournalDialog {
  private ref = inject(MatDialogRef<CopyJournalDialog, CopyJournalResult>);
  readonly data = inject<CopyJournalData>(MAT_DIALOG_DATA);

  readonly maxDate = maxDate();

  /** Target date — defaults to today, unless the source IS today (then tomorrow), so a valid target is preselected. */
  readonly targetDate = signal<string>(
    this.data.sourceDate === todayIso() ? this.tomorrow() : todayIso(),
  );

  /** True when the chosen target is the source day itself (copy-onto-self is forbidden). */
  readonly isSameDay = computed(() => !!this.targetDate() && this.targetDate() === this.data.sourceDate);

  /** Enabled only with a valid target date that isn't the source day. */
  readonly canCopy = computed(() => !!this.targetDate() && !this.isSameDay());

  copy(): void {
    if (!this.canCopy()) return;
    this.ref.close({ targetDate: this.targetDate() });
  }

  cancel(): void {
    this.ref.close();
  }

  private tomorrow(): string {
    const d = new Date();
    d.setHours(0, 0, 0, 0);
    d.setDate(d.getDate() + 1);
    return toIso(d);
  }
}
