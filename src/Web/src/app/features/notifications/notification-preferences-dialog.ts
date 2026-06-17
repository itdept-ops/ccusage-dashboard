import { Component, computed, inject, signal } from '@angular/core';

import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatIconModule } from '@angular/material/icon';

import { ChatRealtime } from '../../core/chat-realtime';
import { NotificationPreferenceDto } from '../../core/models';

/** Whether the browser exposes the Notification API at all (false in unsupported/older contexts). */
function browserNotificationsSupported(): boolean {
  return typeof window !== 'undefined' && 'Notification' in window;
}

/**
 * Notification delivery preferences dialog (opened from the bell). Two groups of toggles:
 *   • Triggers  — server-side gate for whether a notification row is created at all.
 *   • Surfaces  — client-side gate for popping an in-app toast / OS notification when one arrives live.
 *
 * Loads the current prefs from {@link ChatRealtime.preferences} (already fetched on connect), edits a
 * local working copy, and persists via {@link ChatRealtime.updatePreferences} (PUT) on Save. Enabling
 * "Browser notifications" calls Notification.requestPermission() inline (a real user gesture); if the
 * browser blocks it the stored preference is kept but a hint explains it won't fire. Available to anyone
 * with chat.read — NOT gated behind settings.* perms.
 */
@Component({
  selector: 'app-notification-preferences-dialog',
  imports: [MatDialogModule, MatButtonModule, MatSlideToggleModule, MatIconModule],
  templateUrl: './notification-preferences-dialog.html',
  styleUrl: './notification-preferences-dialog.scss',
})
export class NotificationPreferencesDialog {
  private chat = inject(ChatRealtime);
  private ref = inject(MatDialogRef<NotificationPreferencesDialog, NotificationPreferenceDto>);

  /** Editable working copy, seeded from the live preferences signal. */
  readonly model = signal<NotificationPreferenceDto>({ ...this.chat.preferences() });

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** True when the platform can't deliver OS notifications at all (API missing). */
  readonly browserSupported = browserNotificationsSupported();

  /** Live browser permission state ('granted' | 'denied' | 'default'); drives the hint under the toggle. */
  readonly permission = signal<NotificationPermission | null>(
    this.browserSupported ? Notification.permission : null,
  );

  /**
   * Hint shown beneath the "Browser notifications" toggle. Only relevant once the user has turned the
   * surface on: explains when the OS won't actually pop a notification despite the saved preference.
   */
  readonly browserHint = computed<string | null>(() => {
    if (!this.model().surfaceBrowser) return null;
    if (!this.browserSupported) return 'This browser does not support desktop notifications.';
    const p = this.permission();
    if (p === 'denied') {
      return 'Your browser is blocking notifications for this site — enable them in site settings to receive them.';
    }
    if (p === 'default') {
      return 'Allow notifications when your browser asks, so alerts can appear while this tab is in the background.';
    }
    return 'Desktop notifications appear only when this tab is in the background.';
  });

  /** Flip a trigger/surface boolean on the working copy. */
  patch<K extends keyof NotificationPreferenceDto>(key: K, value: boolean): void {
    this.model.update(m => ({ ...m, [key]: value }));
  }

  /**
   * Toggle the in-app toast surface. Plain boolean — no permission needed.
   */
  onToastsChange(value: boolean): void {
    this.patch('surfaceToasts', value);
  }

  /**
   * Toggle the browser/OS notification surface. Turning it ON requests OS permission inline (the
   * change handler runs in the user's click gesture). The stored preference follows the toggle either
   * way; {@link browserHint} explains when the browser will actually deliver it.
   */
  async onBrowserChange(value: boolean): Promise<void> {
    this.patch('surfaceBrowser', value);
    if (value && this.browserSupported && Notification.permission === 'default') {
      try {
        const result = await Notification.requestPermission();
        this.permission.set(result);
      } catch {
        // Some browsers reject the promise instead of resolving 'denied'; reflect current state.
        this.permission.set(Notification.permission);
      }
    }
  }

  save(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.chat.updatePreferences(this.model())
      .then(saved => this.ref.close(saved))
      .catch(() => {
        this.busy.set(false);
        this.error.set('Could not save your notification preferences. Please try again.');
      });
  }

  cancel(): void {
    this.ref.close();
  }
}
