import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { FamilyToday, FamilyTodayNote } from '../../../core/models';
import { HearthShell, HearthPhase } from './hearth-shell';

/**
 * Hearth "Pinned notes" glance card — rebuilt on the shared beta-ui foundation. The household's pinned
 * notes (id + title only, per the DTO) as a stacked sticky-note list deep-linking to the live
 * `/family/notes`. Glance data comes from the page-owned `today` snapshot, so this card owns no network; it
 * shows the same skeleton/empty/failed lifecycle driven by the page's `loading`/`failed` inputs.
 */
@Component({
  selector: 'fb-notes-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HearthShell, RouterLink, MatIconModule],
  template: `
    <fb-hearth-shell
      title="Pinned notes" route="/family/notes" icon="push_pin"
      accentA="#fcd34d" accentB="#f59e0b"
      [phase]="phase()" emptyText="Nothing pinned right now." emptyIcon="sticky_note_2">

      @if (notes().length) {
        <span head-trailing class="badge">{{ notes().length }}</span>
      }

      @if (phase() === 'ready') {
        <ul body class="notes">
          @for (n of notes(); track n.id) {
            <li>
              <a class="note" routerLink="/family/notes">
                <span class="note__pin" aria-hidden="true"><mat-icon>sticky_note_2</mat-icon></span>
                <span class="note__title">{{ n.title || 'Untitled note' }}</span>
                <mat-icon class="note__chev" aria-hidden="true">chevron_right</mat-icon>
              </a>
            </li>
          }
        </ul>
      }
    </fb-hearth-shell>
  `,
  styles: [`
    .badge {
      margin-left: auto; font-size: 12px; font-weight: 800; min-width: 22px; text-align: center;
      padding: 3px 9px; border-radius: var(--r-pill);
      background: color-mix(in srgb, #f59e0b 22%, transparent); color: #fcd34d;
    }
    .notes { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    .note {
      display: flex; align-items: center; gap: 12px; min-height: 48px; padding: 8px 12px;
      text-decoration: none; color: var(--ink); border-radius: var(--r-tile);
      background: color-mix(in srgb, #f59e0b 7%, var(--bg-sink)); border: 1px solid var(--hairline);
      transition: transform 120ms var(--ease-out);
    }
    .note:active { transform: scale(.99); }
    .note:focus-visible { outline: 2px solid #f59e0b; outline-offset: 2px; }
    .note__pin {
      flex: 0 0 auto; display: grid; place-items: center; width: 30px; height: 30px; border-radius: 9px;
      background: color-mix(in srgb, #f59e0b 22%, transparent);
    }
    .note__pin mat-icon { color: #fcd34d; font-size: 18px; width: 18px; height: 18px; }
    .note__title { flex: 1 1 auto; font-size: 15px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .note__chev { flex: 0 0 auto; color: var(--ink-faint); font-size: 20px; width: 20px; height: 20px; }
  `],
})
export class NotesCard {
  /** The shared Today snapshot (page-owned, best-effort). */
  readonly today = input<FamilyToday | null>(null);
  /** Whether the shared snapshot is still loading (page-owned). */
  readonly loading = input<boolean>(true);
  /** Whether the shared snapshot load failed (page-owned). */
  readonly failed = input<boolean>(false);

  readonly notes = computed<FamilyTodayNote[]>(() => this.today()?.pinnedNotes ?? []);

  readonly phase = computed<HearthPhase>(() => {
    if (this.loading()) return 'loading';
    if (this.failed()) return 'failed';
    return this.notes().length ? 'ready' : 'empty';
  });
}
