import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';

import { Api } from '../../../core/api';
import { Household, HouseholdMember, Presence } from '../../../core/models';
import { HearthShell, HearthPhase } from './hearth-shell';

const ONLINE_WINDOW_MS = 5 * 60_000;

/** A household member decorated with a derived online flag (from presence last-seen). */
interface MemberChip {
  userId: number;
  name: string;
  picture?: string | null;
  isSelf: boolean;
  online: boolean;
  /** The member's latest coarse city (only ever populated when they share to household). */
  city?: string | null;
}

/**
 * Hearth "Who's home" glance card — rebuilt on the shared beta-ui foundation. The household's members as
 * an OVERLAPPING presence-avatar stack (online members lead, each with a live online dot), then a roster
 * row of named chips with a pulsing "online now" count — the canonical Atrium presence-widget pattern.
 * BOTH streams load best-effort and INDEPENDENTLY (each its own `catchError(of(null))`): if presence fails
 * we still render the members with no dots; if the household fails the card shows its failed/retry state.
 * Identity is name + picture (+ a coarse shared city) only — NEVER an email. Deep-links to the family map.
 *
 * `initials` is a small COPIED helper (no live import).
 */
@Component({
  selector: 'fb-household-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HearthShell],
  template: `
    <fb-hearth-shell
      title="Who's home" route="/family/locations" icon="diversity_3"
      accentA="#34d399" accentB="#22d3ee"
      [phase]="phase()" emptyText="No household members yet." emptyIcon="groups"
      (retry)="reload()">

      @if (onlineCount()) {
        <span head-trailing class="badge">
          <span class="badge__pulse" aria-hidden="true"></span>{{ onlineCount() }} online
        </span>
      }

      @if (phase() === 'ready') {
        <div body class="who">
          <div class="who__stack">
            @for (m of chips(); track m.userId; let i = $index) {
              <span class="av" [class.av--self]="m.isSelf" [style.z-index]="chips().length - i"
                    [title]="m.name + (m.isSelf ? ' (you)' : '')">
                @if (m.picture) {
                  <img class="av__img" [src]="m.picture" [alt]="m.name" referrerpolicy="no-referrer" />
                } @else {
                  <span class="av__init" aria-hidden="true">{{ initials(m.name) }}</span>
                }
                @if (m.online) { <span class="av__dot" [attr.aria-label]="m.name + ' online'"></span> }
              </span>
            }
          </div>

          <ul class="who__roster">
            @for (m of chips(); track m.userId) {
              <li class="who__row" [class.who__row--on]="m.online">
                <span class="who__name">{{ m.name }}@if (m.isSelf) { <span class="who__you">you</span> }</span>
                <span class="who__state">
                  @if (m.online) {
                    <span class="who__live"><span class="who__live-dot" aria-hidden="true"></span>{{ m.city || 'Online' }}</span>
                  } @else { <span class="who__away">{{ m.city || 'Away' }}</span> }
                </span>
              </li>
            }
          </ul>
        </div>
      }
    </fb-hearth-shell>
  `,
  styles: [`
    .badge {
      margin-left: auto; display: inline-flex; align-items: center; gap: 6px;
      font-size: 12px; font-weight: 800; padding: 3px 10px 3px 9px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--signal) 18%, transparent); color: var(--signal);
    }
    .badge__pulse {
      width: 7px; height: 7px; border-radius: 50%; background: var(--signal);
      box-shadow: 0 0 0 0 color-mix(in srgb, var(--signal) 60%, transparent);
      animation: who-pulse 2s var(--ease-out) infinite;
    }

    .who { display: flex; flex-direction: column; gap: 14px; }
    .who__stack { display: flex; align-items: center; }
    .av {
      position: relative; width: 44px; height: 44px; flex: 0 0 auto; margin-left: -12px;
      border-radius: 50%; box-shadow: 0 0 0 2px var(--bg-rise);
    }
    .av:first-child { margin-left: 0; }
    .av--self { box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-a) 70%, var(--bg-rise)); }
    .av__img, .av__init { width: 44px; height: 44px; border-radius: 50%; object-fit: cover; display: grid; place-items: center; }
    .av__init {
      background: linear-gradient(135deg, color-mix(in srgb, #34d399 28%, var(--bg-sink)), color-mix(in srgb, #22d3ee 28%, var(--bg-sink)));
      color: var(--ink); font-family: var(--font-ui); font-size: 15px; font-weight: 700;
    }
    .av__dot {
      position: absolute; right: 0; bottom: 0; width: 12px; height: 12px; border-radius: 50%;
      background: var(--signal); box-shadow: 0 0 0 2.5px var(--bg-rise);
    }

    .who__roster { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; }
    .who__row {
      display: flex; align-items: center; justify-content: space-between; gap: 12px;
      min-height: 40px; padding: 6px 0;
    }
    .who__row + .who__row { border-top: 1px solid var(--hairline); }
    .who__name { font-size: 15px; font-weight: 600; color: var(--ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .who__you {
      margin-left: 6px; font-size: 10px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase;
      color: color-mix(in srgb, var(--accent-a) 80%, var(--ink)); vertical-align: middle;
    }
    .who__state { flex: 0 0 auto; font-size: 12px; font-weight: 700; }
    .who__live { display: inline-flex; align-items: center; gap: 5px; color: var(--signal); }
    .who__live-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--signal); }
    .who__away { color: var(--ink-faint); }

    @keyframes who-pulse {
      0% { box-shadow: 0 0 0 0 color-mix(in srgb, var(--signal) 55%, transparent); }
      70% { box-shadow: 0 0 0 7px transparent; }
      100% { box-shadow: 0 0 0 0 transparent; }
    }
    @media (prefers-reduced-motion: reduce) { .badge__pulse { animation: none; } }
  `],
})
export class HouseholdCard {
  private readonly api = inject(Api);
  private readonly destroyRef = inject(DestroyRef);

  private readonly household = signal<Household | null>(null);
  private readonly presenceList = signal<Presence[] | null>(null);
  private readonly failed = signal(false);
  private readonly loadingState = signal(true);

  private readonly members = computed<HouseholdMember[]>(() => this.household()?.members ?? []);

  /** Member ids seen online (presence within the window, matched by userId) + their coarse city. */
  private readonly onlineInfo = computed<ReadonlyMap<number, { city?: string | null }>>(() => {
    const now = Date.now();
    const map = new Map<number, { city?: string | null }>();
    for (const p of this.presenceList() ?? []) {
      if (p.userId != null && now - Date.parse(p.lastSeenUtc) < ONLINE_WINDOW_MS) map.set(p.userId, { city: p.city });
    }
    return map;
  });

  readonly chips = computed<MemberChip[]>(() => {
    const online = this.onlineInfo();
    return this.members()
      .map(m => ({
        userId: m.userId, name: m.name, picture: m.picture, isSelf: m.isSelf,
        online: online.has(m.userId), city: online.get(m.userId)?.city,
      }))
      // Online members lead the overlapping stack + roster.
      .sort((a, b) => Number(b.online) - Number(a.online));
  });

  readonly onlineCount = computed(() => this.chips().filter(c => c.online).length);

  readonly phase = computed<HearthPhase>(() => {
    if (this.loadingState()) return 'loading';
    if (this.failed()) return 'failed';
    return this.members().length ? 'ready' : 'empty';
  });

  /** COPIED helper — first letters of up to two name words. */
  initials(name: string): string {
    return name.trim().split(/\s+/).slice(0, 2).map(w => w[0]?.toUpperCase() ?? '').join('') || '?';
  }

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loadingState.set(true);
    this.failed.set(false);
    // Household drives the card's lifecycle (failed/retry hinges on it).
    this.api.getHousehold()
      .pipe(catchError(() => { this.failed.set(true); return of<Household | null>(null); }), takeUntilDestroyed(this.destroyRef))
      .subscribe(h => {
        if (h) this.household.set(h);
        this.loadingState.set(false);
      });
    // Presence is purely decorative — a failure just means no dots, never blanks the card.
    this.api.presence()
      .pipe(catchError(() => of<Presence[] | null>(null)), takeUntilDestroyed(this.destroyRef))
      .subscribe(list => { if (list) this.presenceList.set(list); });
  }
}
