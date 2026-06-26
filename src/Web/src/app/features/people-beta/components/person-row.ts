import {
  ChangeDetectionStrategy, Component, computed, input, output,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { timeAgo } from '../../../shared/format';
import { PersonVm, roleLabel } from '../people-beta.model';

/**
 * Circle person ROW — one rich roster row for the People "Circle" beta surface. A colored, ringed
 * avatar (image or initials fallback) with a live presence dot, the DisplayName-formatted name (NEVER
 * an email), a presence line ("Active now" / "active 5m ago" / "Offline"), relationship chips
 * (Contact / household role), an optional coarse city, and the opt-in status. The whole row is a
 * button that opens the tap-sheet of quick actions; nested in a BetaSwipeRow by the page so a swipe
 * also reveals Message / Nudge.
 *
 * Pure presentation: it takes a {@link PersonVm} + the current clock tick and emits `open` on tap.
 * Reads --accent-a/--accent-b off the page host; the avatar hue is a per-person CSS var so each
 * fallback gets a stable color without leaving the dark contract.
 */
@Component({
  selector: 'app-circle-row',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule],
  template: `
    <button type="button" class="cr" [class.cr--self]="p().isSelf"
            [attr.data-presence]="p().presence" (click)="open.emit()"
            [attr.aria-label]="ariaLabel()">
      <span class="cr__avatar-wrap" [style.--hue]="p().hue">
        @if (p().picture) {
          <img class="cr__avatar" [src]="p().picture" alt="" referrerpolicy="no-referrer" />
        } @else {
          <span class="cr__avatar cr__avatar--init" aria-hidden="true">{{ p().initials }}</span>
        }
        <span class="cr__dot" [attr.data-presence]="p().presence" aria-hidden="true"></span>
      </span>

      <span class="cr__body">
        <span class="cr__top">
          <span class="cr__name">{{ p().name }}</span>
          @if (p().isSelf) { <span class="cr__you">you</span> }
        </span>

        <span class="cr__meta">
          <span class="cr__presence" [attr.data-presence]="p().presence">
            <span class="cr__pdot" [attr.data-presence]="p().presence" aria-hidden="true"></span>
            @switch (p().presence) {
              @case ('online') { Active now }
              @case ('away') { Active {{ timeAgo(p().lastSeenUtc, now()) }} }
              @default { Offline }
            }
          </span>
          @if (p().city) {
            <span class="cr__city"><mat-icon aria-hidden="true">place</mat-icon>{{ p().city }}</span>
          }
        </span>

        @if (p().status) { <span class="cr__status" [title]="p().status!">{{ p().status }}</span> }

        <span class="cr__chips">
          @if (p().isContact) {
            <span class="cr__chip cr__chip--contact"><mat-icon aria-hidden="true">person</mat-icon>Contact</span>
          }
          @if (p().isHousehold) {
            <span class="cr__chip cr__chip--family">
              <mat-icon aria-hidden="true">cottage</mat-icon>{{ p().role ? roleLabel(p().role) : 'Family' }}
            </span>
          }
        </span>
      </span>

      <mat-icon class="cr__chev" aria-hidden="true">chevron_right</mat-icon>
    </button>
  `,
  styleUrl: './person-row.scss',
})
export class CircleRow {
  /** The enriched person to render. */
  readonly p = input.required<PersonVm>();
  /** The current clock tick (ms) so the "active …" label recomputes between refreshes. */
  readonly now = input<number>(Date.now());
  /** Fired when the row is tapped — the page opens the quick-action sheet. */
  readonly open = output<void>();

  protected readonly timeAgo = timeAgo;
  protected readonly roleLabel = roleLabel;

  /** A spoken summary of the row (name + presence + relationship). */
  protected readonly ariaLabel = computed(() => {
    const p = this.p();
    const pres = p.presence === 'online' ? 'online' : p.presence === 'away' ? 'away' : 'offline';
    const rel = p.isContact && p.isHousehold ? 'contact and household'
      : p.isContact ? 'contact' : p.isHousehold ? 'household' : '';
    return `${p.name}${p.isSelf ? ' (you)' : ''}, ${pres}${rel ? ', ' + rel : ''}. Tap for actions.`;
  });
}
