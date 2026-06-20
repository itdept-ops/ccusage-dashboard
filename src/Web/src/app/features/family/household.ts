import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { catchError, firstValueFrom, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { Household, HouseholdCandidate, HouseholdMember } from '../../core/models';

/**
 * Household settings — the family's "manage the home" room. The owner can rename the household, add a
 * member from a people-picker (drawn from GET /household/candidates by userId + name; never an email),
 * and remove a member (never the owner). Non-owners see the same roster read-only — the owner-only
 * controls simply don't render for them (and the server enforces it regardless). Identity everywhere is
 * userId + display name + picture; no email is ever shown (email-privacy).
 */
@Component({
  selector: 'app-household-settings',
  imports: [
    RouterLink, FormsModule, MatIconModule, MatButtonModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatTooltipModule, MatProgressSpinnerModule, MatSnackBarModule,
  ],
  templateUrl: './household.html',
  styleUrl: './family.scss',
})
export class HouseholdSettings {
  private api = inject(Api);
  readonly auth = inject(AuthService);
  private snack = inject(MatSnackBar);

  readonly household = signal<Household | null>(null);
  readonly loading = signal(true);
  readonly error = signal(false);

  /** Draft household name bound to the rename field (seeded from the loaded household). */
  readonly nameDraft = signal('');
  readonly saving = signal(false);

  /** People the owner may add (loaded lazily when the owner opens the picker). */
  readonly candidates = signal<HouseholdCandidate[]>([]);
  readonly candidatesLoading = signal(false);
  /** The candidate userId selected in the add-member picker (null = nothing chosen). */
  readonly pickedUserId = signal<number | null>(null);
  readonly adding = signal(false);
  /** userId currently being removed (drives the per-row spinner), or null. */
  readonly removingId = signal<number | null>(null);

  readonly members = computed<HouseholdMember[]>(() => this.household()?.members ?? []);

  /** The caller's own member row (whatever role), or undefined until loaded. */
  private readonly self = computed(() => this.members().find(m => m.isSelf));

  /** True when the caller is the household owner — gates every mutating control (mirrors the server). */
  readonly isOwner = computed(() => this.self()?.role === 'owner');

  /** True once the rename field differs from the saved name (enables the Save button). */
  readonly nameDirty = computed(() => {
    const saved = this.household()?.name ?? '';
    return this.nameDraft().trim().length > 0 && this.nameDraft().trim() !== saved;
  });

  constructor() {
    this.api.getHousehold()
      .pipe(catchError(() => { this.error.set(true); return of(null); }), takeUntilDestroyed())
      .subscribe(h => { if (h) this.apply(h); this.loading.set(false); });
  }

  private apply(h: Household): void {
    this.household.set(h);
    this.nameDraft.set(h.name);
  }

  /** Display role label for the chip. */
  roleLabel(role: string): string {
    return role === 'owner' ? 'Owner' : 'Member';
  }

  initials(name: string): string {
    const parts = (name || '').split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '') + (parts[1]?.[0] ?? '')).toUpperCase() || '?';
  }

  /** Persist a rename (owner only). */
  async saveName(): Promise<void> {
    const name = this.nameDraft().trim();
    if (!name || !this.nameDirty() || this.saving()) return;
    this.saving.set(true);
    try {
      const h = await firstValueFrom(this.api.renameHousehold(name));
      this.apply(h);
      this.snack.open('Family name updated.', 'OK', { duration: 3000 });
    } catch {
      this.snack.open("Couldn't rename the family. Please try again.", 'OK', { duration: 4000 });
    } finally {
      this.saving.set(false);
    }
  }

  /** Lazy-load the candidate people when the owner first focuses the picker. */
  loadCandidates(): void {
    if (this.candidatesLoading() || this.candidates().length > 0) return;
    this.candidatesLoading.set(true);
    this.api.householdCandidates()
      .pipe(catchError(() => of<HouseholdCandidate[]>([])))
      .subscribe(list => { this.candidates.set(list); this.candidatesLoading.set(false); });
  }

  /** Add the picked person to the household (owner only). */
  async addMember(): Promise<void> {
    const userId = this.pickedUserId();
    if (userId == null || this.adding()) return;
    this.adding.set(true);
    try {
      const h = await firstValueFrom(this.api.addMember(userId));
      this.apply(h);
      // Drop the just-added person from the picker so it can't be re-added, and reset the selection.
      this.candidates.update(list => list.filter(c => c.userId !== userId));
      this.pickedUserId.set(null);
      this.snack.open('Welcome to the family!', 'OK', { duration: 3000 });
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't add that person."), 'OK', { duration: 4000 });
    } finally {
      this.adding.set(false);
    }
  }

  /** Remove a member (owner only; never the owner). */
  async removeMember(m: HouseholdMember): Promise<void> {
    if (!this.isOwner() || m.role === 'owner' || this.removingId() != null) return;
    this.removingId.set(m.userId);
    try {
      const h = await firstValueFrom(this.api.removeMember(m.userId));
      this.apply(h);
      // Refresh the picker so the removed person becomes addable again.
      this.candidates.set([]);
      this.snack.open(`${m.name} was removed from the family.`, 'OK', { duration: 3000 });
    } catch (e) {
      this.snack.open(this.messageOf(e, "Couldn't remove that member."), 'OK', { duration: 4000 });
    } finally {
      this.removingId.set(null);
    }
  }

  /** Pull the server's friendly `message` from an HttpErrorResponse, falling back to a default. */
  private messageOf(e: unknown, fallback: string): string {
    const msg = (e as { error?: { message?: string } })?.error?.message;
    return typeof msg === 'string' && msg ? msg : fallback;
  }
}
