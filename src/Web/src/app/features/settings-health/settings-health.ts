import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { HealthStatus, HealthSettingsPatch, HealthSyncNowResult } from '../../core/models';
import { beginFitbitAuthorize, consumePendingFitbitCode } from './fitbit-oauth';

/** One per-signal toggle row's static metadata. */
interface SignalToggle {
  key: 'syncSteps' | 'syncSleep' | 'syncHeartRate' | 'syncWorkouts';
  label: string;
  icon: string;
  hint: string;
}

/**
 * Settings · Wearable health sync (route: settings/health, gated `health.sync`). The owner-scoped surface
 * to connect a Fitbit (v1) so steps/active-calories, sleep, resting-HR and workouts auto-flow into the
 * tracker — so the user stops typing them. Provider-agnostic shell (Fitbit today; Oura slots in later).
 *
 * FLOW: GET /api/health/status drives the whole screen. Three top-level states —
 *   1. NOT CONFIGURED  (`configured:false`) — a calm "not set up on this server" notice; nothing to do.
 *   2. NOT CONNECTED   (`connected:false`) — a "Connect Fitbit" CTA that kicks the OAuth 2.0 auth-code +
 *      PKCE flow ({@link beginFitbitAuthorize}: stash a fresh code_verifier, redirect to Fitbit). On return
 *      the page reads `?code` ({@link consumePendingFitbitCode}) and POSTs /connect to finish the exchange.
 *   3. CONNECTED       — provider + last-sync status, the per-signal toggles (PATCH /settings on change),
 *      a "Sync now" button (POST /sync-now → the imported/updated/skipped summary) and Disconnect.
 *
 * PRIVACY: sleep + resting-HR are written owner-only server-side and never surface to coach/family overlays;
 * the client never sees the refresh token or client secret. The OAuth client id is public by design.
 */
@Component({
  selector: 'app-settings-health',
  imports: [
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSlideToggleModule,
  ],
  templateUrl: './settings-health.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './settings-health.scss',
})
export class SettingsHealth {
  private api = inject(Api);
  private snack = inject(MatSnackBar);

  readonly status = signal<HealthStatus | null>(null);
  readonly loading = signal(true);
  readonly errored = signal(false);
  /** True while the OAuth callback `?code` is being exchanged on /connect. */
  readonly connecting = signal(false);
  readonly syncing = signal(false);
  readonly disconnecting = signal(false);
  /** Set ID of the toggle mid-save (disables its row); null when idle. */
  readonly savingKey = signal<string | null>(null);
  readonly result = signal<HealthSyncNowResult | null>(null);

  readonly signalToggles: readonly SignalToggle[] = [
    { key: 'syncSteps', label: 'Steps & active calories', icon: 'directions_walk',
      hint: 'Daily steps, distance and active-calorie burn flow into your activity.' },
    { key: 'syncSleep', label: 'Sleep', icon: 'bedtime',
      hint: 'Each night logs against your wake date. Private to you.' },
    { key: 'syncHeartRate', label: 'Resting heart rate', icon: 'favorite',
      hint: 'Your daily resting HR. Sensitive — kept owner-only.' },
    { key: 'syncWorkouts', label: 'Workouts', icon: 'fitness_center',
      hint: 'Recorded exercises become tracker workout entries.' },
  ];

  /** "Reconnect" prompt — the saved token died (the server reports an auth-expired last sync). */
  readonly authExpired = computed(() => {
    const s = this.status();
    return !!s?.connected && s.lastSyncStatus === 'AuthExpired';
  });

  readonly lastSyncLabel = computed(() => {
    const iso = this.status()?.lastSyncUtc;
    if (!iso) return 'Never';
    const d = new Date(iso);
    return isNaN(d.getTime()) ? 'Never' : d.toLocaleString();
  });

  readonly statusTone = computed<'ok' | 'warn' | 'idle'>(() => {
    const s = this.status();
    if (!s?.connected) return 'idle';
    switch (s.lastSyncStatus) {
      case 'Ok': return 'ok';
      case 'AuthExpired':
      case 'RateLimited':
      case 'Error': return 'warn';
      default: return 'idle';
    }
  });

  constructor() {
    void this.init();
  }

  // ─────────────── LOAD + OAuth callback ───────────────

  private async init(): Promise<void> {
    // If we're returning from Fitbit with ?code, finish the exchange BEFORE the first status read,
    // so the page lands directly on the connected state.
    const pending = consumePendingFitbitCode();
    if (pending) {
      await this.finishConnect(pending.code, pending.redirectUri, pending.verifier);
      return;
    }
    await this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.errored.set(false);
    try {
      this.status.set(await firstValueFrom(this.api.healthStatus()));
    } catch {
      this.errored.set(true);
    } finally {
      this.loading.set(false);
    }
  }

  // ─────────────── CONNECT (PKCE) ───────────────

  /** Kick the OAuth flow: stash a fresh PKCE verifier and redirect the browser to Fitbit's authorize URL. */
  async connect(): Promise<void> {
    const s = this.status();
    if (!s?.configured || !s.clientId) return;
    try {
      await beginFitbitAuthorize(s.clientId, s.scopes);
    } catch {
      this.snack.open("Couldn't start the connect flow. Please try again.", 'Dismiss', { duration: 4000 });
    }
  }

  private async finishConnect(code: string, redirectUri: string, verifier: string): Promise<void> {
    this.connecting.set(true);
    this.loading.set(true);
    try {
      await firstValueFrom(this.api.healthConnect(code, redirectUri, verifier));
      this.snack.open('Fitbit connected — your data will sync automatically.', 'OK', { duration: 4000 });
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't connect your wearable. Please try again."),
        'Dismiss', { duration: 5000 });
    } finally {
      this.connecting.set(false);
      await this.reload();
    }
  }

  // ─────────────── TOGGLES ───────────────

  async toggle(key: SignalToggle['key'] | 'autoSyncEnabled'): Promise<void> {
    const s = this.status();
    if (!s) return;
    const patch: HealthSettingsPatch = { [key]: !s[key] };
    this.savingKey.set(key);
    try {
      this.status.set(await firstValueFrom(this.api.healthSettings(patch)));
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't save — try again"), 'Dismiss', { duration: 4000 });
    } finally {
      this.savingKey.set(null);
    }
  }

  // ─────────────── SYNC NOW + DISCONNECT ───────────────

  async syncNow(): Promise<void> {
    if (this.syncing()) return;
    this.syncing.set(true);
    this.result.set(null);
    try {
      const res = await firstValueFrom(this.api.healthSyncNow());
      this.result.set(res);
      await this.reload();
    } catch (e) {
      this.snack.open(this.messageOf(e, "Sync failed — try again"), 'Dismiss', { duration: 4000 });
    } finally {
      this.syncing.set(false);
    }
  }

  async disconnect(): Promise<void> {
    if (this.disconnecting()) return;
    this.disconnecting.set(true);
    try {
      await firstValueFrom(this.api.healthDisconnect());
      this.result.set(null);
      this.snack.open('Wearable disconnected.', 'OK', { duration: 3000 });
      await this.reload();
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't disconnect — try again"), 'Dismiss', { duration: 4000 });
    } finally {
      this.disconnecting.set(false);
    }
  }

  /** Total rows touched across all signals (drives the summary's headline). */
  summaryTotal(r: HealthSyncNowResult): number {
    const each = (x: { imported: number; updated: number }) => x.imported + x.updated;
    return each(r.steps) + each(r.sleep) + each(r.heartRate) + each(r.workouts);
  }

  summaryRows(r: HealthSyncNowResult): { label: string; imported: number; updated: number; skipped: number }[] {
    return [
      { label: 'Steps', ...r.steps },
      { label: 'Sleep', ...r.sleep },
      { label: 'Heart rate', ...r.heartRate },
      { label: 'Workouts', ...r.workouts },
    ];
  }

  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
