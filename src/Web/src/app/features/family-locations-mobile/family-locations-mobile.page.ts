import {
  ChangeDetectionStrategy, Component, OnDestroy, computed, inject, signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { MatIconModule } from '@angular/material/icon';

import { Api } from '../../core/api';
import { FamilyMemberLocation } from '../../core/models';
import { timeAgo } from '../../shared/format';
import { LocationMap, type MapPin } from '../location/location-map';
import { BetaSkeleton } from '../beta-ui';

/**
 * Family Locations "Where is everyone" — the mobile-first twin of the live /family/locations page
 * (gated `platform.mobile` + the SAME `family.use` the live route carries). It re-presents the exact
 * same family-finder data for a phone: a FULL-BLEED Leaflet/OSM map fills the screen, with a glassy,
 * draggable-feel member sheet docked over its bottom edge listing every opted-in household member —
 * name, place, "as of <relative time>", a muted "stale" badge past a few hours, and the caller's own
 * row distinguished as "You". Tapping a row focuses (emphasises) that member's pin and recentres; a
 * pin tap selects its row. A refresh button re-fetches + re-bases the "as of" clock.
 *
 * DATA PARITY + PRIVACY: every row comes straight from the SAME endpoint the live page uses —
 * {@link Api.familyLocations} (GET /api/family/locations), which resolves the caller's own household
 * server-side and only surfaces members who opted into household sharing AND have a recent fix. Identity
 * on the wire is userId + DISPLAY NAME only; no email is ever rendered. When nobody has shared yet, a
 * friendly empty state points members at their My-locations page to opt in.
 *
 * ISOLATION: imports only the shared {@link Api}/{@link FamilyMemberLocation} DTO, the shared
 * {@link LocationMap} (which lazy-loads Leaflet via the shared leaflet-loader so the lib never bloats the
 * main bundle), the `timeAgo` helper, and the beta-ui kit ({@link BetaSkeleton}). It never imports or
 * touches the live page, the page-registry, or app shell, and adds no npm deps. Mobile-first: the map +
 * sheet own the immersive column under the global slim top bar + bottom tab bar; the sheet is
 * `position:absolute bottom:0` INSIDE the host so it clears the ~62px global tab bar. The screenshot
 * harness mocks the API, so loading/empty/error all render cleanly with ZERO data.
 */
@Component({
  selector: 'app-family-locations-mobile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatIconModule, LocationMap, BetaSkeleton],
  template: `
    <div class="fl">
      <!-- ─────────── FULL-BLEED MAP ─────────── -->
      <div class="fl-map" [class.is-dim]="loading() || errored() || !members().length">
        @if (members().length) {
          <app-location-map [pins]="pins()" (pinClick)="onPinClick($event)" />
        } @else {
          <!-- No pins to plot yet — a calm placeholder behind the sheet (still themed map chrome). -->
          <div class="fl-map__placeholder" aria-hidden="true">
            <mat-icon>public</mat-icon>
          </div>
        }
      </div>

      <!-- ─────────── FLOATING HEADER (over the map) ─────────── -->
      <header class="fl-head">
        <div class="fl-head__text">
          <span class="fl-head__eyebrow"><mat-icon aria-hidden="true">person_pin_circle</mat-icon> Family</span>
          <h1 class="fl-head__title">Where is everyone</h1>
        </div>
        <button type="button" class="fl-head__refresh" (click)="refresh()"
                [class.is-spinning]="loading()" [disabled]="loading()"
                aria-label="Refresh locations">
          <mat-icon aria-hidden="true">refresh</mat-icon>
        </button>
      </header>

      <!-- ─────────── DOCKED MEMBER SHEET (absolute bottom, clears the global tab bar) ─────────── -->
      <section class="fl-sheet" aria-label="People sharing their location" aria-live="polite">
        <div class="fl-sheet__grip" aria-hidden="true"></div>

        @if (loading()) {
          <!-- SKELETON rows -->
          <div class="fl-sheet__head">
            <app-bs-skeleton width="46%" height="16px" radius="6px" />
          </div>
          <ul class="fl-list">
            @for (n of skeletonCells; track n) {
              <li class="fl-row fl-row--skel">
                <app-bs-skeleton width="40px" height="40px" radius="50%" />
                <div class="fl-row__skel-lines">
                  <app-bs-skeleton width="55%" height="13px" radius="5px" />
                  <app-bs-skeleton width="78%" height="11px" radius="5px" />
                </div>
              </li>
            }
          </ul>

        } @else if (errored()) {
          <!-- ERROR -->
          <div class="fl-state">
            <span class="fl-state__orb"><mat-icon aria-hidden="true">cloud_off</mat-icon></span>
            <h2 class="fl-state__title">Couldn't load locations</h2>
            <p class="fl-state__body">Something went wrong reaching the family finder. Give it another go.</p>
            <button type="button" class="fl-state__cta" (click)="refresh()">
              <mat-icon aria-hidden="true">refresh</mat-icon> Try again
            </button>
          </div>

        } @else if (!members().length) {
          <!-- EMPTY -->
          <div class="fl-state">
            <span class="fl-state__orb"><mat-icon aria-hidden="true">location_off</mat-icon></span>
            <h2 class="fl-state__title">Nobody's sharing yet</h2>
            <p class="fl-state__body">
              When a household member opts into sharing their location, they'll show up on the map here.
            </p>
            <a class="fl-state__cta" routerLink="/locations">
              <mat-icon aria-hidden="true">my_location</mat-icon> Share my location
            </a>
          </div>

        } @else {
          <div class="fl-sheet__head">
            <span class="fl-sheet__count">
              <span class="mono-num">{{ members().length }}</span>
              {{ members().length === 1 ? 'person' : 'people' }} sharing
            </span>
          </div>
          <ul class="fl-list">
            @for (m of members(); track m.userId) {
              <li class="fl-row" [class.is-self]="m.isSelf"
                  [class.is-selected]="m.userId === selectedId()">
                <button type="button" class="fl-row__btn" (click)="select(m.userId)"
                        [attr.aria-pressed]="m.userId === selectedId()"
                        [attr.aria-label]="rowAria(m)">
                  <span class="fl-row__avatar" [style.--swatch]="colorFor(m)" aria-hidden="true">
                    {{ initials(m.name) }}
                  </span>
                  <span class="fl-row__body">
                    <span class="fl-row__name">
                      {{ m.name }}
                      @if (m.isSelf) { <span class="fl-row__you">You</span> }
                    </span>
                    <span class="fl-row__meta">
                      <mat-icon class="fl-row__pin" aria-hidden="true">place</mat-icon>
                      {{ placeLabel(m) }}
                    </span>
                    <span class="fl-row__when">
                      {{ timeAgo(m.capturedUtc, now()) }}
                      @if (isStale(m)) { <span class="fl-row__stale">stale</span> }
                    </span>
                  </span>
                  <mat-icon class="fl-row__go" aria-hidden="true">my_location</mat-icon>
                </button>
              </li>
            }
          </ul>
          <p class="fl-foot">
            <mat-icon aria-hidden="true">shield</mat-icon>
            Only members who opted in appear. Powered by OpenStreetMap.
          </p>
        }
      </section>
    </div>
  `,
  styleUrl: './family-locations-mobile.page.scss',
})
export class FamilyLocationsMobilePage implements OnDestroy {
  private api = inject(Api);

  readonly timeAgo = timeAgo;

  readonly members = signal<FamilyMemberLocation[]>([]);
  readonly loading = signal(true);
  readonly errored = signal(false);
  readonly now = signal(Date.now());

  /** The focused member id (a tapped row or pin); null = show all. */
  readonly selectedId = signal<number | null>(null);

  readonly skeletonCells = Array.from({ length: 4 }, (_, i) => i);

  /** Advances now() while alive so "as of" times + the stale badge don't freeze. */
  private clockTimer: ReturnType<typeof setInterval> | null = null;
  private static readonly CLOCK_MS = 60_000;

  /** A pin older than this (a few hours) reads muted-"stale" in the list. */
  private static readonly STALE_MS = 3 * 60 * 60 * 1000;

  /** A small deterministic palette so each member reads apart. */
  private static readonly PALETTE = [
    '#3fd8d0', '#8b7cff', '#f0a020', '#ef5d8f',
    '#5b8def', '#5bbf6a', '#d98b3f', '#b06be0',
  ];

  /** One map pin per member; the caller (and any selected member) is emphasised. */
  readonly pins = computed<MapPin[]>(() => {
    const sel = this.selectedId();
    const n = this.now();
    return this.members().map((m) => ({
      id: `u:${m.userId}`,
      lat: m.lat,
      lng: m.lng,
      title: m.isSelf ? `${m.name} (you)` : m.name,
      subtitle: `${this.placeLabel(m)} · ${timeAgo(m.capturedUtc, n)}`,
      kind: 'user' as const,
      emphasis: m.isSelf || m.userId === sel,
    }));
  });

  constructor() {
    void this.load();
    this.clockTimer = setInterval(
      () => this.now.set(Date.now()),
      FamilyLocationsMobilePage.CLOCK_MS,
    );
  }

  ngOnDestroy(): void {
    if (this.clockTimer) {
      clearInterval(this.clockTimer);
      this.clockTimer = null;
    }
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.errored.set(false);
    this.now.set(Date.now());
    try {
      const rows = await firstValueFrom(this.api.familyLocations());
      this.members.set(rows ?? []);
    } catch {
      this.errored.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  /** Re-fetch (and refresh the "as of" clock). */
  refresh(): void {
    void this.load();
  }

  /** Focus a member from the list or a pin; tapping the focused one again clears it. */
  select(userId: number): void {
    this.selectedId.update((cur) => (cur === userId ? null : userId));
  }

  /** Map pin-click handler: pin ids are "u:<userId>". */
  onPinClick(id: string): void {
    if (id.startsWith('u:')) this.select(+id.slice(2));
  }

  rowAria(m: FamilyMemberLocation): string {
    const who = m.isSelf ? `${m.name}, you` : m.name;
    return `${who}, ${this.placeLabel(m)}, as of ${timeAgo(m.capturedUtc, this.now())}. Focus on the map.`;
  }

  /** A stable accent colour for a member's avatar swatch. */
  colorFor(m: FamilyMemberLocation): string {
    const len = FamilyLocationsMobilePage.PALETTE.length;
    const i = ((m.userId % len) + len) % len;
    return FamilyLocationsMobilePage.PALETTE[i];
  }

  /** Two-letter initials for the swatch (display name only; never an email). */
  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  /** City-first place label, falling back to coarse coords if not reverse-geocoded. */
  placeLabel(m: FamilyMemberLocation): string {
    const parts = [m.city, m.region, m.country].filter(Boolean);
    return parts.length ? parts.join(', ') : `${m.lat.toFixed(3)}, ${m.lng.toFixed(3)}`;
  }

  /** True when the latest fix is older than the stale window. */
  isStale(m: FamilyMemberLocation): boolean {
    return this.now() - new Date(m.capturedUtc).getTime() > FamilyLocationsMobilePage.STALE_MS;
  }
}
