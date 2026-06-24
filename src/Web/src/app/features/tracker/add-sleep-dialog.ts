import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { AddSleepRequest, SleepEntryDto } from '../../core/models';

/** Opens with the active (wake) date and, when editing, the existing entry to prefill + replace. */
export interface AddSleepData {
  date: string;
  /** When set, the dialog edits this night: fields prefill and saving deletes-then-re-adds it. */
  edit?: SleepEntryDto;
}

/**
 * What the dialog resolves with so the page can persist + refresh: the sleep request to POST and, when
 * editing, the id of the entry to delete first (the backend has no PATCH — edit = delete + re-add).
 */
export interface AddSleepResult {
  request: AddSleepRequest;
  /** Set when editing: delete this entry before adding the replacement. */
  replaceId?: number;
}

/** The 1..5 quality scale, low→high, for the star picker + its accessible labels. */
const QUALITY_LABELS = ['', 'Poor', 'Fair', 'OK', 'Good', 'Great'];

/**
 * Log-a-night-of-sleep dialog (the slimmed, classic single-entry twin of the coffee dialog). Enters hours
 * slept + a 1..5 quality rating (star picker) plus optional bedtime / wake time and a short note. The
 * night maps to the WAKE date the caller is viewing. Sleep is OWNER-ONLY, so there's no AI affordance and
 * no sharing. Resolves with an {@link AddSleepResult} for the page to log; cancel resolves with undefined.
 */
@Component({
  selector: 'app-add-sleep-dialog',
  imports: [
    FormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
  ],
  template: `
    <h2 mat-dialog-title class="sl-title">{{ data.edit ? 'Edit sleep' : 'Log sleep' }}</h2>
    <mat-dialog-content class="sl-body">
      <mat-form-field appearance="outline" class="sl-field">
        <mat-label>Hours slept</mat-label>
        <input
          matInput
          type="number"
          min="0"
          max="24"
          step="0.5"
          inputmode="decimal"
          cdkFocusInitial
          [ngModel]="hours()"
          (ngModelChange)="hours.set($event)"
        />
        <span matTextSuffix>h</span>
        <mat-hint>For the night you woke on {{ data.date }}.</mat-hint>
      </mat-form-field>

      <div class="sl-quality" role="group" aria-label="Sleep quality, 1 to 5">
        <span class="sl-quality-label">Quality</span>
        <div class="sl-stars">
          @for (n of stars; track n) {
            <button
              mat-icon-button
              type="button"
              class="sl-star"
              [class.sl-star--on]="n <= quality()"
              (click)="quality.set(n)"
              [attr.aria-pressed]="n <= quality()"
              [attr.aria-label]="n + ' of 5, ' + qualityLabels[n]"
            >
              <mat-icon>{{ n <= quality() ? 'star' : 'star_border' }}</mat-icon>
            </button>
          }
          <span class="sl-quality-word">{{ qualityLabels[quality()] }}</span>
        </div>
      </div>

      <div class="sl-times" role="group" aria-label="Bed and wake times (optional)">
        <mat-form-field appearance="outline" class="sl-time">
          <mat-label>Bedtime (optional)</mat-label>
          <input matInput type="time" [ngModel]="bedTime()" (ngModelChange)="bedTime.set($event)" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="sl-time">
          <mat-label>Wake time (optional)</mat-label>
          <input
            matInput
            type="time"
            [ngModel]="wakeTime()"
            (ngModelChange)="wakeTime.set($event)"
          />
        </mat-form-field>
      </div>

      <mat-form-field appearance="outline" class="sl-field">
        <mat-label>Note (optional)</mat-label>
        <input
          matInput
          type="text"
          maxlength="200"
          placeholder="Restless, woke at 3am, slept great…"
          [ngModel]="note()"
          (ngModelChange)="note.set($event)"
        />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions class="sl-actions" align="end">
      <button mat-stroked-button type="button" (click)="cancel()">Cancel</button>
      <button
        mat-flat-button
        type="button"
        color="primary"
        [disabled]="!canSave()"
        (click)="save()"
      >
        {{ data.edit ? 'Save' : 'Log' }}
      </button>
    </mat-dialog-actions>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    .sl-title {
      font-family: var(--tech-font-ui);
      font-weight: 700;
      color: var(--tech-text);
    }
    .sl-body {
      min-width: min(380px, 84vw);
      padding-top: 4px !important;
      display: flex;
      flex-direction: column;
      gap: var(--tech-space-2);
    }
    .sl-field {
      width: 100%;
    }
    .sl-actions {
      padding: var(--tech-space-3) var(--tech-space-4);
      gap: 8px;
      button {
        border-radius: var(--tech-r-control);
        font-weight: 600;
        min-height: 44px;
      }
    }

    .sl-quality {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .sl-quality-label {
      font-size: var(--tech-fs-label);
      color: var(--tech-text-secondary);
    }
    .sl-stars {
      display: flex;
      align-items: center;
      gap: 2px;
    }
    .sl-star {
      color: var(--tech-text-tertiary);
      mat-icon {
        font-size: 24px;
        width: 24px;
        height: 24px;
      }
    }
    .sl-star--on {
      color: var(--tech-accent);
    }
    .sl-quality-word {
      margin-left: 8px;
      font-size: var(--tech-fs-label);
      color: var(--tech-text-secondary);
    }

    .sl-times {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: var(--tech-space-2);
    }
    .sl-time {
      width: 100%;
    }
  `,
})
export class AddSleepDialog {
  private ref = inject(MatDialogRef<AddSleepDialog, AddSleepResult>);
  readonly data = inject<AddSleepData>(MAT_DIALOG_DATA);

  readonly stars = [1, 2, 3, 4, 5] as const;
  readonly qualityLabels = QUALITY_LABELS;

  readonly hours = signal<number | null>(this.data.edit?.hours ?? 8);
  readonly quality = signal<number>(this.data.edit?.quality ?? 3);
  readonly bedTime = signal<string>(this.data.edit?.bedTime ?? '');
  readonly wakeTime = signal<string>(this.data.edit?.wakeTime ?? '');
  readonly note = signal<string>(this.data.edit?.note ?? '');

  /** Hours clamped to the server's [0, 24] range, rounded to 1dp, else null. */
  private readonly validHours = computed<number | null>(() => {
    const h = this.hours();
    if (h == null) return null;
    const n = Math.round(h * 10) / 10;
    return n >= 0 && n <= 24 ? n : null;
  });

  readonly canSave = computed(() => this.validHours() != null);

  save(): void {
    const hours = this.validHours();
    if (hours == null) return;
    const bedTime = this.bedTime().trim();
    const wakeTime = this.wakeTime().trim();
    const note = this.note().trim();
    this.ref.close({
      request: {
        date: this.data.date,
        hours,
        quality: this.quality(),
        bedTime: bedTime || undefined,
        wakeTime: wakeTime || undefined,
        note: note || undefined,
      },
      replaceId: this.data.edit?.id,
    });
  }

  cancel(): void {
    this.ref.close();
  }
}
