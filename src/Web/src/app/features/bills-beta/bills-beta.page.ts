import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';

import { Api } from '../../core/api';
import { AuthService } from '../../core/auth';
import { BillDto, BillItemDto, ChatContactDto, PERM, ReceiptBreakdownDto } from '../../core/models';
import { captureImage, pickImage, confirmPhotoNotice } from '../tracker/ai-image';

import { PersonTotalCard } from './cards/person-total-card';
import { BillItemRow, AssignChange } from './rows/bill-item-row';
import { ReceiptReviewSheet, ReceiptReviewResult } from './ui/receipt-review-sheet';

/**
 * Bills Beta — the "Tally" mobile-first split-the-check experience. A clean-sheet redesign of the live
 * desktop two-pane bills editor (`app-bills`), reframed for the real moment "we got the check, split it
 * now": a sticky bill-switcher pill scroller, a hero capture card (camera/photo dropzone → AI receipt),
 * claim-first 56px item rows with an inline contact-avatar claim strip (replacing the per-item dialog),
 * tax/tip steppers, a horizontal snap rail of per-person total cards (+ an amber Unclaimed card, never
 * red), and a sticky bottom share bar ("Get claim link" → Web Share).
 *
 * ISOLATION: reuses the existing bills `Api` methods + DTOs and the tracker-beta {@link BottomSheet} /
 * {@link SwipeRow} primitives, but touches NO live page/component. The 3-line `Bills.toPayLinks` mapper
 * is inlined into {@link PersonTotalCard}. Owns its own optimistic row patch + reconcile (no shared store).
 * All tokens come from this page's `:host` (bills-beta.page.scss) — no global `--tech-*`.
 */
@Component({
  selector: 'app-bills-beta',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './bills-beta.page.scss',
  imports: [
    CurrencyPipe, FormsModule, MatIconModule, MatSnackBarModule,
    PersonTotalCard, BillItemRow, ReceiptReviewSheet,
  ],
  template: `
    <!-- ─────────────── STICKY BILL-SWITCHER ─────────────── -->
    <header class="bb-switcher">
      <div class="bb-switcher__rail" role="tablist" aria-label="Your bills">
        @for (b of bills(); track b.id) {
          <button type="button" class="bb-pill"
                  [class.is-active]="b.id === selectedId()"
                  [class.is-settled]="b.status === 'settled'"
                  role="tab" [attr.aria-selected]="b.id === selectedId()"
                  (click)="select(b.id)">
            <span class="bb-pill__dot" aria-hidden="true"></span>{{ b.title }}
          </button>
        }
      </div>
      <button type="button" class="bb-add" aria-label="New bill" [disabled]="busy()" (click)="createBill()">
        <mat-icon aria-hidden="true">add</mat-icon>
      </button>
    </header>

    <!-- ─────────────── SCROLL REGION ─────────────── -->
    <main class="bb-scroll">
      @if (loading() && !bills().length) {
        <div class="bb-card bb-skeleton" style="min-height:152px" aria-hidden="true"></div>
        <div class="bb-card bb-skeleton" style="min-height:200px" aria-hidden="true"></div>
      } @else if (!bills().length) {
        <div class="bb-empty">
          <mat-icon aria-hidden="true">receipt_long</mat-icon>
          <p>No bills yet. Start one and split the check.</p>
          <button type="button" class="bb-empty__btn" (click)="createBill()">New bill</button>
        </div>
      } @else if (selected(); as b) {

        <!-- HERO CAPTURE CARD -->
        <section class="bb-card bb-hero" aria-label="Bill">
          <h1 class="bb-hero__title">
            <input [ngModel]="b.title" (blur)="saveTitle(b, $any($event.target).value)"
                   aria-label="Bill title" />
          </h1>

          @if (importing()) {
            <div class="bb-importing" aria-live="polite">
              <span>Adding items… {{ importDone() }}/{{ importTotal() }}</span>
              <div class="bb-importing__track"><div class="bb-importing__fill"
                   [style.width.%]="importTotal() ? (importDone() / importTotal()) * 100 : 0"></div></div>
            </div>
          } @else if (!b.items.length) {
            <!-- Empty: full dropzone -->
            <div class="bb-dropzone">
              @if (canUseVision()) {
                <button type="button" class="bb-dropbtn" (click)="uploadReceipt(b, true)">
                  <mat-icon aria-hidden="true">photo_camera</mat-icon> Snap receipt
                </button>
                <button type="button" class="bb-dropbtn" (click)="uploadReceipt(b, false)">
                  <mat-icon aria-hidden="true">image</mat-icon> Photo
                </button>
              } @else {
                <div class="bb-dropbtn" style="cursor:default">
                  <mat-icon aria-hidden="true">add</mat-icon> Add items below
                </div>
              }
            </div>
          } @else {
            <!-- Has items: slim add bar -->
            <div class="bb-addbar">
              <div class="bb-addbar__field">
                <input class="bb-addbar__name" placeholder="Add item" [ngModel]="newName()"
                       (ngModelChange)="newName.set($event)" (keydown.enter)="addItem(b)"
                       aria-label="New item name" />
                <input class="bb-addbar__amt" type="number" inputmode="decimal" min="0" step="0.01"
                       placeholder="0.00" [ngModel]="newAmount()" (ngModelChange)="newAmount.set($event)"
                       (keydown.enter)="addItem(b)" aria-label="New item amount" />
              </div>
              <button type="button" class="bb-addbar__btn" aria-label="Add item"
                      [disabled]="busy()" (click)="addItem(b)">
                <mat-icon aria-hidden="true">add</mat-icon>
              </button>
              @if (canUseVision()) {
                <button type="button" class="bb-iconbtn" aria-label="Snap receipt"
                        (click)="uploadReceipt(b, true)">
                  <mat-icon aria-hidden="true">photo_camera</mat-icon>
                </button>
              }
            </div>
          }
        </section>

        @if (b.items.length) {
          <!-- ITEMS -->
          <p class="bb-section-h">Items</p>
          <div class="bb-items">
            @for (it of b.items; track it.id) {
              <app-bill-item-row [item]="it" [contacts]="contacts()"
                                 (settle)="toggleItemSettled(b, $event)"
                                 (delete)="deleteItem(b, $event)"
                                 (assign)="onAssign(b, $event)" />
            }
          </div>

          <!-- TAX / TIP STEPPERS -->
          <div class="bb-taxtip">
            <div class="bb-stepper">
              <span class="bb-stepper__lbl">Tax</span>
              <span class="bb-stepper__v">{{ (b.taxAmount ?? 0) | currency: 'USD' }}</span>
              <div class="bb-stepper__btns">
                <button type="button" class="bb-stepper__btn" aria-label="Decrease tax"
                        (click)="bumpTax(b, -1)">−</button>
                <button type="button" class="bb-stepper__btn" aria-label="Increase tax"
                        (click)="bumpTax(b, 1)">+</button>
              </div>
            </div>
            <div class="bb-stepper">
              <span class="bb-stepper__lbl">Tip</span>
              <span class="bb-stepper__v">{{ (b.tipAmount ?? 0) | currency: 'USD' }}</span>
              <div class="bb-stepper__btns">
                <button type="button" class="bb-stepper__btn" aria-label="Decrease tip"
                        (click)="bumpTip(b, -1)">−</button>
                <button type="button" class="bb-stepper__btn" aria-label="Increase tip"
                        (click)="bumpTip(b, 1)">+</button>
              </div>
            </div>
          </div>

          <!-- BILL TOTAL -->
          <div class="bb-billtotal">
            <span class="bb-billtotal__lbl">Total</span>
            <span class="bb-billtotal__v">{{ billTotal() | currency: 'USD' }}</span>
          </div>

          <!-- PER-PERSON TOTALS RAIL -->
          @if (b.personTotals.length || b.unclaimedTotal > 0) {
            <p class="bb-section-h">Who owes what</p>
            <div class="bb-totals-rail" aria-label="Per-person totals">
              @for (p of b.personTotals; track p.name) {
                <app-person-total-card [person]="p" [payments]="b.payments" />
              }
              @if (b.unclaimedTotal > 0) {
                <app-person-total-card [unclaimed]="true" [amount]="b.unclaimedTotal" />
              }
            </div>
          }
        }
      }
    </main>

    <!-- ─────────────── STICKY SHARE BAR ─────────────── -->
    @if (selected(); as b) {
      <footer class="bb-share-bar">
        <button type="button" class="bb-share-btn" [class.is-active]="b.shareEnabled"
                (click)="b.shareEnabled ? shareLink(b) : enableShare(b)">
          <mat-icon aria-hidden="true">{{ b.shareEnabled ? 'link' : 'ios_share' }}</mat-icon>
          {{ b.shareEnabled ? 'Share claim link' : 'Get claim link' }}
        </button>
        @if (b.shareEnabled) {
          <button type="button" class="bb-share-copy" aria-label="Copy claim link" (click)="copyShare(b)">
            <mat-icon aria-hidden="true">content_copy</mat-icon>
          </button>
          <button type="button" class="bb-share-copy" aria-label="Turn off claim link" (click)="disableShare(b)">
            <mat-icon aria-hidden="true">link_off</mat-icon>
          </button>
        }
      </footer>
    }

    <!-- RECEIPT REVIEW SHEET -->
    <app-receipt-review-sheet [(open)]="reviewOpen" [breakdown]="reviewBreakdown()"
                              (confirmed)="onReviewConfirmed($event)" />
  `,
})
export class BillsBetaPage {
  private api = inject(Api);
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);

  readonly bills = signal<BillDto[]>([]);
  readonly selectedId = signal<number | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly contacts = signal<ChatContactDto[]>([]);

  /** AI receipt is only offered when the caller holds ai.vision (matching the live page's extra gate). */
  readonly canUseVision = computed(() => this.auth.hasPermission(PERM.aiVision));

  readonly newName = signal('');
  readonly newAmount = signal<number | null>(null);

  // Receipt-import progress (drives the determinate bar in the hero).
  readonly importing = signal(false);
  readonly importDone = signal(0);
  readonly importTotal = signal(0);

  // Receipt-review sheet state.
  readonly reviewOpen = signal(false);
  readonly reviewBreakdown = signal<ReceiptBreakdownDto | null>(null);
  private reviewBillId: number | null = null;

  readonly selected = computed<BillDto | null>(() => {
    const id = this.selectedId();
    return id == null ? null : this.bills().find(b => b.id === id) ?? null;
  });

  /** The bill's full list price (items + tax + tip). */
  readonly billTotal = computed(() => {
    const b = this.selected();
    if (!b) return 0;
    return b.items.reduce((s, i) => s + i.amount, 0) + (b.taxAmount ?? 0) + (b.tipAmount ?? 0);
  });

  constructor() {
    this.reload(true);
    this.loadContacts();
  }

  private async reload(selectFirst = false): Promise<void> {
    this.loading.set(true);
    try {
      const list = await firstValueFrom(this.api.bills());
      this.bills.set(list);
      if (selectFirst && list.length && this.selectedId() == null) this.selectedId.set(list[0].id);
      if (this.selectedId() != null && !list.some(b => b.id === this.selectedId())) {
        this.selectedId.set(list[0]?.id ?? null);
      }
    } catch {
      this.snack.open('Could not load your bills.', 'OK', { duration: 4000 });
    } finally {
      this.loading.set(false);
    }
  }

  /** Best-effort contacts for the inline claim strip — chat.read gated; silently empty if denied. */
  private async loadContacts(): Promise<void> {
    try {
      this.contacts.set(await firstValueFrom(this.api.myContacts()));
    } catch {
      this.contacts.set([]);
    }
  }

  /** Replace one bill in the list (after a write returns the fresh DTO) without a full reload. */
  private patchBill(b: BillDto): void {
    this.bills.set(this.bills().map(x => (x.id === b.id ? b : x)));
  }

  /** Patch a single item's fields in the selected bill's list — used for optimistic assign/settle. */
  private patchItem(billId: number, itemId: number, change: Partial<BillItemDto>): void {
    this.bills.set(this.bills().map(b =>
      b.id !== billId ? b : { ...b, items: b.items.map(i => (i.id === itemId ? { ...i, ...change } : i)) }));
  }

  select(id: number): void {
    this.selectedId.set(id);
  }

  async createBill(): Promise<void> {
    this.busy.set(true);
    try {
      const b = await firstValueFrom(this.api.createBill({ title: 'New bill' }));
      this.bills.set([b, ...this.bills()]);
      this.selectedId.set(b.id);
    } catch {
      this.snack.open('Could not create the bill.', 'OK', { duration: 4000 });
    } finally {
      this.busy.set(false);
    }
  }

  async saveTitle(b: BillDto, title: string): Promise<void> {
    const t = title.trim();
    if (!t || t === b.title) return;
    await this.update(b, { title: t });
  }

  /** Tax/tip steppers move in $1 increments, clamped at 0. */
  async bumpTax(b: BillDto, dir: number): Promise<void> {
    const next = Math.max(0, Math.round(((b.taxAmount ?? 0) + dir) * 100) / 100);
    await this.update(b, { taxAmount: next || null });
  }

  async bumpTip(b: BillDto, dir: number): Promise<void> {
    const next = Math.max(0, Math.round(((b.tipAmount ?? 0) + dir) * 100) / 100);
    await this.update(b, { tipAmount: next || null });
  }

  /** Mirrors the live page: always resend tax/tip so a null clear stays explicit, merged with `body`. */
  private async update(b: BillDto, body: Parameters<Api['updateBill']>[1]): Promise<void> {
    try {
      const updated = await firstValueFrom(this.api.updateBill(b.id, {
        taxAmount: b.taxAmount ?? null,
        tipAmount: b.tipAmount ?? null,
        ...body,
      }));
      this.patchBill(updated);
    } catch {
      this.snack.open('Could not save.', 'OK', { duration: 4000 });
      this.reload();
    }
  }

  // ---- Items ----

  async addItem(b: BillDto): Promise<void> {
    const name = this.newName().trim();
    const amount = this.newAmount() ?? 0;
    if (!name || amount <= 0) return;
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.addBillItem(b.id, { name, amount }));
      this.newName.set('');
      this.newAmount.set(null);
      await this.refreshSelected();
    } catch {
      this.snack.open('Could not add the item.', 'OK', { duration: 4000 });
    } finally {
      this.busy.set(false);
    }
  }

  async deleteItem(b: BillDto, item: BillItemDto): Promise<void> {
    // Optimistic remove, reconcile on refresh.
    const prev = b.items;
    this.bills.set(this.bills().map(x =>
      x.id !== b.id ? x : { ...x, items: x.items.filter(i => i.id !== item.id) }));
    try {
      await firstValueFrom(this.api.deleteBillItem(b.id, item.id));
      await this.refreshSelected();
    } catch {
      this.bills.set(this.bills().map(x => (x.id === b.id ? { ...x, items: prev } : x)));
      this.snack.open('Could not remove the item.', 'OK', { duration: 4000 });
    }
  }

  /** Optimistic assign: patch the row, reconcile with bill(id); revert + snackbar on error. */
  async onAssign(b: BillDto, change: AssignChange): Promise<void> {
    const { item, userId } = change;
    const prev = { assignedToUserId: item.assignedToUserId ?? null, assignedToName: item.assignedToName ?? null, open: item.open };
    const name = userId == null ? null : (this.contacts().find(c => c.userId === userId)?.name ?? null);
    this.patchItem(b.id, item.id, { assignedToUserId: userId, assignedToName: name, open: userId == null });
    try {
      await firstValueFrom(this.api.assignBillItem(b.id, item.id, userId));
      await this.refreshSelected();
    } catch (e: unknown) {
      this.patchItem(b.id, item.id, prev);
      const msg = (e as { error?: { message?: string } })?.error?.message ?? 'Could not assign the item.';
      this.snack.open(msg, 'OK', { duration: 4000 });
    }
  }

  /** Optimistic settle: flip the row, reconcile with bill(id); revert + snackbar on error. */
  async toggleItemSettled(b: BillDto, item: BillItemDto): Promise<void> {
    const next = !item.settled;
    this.patchItem(b.id, item.id, { settled: next });
    try {
      await firstValueFrom(this.api.settleBillItem(b.id, item.id, next));
      await this.refreshSelected();
    } catch {
      this.patchItem(b.id, item.id, { settled: !next });
      this.snack.open('Could not update the item.', 'OK', { duration: 4000 });
    }
  }

  private async refreshSelected(): Promise<void> {
    const id = this.selectedId();
    if (id == null) return;
    try {
      this.patchBill(await firstValueFrom(this.api.bill(id)));
    } catch {
      this.reload();
    }
  }

  // ---- Receipt AI (gated ai.vision; image digested in-memory, never stored; 503-graceful) ----

  async uploadReceipt(b: BillDto, fromCamera: boolean): Promise<void> {
    if (!this.canUseVision()) return;
    if (!(await confirmPhotoNotice())) return;

    let img;
    try {
      img = fromCamera ? await captureImage() : await pickImage();
    } catch (e: unknown) {
      this.snack.open((e as Error)?.message ?? 'Could not read that image.', 'OK', { duration: 4000 });
      return;
    }
    if (!img) return;

    this.busy.set(true);
    let breakdown: ReceiptBreakdownDto;
    try {
      breakdown = await firstValueFrom(this.api.billReceipt(b.id, img));
    } catch (e: unknown) {
      const status = (e as { status?: number })?.status;
      this.snack.open(
        status === 503
          ? 'Receipt AI is unavailable right now — add the items manually.'
          : 'Could not read that receipt. Add the items manually.',
        'OK', { duration: 5000 });
      return;
    } finally {
      this.busy.set(false);
    }

    this.reviewBillId = b.id;
    this.reviewBreakdown.set(breakdown);
    this.reviewOpen.set(true);
  }

  /** Batch-write the reviewed lines, then the detected tax/tip, behind a determinate bar. */
  async onReviewConfirmed(result: ReceiptReviewResult): Promise<void> {
    const id = this.reviewBillId;
    const b = id == null ? null : this.bills().find(x => x.id === id);
    if (!b) return;

    this.importing.set(true);
    this.importTotal.set(result.items.length);
    this.importDone.set(0);
    try {
      for (const it of result.items) {
        await firstValueFrom(this.api.addBillItem(b.id, { name: it.name, amount: it.amount }));
        this.importDone.update(n => n + 1);
      }
      if (result.tax != null || result.tip != null) {
        await firstValueFrom(this.api.updateBill(b.id, {
          taxAmount: result.tax ?? b.taxAmount ?? null,
          tipAmount: result.tip ?? b.tipAmount ?? null,
        }));
      }
      await this.refreshSelected();
      this.snack.open(`Added ${result.items.length} item${result.items.length === 1 ? '' : 's'} from the receipt.`,
        'OK', { duration: 3000 });
    } catch {
      this.snack.open('Saved some lines but hit an error — check the bill.', 'OK', { duration: 4000 });
      this.refreshSelected();
    } finally {
      this.importing.set(false);
      this.reviewBillId = null;
    }
  }

  // ---- Public claim link ----

  /** Absolute claim URL for copy/share (the API returns a path like /bill/{token}). */
  private shareUrl(b: BillDto): string {
    return b.sharePath ? `${location.origin}${b.sharePath}` : '';
  }

  private async setShare(b: BillDto, enabled: boolean): Promise<void> {
    try {
      const res = await firstValueFrom(this.api.toggleBillShare(b.id, enabled));
      this.patchBill({ ...b, shareEnabled: res.shareEnabled, sharePath: res.sharePath ?? null });
    } catch {
      this.snack.open('Could not update the share link.', 'OK', { duration: 4000 });
    }
  }

  /** Turn the link on, then immediately offer to share it. */
  async enableShare(b: BillDto): Promise<void> {
    await this.setShare(b, true);
    const fresh = this.selected();
    if (fresh?.shareEnabled) await this.shareLink(fresh);
  }

  async disableShare(b: BillDto): Promise<void> {
    await this.setShare(b, false);
  }

  /** Native Web Share when available, else copy to clipboard. */
  async shareLink(b: BillDto): Promise<void> {
    const url = this.shareUrl(b);
    if (!url) return;
    const nav = navigator as Navigator & { share?: (d: ShareData) => Promise<void> };
    if (nav.share) {
      try {
        await nav.share({ title: b.title, text: `Claim your items on "${b.title}"`, url });
        return;
      } catch {
        // user cancelled the share sheet — fall through to copy as a convenience
      }
    }
    await this.copyShare(b);
  }

  async copyShare(b: BillDto): Promise<void> {
    const url = this.shareUrl(b);
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      this.snack.open('Claim link copied.', 'OK', { duration: 2500 });
    } catch {
      this.snack.open('Copy failed — select and copy the link manually.', 'OK', { duration: 4000 });
    }
  }
}
