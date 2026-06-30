import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { AuthService } from '../../../core/auth';
import { SnapRouteService } from '../../../core/snap-route';
import { bottomTabs, type NavItem } from '../../../core/nav-model';

/**
 * The GLOBAL mobile BOTTOM TAB BAR — mirrors the FiMobile template footer: a row of nav tabs with a
 * RAISED CENTER action button (the rear-camera "Snap & Route" capture). Tabs are split evenly on
 * either side of the center pod, with Chat pushed to the right so the camera sits dead-center.
 *
 * NAV IS DERIVED: the tabs read {@link bottomTabs} over PAGE_REGISTRY (core/nav-model), gated by the
 * caller's permissions. The old "More" sheet is gone — every other destination + the account/session
 * rows now live in the left offcanvas sidebar ({@link MobileSidebar}, opened from the top bar).
 *
 * CONTRACT: selector `app-bottom-tab-bar` — mount once, only when on mobile.
 */
@Component({
  selector: 'app-bottom-tab-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './bottom-tab-bar.scss',
  imports: [RouterLink, RouterLinkActive, MatIconModule],
  template: `
    <nav class="tabbar" aria-label="Primary">
      @for (t of leftTabs(); track t.id) {
        <a class="tab" [routerLink]="t.path" routerLinkActive="active"
           [attr.data-tour]="'tab-' + t.id"
           #rlaTab="routerLinkActive"
           [routerLinkActiveOptions]="t.path === '/' ? exact : nonExact"
           [attr.aria-current]="rlaTab.isActive ? 'page' : null"
           [attr.aria-label]="t.label">
          <span class="tab__icon"><mat-icon aria-hidden="true">{{ t.icon }}</mat-icon></span>
          <span class="tab__label">{{ t.label }}</span>
        </a>
      }

      <!-- The raised CENTER action button — the rear-camera Snap & Route capture (FiMobile center button).
           Shown only when the caller can capture (ai.vision + ≥1 writable destination). -->
      @if (canSnap()) {
        <button type="button" class="centerbtn" data-tour="snap-fab" (click)="snap()"
                aria-label="Snap a photo and route it">
          <span class="centerbtn__pod"><mat-icon aria-hidden="true">photo_camera</mat-icon></span>
        </button>
      }

      @for (t of rightTabs(); track t.id) {
        <a class="tab" [routerLink]="t.path" routerLinkActive="active"
           [attr.data-tour]="'tab-' + t.id"
           #rlaTab2="routerLinkActive"
           [routerLinkActiveOptions]="t.path === '/' ? exact : nonExact"
           [attr.aria-current]="rlaTab2.isActive ? 'page' : null"
           [attr.aria-label]="t.label">
          <span class="tab__icon"><mat-icon aria-hidden="true">{{ t.icon }}</mat-icon></span>
          <span class="tab__label">{{ t.label }}</span>
        </a>
      }
    </nav>
  `,
})
export class BottomTabBar {
  private readonly auth = inject(AuthService);
  private readonly snapRoute = inject(SnapRouteService);

  /** Whether to show the raised center camera (ai.vision + ≥1 writable destination; reactive to /me). */
  protected readonly canSnap = this.snapRoute.canCapture;

  /** Open the OS rear camera → classify → route-review via the shared Snap & Route orchestrator. */
  protected snap(): void {
    this.snapRoute.request();
  }

  protected readonly exact = { exact: true } as const;
  protected readonly nonExact = { exact: false } as const;

  private readonly has = (p: string): boolean => this.auth.hasPermission(p);

  /** All accessible bottom tabs, reordered so Chat is RIGHTMOST (camera sits centered). */
  private readonly tabs = computed<NavItem[]>(() => {
    this.auth.permissions(); // reactive dependency
    const all = bottomTabs(this.has);
    const chat = all.filter((t) => t.path === '/chat');
    const rest = all.filter((t) => t.path !== '/chat');
    return [...rest, ...chat];
  });

  /** Split point: with a center camera, balance the tabs on each side; chat lands on the right. */
  private readonly splitAt = computed(() => {
    const n = this.tabs().length;
    return this.canSnap() ? Math.ceil(n / 2) : n; // no center → all tabs in one row
  });

  protected readonly leftTabs = computed(() => this.tabs().slice(0, this.splitAt()));
  protected readonly rightTabs = computed(() => this.tabs().slice(this.splitAt()));
}
