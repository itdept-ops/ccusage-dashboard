import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonToggleModule } from '@angular/material/button-toggle';

import { FamilyPollCreate, FamilyPollKind, FamilyPollOptionInput } from '../../core/models';

/** One draft TIME slot: local "YYYY-MM-DD" date + "HH:mm" start/end. */
interface TimeDraft {
  id: number;
  date: string;
  start: string;
  end: string;
}

/** One draft TEXT option (e.g. "Zoo"). */
interface TextDraft {
  id: number;
  label: string;
}

/**
 * Family Hub F6b — CREATE A POLL. Choose a kind: a TIME poll (add several candidate start/end slots via
 * date + time pickers) or a TEXT poll (add labelled options like "Zoo" / "Beach"). On save we emit a
 * FamilyPollCreate with the local times converted to ISO UTC. Warm + mobile-friendly; no identity here.
 */
@Component({
  selector: 'app-poll-create-dialog',
  imports: [
    FormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatFormFieldModule,
    MatInputModule, MatButtonToggleModule,
  ],
  templateUrl: './poll-create-dialog.html',
  styleUrls: ['./family.scss', './polls.scss'],
})
export class PollCreateDialog {
  readonly ref = inject(MatDialogRef<PollCreateDialog, FamilyPollCreate>);

  private seq = 0;

  readonly title = signal('');
  readonly kind = signal<FamilyPollKind>('time');

  readonly timeDrafts = signal<TimeDraft[]>([this.newTime(), this.newTime()]);
  readonly textDrafts = signal<TextDraft[]>([this.newText(), this.newText()]);

  /** How many valid options the current kind has (a poll needs at least two). */
  readonly validCount = computed(() => {
    if (this.kind() === 'time') return this.timeDrafts().filter(t => this.timeOk(t)).length;
    return this.textDrafts().filter(t => t.label.trim().length > 0).length;
  });

  readonly canSave = computed(() => this.title().trim().length > 0 && this.validCount() >= 2);

  // ---- TIME drafts ----

  private newTime(): TimeDraft {
    const date = this.localDate(new Date());
    return { id: ++this.seq, date, start: '18:00', end: '19:00' };
  }

  addTime(): void {
    if (this.timeDrafts().length >= 30) return;
    this.timeDrafts.update(list => [...list, this.newTime()]);
  }

  removeTime(id: number): void {
    this.timeDrafts.update(list => list.length > 1 ? list.filter(t => t.id !== id) : list);
  }

  setTime(id: number, field: 'date' | 'start' | 'end', value: string): void {
    this.timeDrafts.update(list => list.map(t => t.id === id ? { ...t, [field]: value } : t));
  }

  private timeOk(t: TimeDraft): boolean {
    if (!t.date || !t.start || !t.end) return false;
    const start = new Date(`${t.date}T${t.start}`);
    const end = new Date(`${t.date}T${t.end}`);
    return !Number.isNaN(start.getTime()) && !Number.isNaN(end.getTime()) && end.getTime() > start.getTime();
  }

  // ---- TEXT drafts ----

  private newText(): TextDraft {
    return { id: ++this.seq, label: '' };
  }

  addText(): void {
    if (this.textDrafts().length >= 30) return;
    this.textDrafts.update(list => [...list, this.newText()]);
  }

  removeText(id: number): void {
    this.textDrafts.update(list => list.length > 1 ? list.filter(t => t.id !== id) : list);
  }

  setText(id: number, value: string): void {
    this.textDrafts.update(list => list.map(t => t.id === id ? { ...t, label: value } : t));
  }

  // ---- Save ----

  save(): void {
    if (!this.canSave()) return;
    const kind = this.kind();
    let options: FamilyPollOptionInput[];
    if (kind === 'time') {
      options = this.timeDrafts()
        .filter(t => this.timeOk(t))
        .map(t => ({
          startUtc: new Date(`${t.date}T${t.start}`).toISOString(),
          endUtc: new Date(`${t.date}T${t.end}`).toISOString(),
        }));
    } else {
      options = this.textDrafts()
        .filter(t => t.label.trim().length > 0)
        .map(t => ({ label: t.label.trim() }));
    }
    const payload: FamilyPollCreate = { title: this.title().trim(), kind, options };
    this.ref.close(payload);
  }

  private localDate(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }
}
