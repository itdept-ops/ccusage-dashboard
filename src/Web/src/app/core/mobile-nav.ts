import { Injectable, signal } from '@angular/core';

/**
 * Shared open/close state for the MOBILE left offcanvas sidebar (the FiMobile `.sidebar-wrap`).
 *
 * The hamburger lives in {@link MobileTopbar} and the drawer is a separate {@link MobileSidebar}
 * component mounted at the shell root — siblings that can't share a template ref, so this tiny
 * signal service mediates. Mirrors the template's `body.menu-open` toggle, minus the cookie.
 */
@Injectable({ providedIn: 'root' })
export class MobileNavService {
  /** Whether the left sidebar drawer is open. */
  readonly sidebarOpen = signal(false);

  open(): void {
    this.sidebarOpen.set(true);
  }
  close(): void {
    this.sidebarOpen.set(false);
  }
  toggle(): void {
    this.sidebarOpen.update((v) => !v);
  }
}
