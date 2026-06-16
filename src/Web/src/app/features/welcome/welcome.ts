import { Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';

import { AuthService } from '../../core/auth';
import { PERM } from '../../core/models';

interface AccessiblePage {
  path: string;
  label: string;
  icon: string;
  blurb: string;
}

/**
 * Authenticated landing page for users who have signed in but have no (or limited) page access —
 * e.g. a freshly auto-provisioned account awaiting an admin's approval. Greets the user, lists the
 * pages they can actually open, and offers sign-out. Rendered inside the app shell (NOT bare).
 */
@Component({
  selector: 'app-welcome',
  imports: [RouterLink, MatIconModule, MatButtonModule],
  templateUrl: './welcome.html',
  styleUrl: './welcome.scss',
})
export class Welcome {
  readonly auth = inject(AuthService);
  private router = inject(Router);

  /** The display name (or email) of the signed-in user. */
  readonly displayName = computed(() => {
    const s = this.auth.session();
    return s?.name?.trim() || s?.email || 'there';
  });

  /** The catalog of pages, paired with the view permission that unlocks each. */
  private static readonly pages: { perm: string; page: AccessiblePage }[] = [
    { perm: PERM.dashboardView, page: { path: '/', label: 'Dashboard', icon: 'space_dashboard', blurb: 'Token usage, cost and records.' } },
    { perm: PERM.calendarView, page: { path: '/calendar', label: 'Calendar', icon: 'calendar_month', blurb: 'Activity heatmap and session drill-down.' } },
    { perm: PERM.pricingView, page: { path: '/pricing', label: 'Pricing', icon: 'sell', blurb: 'Per-model rate table.' } },
    { perm: PERM.settingsView, page: { path: '/settings', label: 'Settings', icon: 'tune', blurb: 'Timezone, sources and sync.' } },
    { perm: PERM.reporterView, page: { path: '/reporter', label: 'Reporter', icon: 'cable', blurb: 'Ingest keys and reporter setup.' } },
    { perm: PERM.usersView, page: { path: '/users', label: 'Users', icon: 'group', blurb: 'User list and access control.' } },
    { perm: PERM.activityView, page: { path: '/activity', label: 'Activity', icon: 'monitoring', blurb: 'Request logs.' } },
  ];

  /** Pages the current user can actually open, in nav order. */
  readonly pages = computed<AccessiblePage[]>(() =>
    Welcome.pages.filter(p => this.auth.hasPermission(p.perm)).map(p => p.page),
  );

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
