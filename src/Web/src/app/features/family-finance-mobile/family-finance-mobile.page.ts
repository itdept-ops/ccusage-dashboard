import {
  ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal, viewChild,
  type ElementRef,
} from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { catchError, firstValueFrom, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';

import { Api } from '../../core/api';
import {
  FinanceAccount,
  FinanceImportBatch,
  FinanceMoneyCoachResult,
  FinanceOwner,
  FinanceSummary,
  FinanceSummaryAiResult,
  FinanceTransaction,
  FinanceTransactionsPage,
  FinanceTxnKind,
} from '../../core/models';
import {
  BetaPullRefresh, BetaSegmentedControl, BetaBottomSheet, BetaStatTile, BetaSkeleton,
  BetaFab, BetaToaster, ToastController, type Segment,
} from '../beta-ui';

/** Friendly His/Hers/Joint labels for the owner tags (display only — never an email). */
const OWNER_LABEL: Record<FinanceOwner, string> = {
  his: 'His', hers: 'Hers', joint: 'Joint', unassigned: 'Unassigned',
};

/** A per-owner accent so His/Hers/Joint read consistently across the screen. */
const OWNER_COLOR: Record<FinanceOwner, string> = {
  his: '#3d8bff', hers: '#ff7eb6', joint: '#3dd68c', unassigned: '#5e6c82',
};

/** The mobile detail filter tabs over the month's transactions. */
type DetailTab = 'spending' | 'recurring';

/**
 * Family Finance "Ledger" — the mobile-first twin of the live /family/finance Hub room, rebuilt on the
 * shared beta-ui "Strata" kit (`@use '../beta-ui/beta-kit'`). One signature accent — a cool MINT → TEAL —
 * re-skins the whole screen via the per-page accent contract. The most sensitive room in the hub: the
 * canonical route stays DOUBLE-GATED server-side by family.use AND family.finance (this twin adds no data
 * path of its own), data is household-private and never shared to outside contacts, and an import is shown
 * by display NAME only — never an email.
 *
 * It re-presents the live dashboard for the thumb: an immersive header with a prev/next MONTH stepper and
 * three headline {@link BetaStatTile} cards (Spent · Income · Net); an optional warm "✨ Explain this month"
 * AI card (read-only, never blocks); a {@link BetaSegmentedControl} flipping a list between SPENDING
 * (by-category bars + the His-vs-Hers owner split) and RECURRING (the deterministic Money-coach bills
 * floor); a recent-transactions strip that opens a {@link BetaBottomSheet} per row; and a {@link BetaFab}
 * whose only "add" path is the SAME Rocket-Money CSV import the live page uses (read-as-text → POST).
 *
 * DATA PARITY + PRIVACY: every number comes straight from the SAME double-gated `/api/family/finance/*`
 * endpoints the live page calls — {@link Api.financeSummary}, {@link Api.financeAccounts},
 * {@link Api.financeTransactions}, {@link Api.financeImports}, {@link Api.financeSummaryAi},
 * {@link Api.financeMoneyCoachAi}, and {@link Api.importFinanceCsv} VERBATIM (the import body is built
 * exactly like the live dropzone). The server resolves the month (it may fall back to the latest with data)
 * and enforces all gating + household scoping; the UI only re-presents what it returns.
 *
 * ISOLATION: gated by `platform.mobile` + the SAME double family.use/family.finance the live route carries;
 * it consumes the kit + the SAME Api as the live counterpart. No live page is imported or modified. Layout
 * is mobile-first (44px targets, safe-area insets, no 390px overflow) and centers on desktop; reduced
 * motion collapses the kit animations via the a11y killswitch. The harness mocks the Api, so every state
 * (loading skeletons, empty/first-run, error) renders cleanly with ZERO data.
 */
@Component({
  selector: 'app-family-finance-mobile',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [ToastController],
  imports: [
    DecimalPipe, MatIconModule,
    BetaPullRefresh, BetaSegmentedControl, BetaBottomSheet, BetaStatTile, BetaSkeleton,
    BetaFab, BetaToaster,
  ],
  template: `
    <!-- ─────────────── PULL-TO-REFRESH OWNS THE SCROLL ─────────────── -->
    <app-bs-pull-refresh class="ff-ptr" [busy]="refreshing()" (refresh)="reload()">
      <div class="ff-scroll" aria-live="polite">

        <!-- ─── IMMERSIVE HEADER: title + accent bloom + month stepper ─── -->
        <header class="ff-hero">
          <div class="ff-hero__bloom" aria-hidden="true"></div>
          <p class="ff-hero__kicker"><mat-icon aria-hidden="true">account_balance_wallet</mat-icon> Family Finance</p>
          <h1 class="ff-hero__title">Ledger</h1>
          <p class="ff-hero__sub">Where the money went — private to your household, never shared out.</p>

          @if (!loading() && !errored() && hasData()) {
            <div class="ff-month" role="group" aria-label="Viewed month">
              <button type="button" class="ff-month__nav" (click)="stepMonth(-1)" aria-label="Previous month">
                <mat-icon aria-hidden="true">chevron_left</mat-icon>
              </button>
              <span class="ff-month__label">{{ monthLabel() }}</span>
              <button type="button" class="ff-month__nav" (click)="stepMonth(1)" aria-label="Next month">
                <mat-icon aria-hidden="true">chevron_right</mat-icon>
              </button>
            </div>
          }
        </header>

        @if (loading()) {
          <!-- skeleton: stat row + list -->
          <div class="ff-stats" aria-hidden="true">
            @for (n of [0,1,2]; track n) { <app-bs-skeleton height="84px" radius="var(--r-tile)" /> }
          </div>
          <div class="ff-seg-wrap" aria-hidden="true">
            <app-bs-skeleton width="100%" height="44px" radius="var(--r-pill)" />
          </div>
          <div class="ff-list" aria-hidden="true">
            @for (n of skeletonCells; track n) { <app-bs-skeleton height="62px" radius="var(--r-tile)" /> }
          </div>

        } @else if (errored()) {
          <div class="ff-state">
            <span class="ff-state__orb"><mat-icon aria-hidden="true">cloud_off</mat-icon></span>
            <h2 class="ff-state__title">Couldn't load your finances</h2>
            <p class="ff-state__body">Something went wrong fetching the household ledger. Give it another go.</p>
            <button type="button" class="ff-state__cta" (click)="reload()">
              <mat-icon aria-hidden="true">refresh</mat-icon> Try again
            </button>
          </div>

        } @else if (!hasData()) {
          <!-- FIRST-RUN: nothing imported yet -->
          <div class="ff-empty">
            <span class="ff-empty__orb"><mat-icon aria-hidden="true">request_quote</mat-icon></span>
            <h2 class="ff-empty__title">No finances yet</h2>
            <p class="ff-empty__body">
              Import a Rocket Money CSV to see your spending, income and recurring bills — all private to your household.
            </p>
            <button type="button" class="ff-empty__cta" (click)="pickFile()">
              <mat-icon aria-hidden="true">upload_file</mat-icon> Import a CSV
            </button>
          </div>

        } @else {
          <!-- ─── HEADLINE STAT TILES: Spent · Income · Net ─── -->
          <div class="ff-stats">
            <app-bs-stat-tile [value]="money(summary()?.totalSpent ?? 0)" label="Spent"
              accentA="#fb7185" accentB="#e11d48" />
            <app-bs-stat-tile [value]="money(summary()?.totalIncome ?? 0)" label="Income"
              accentA="#34d399" accentB="#059669" />
            <app-bs-stat-tile [value]="money(net())" label="Net"
              [accentA]="net() >= 0 ? '#34d399' : '#fb7185'" [accentB]="net() >= 0 ? '#059669' : '#e11d48'" />
          </div>

          <!-- ─── ✨ EXPLAIN THIS MONTH (read-only AI narration; best-effort) ─── -->
          @if (aiSummary(); as ai) {
            <section class="ff-ai">
              <span class="ff-ai__spark" aria-hidden="true"><mat-icon>auto_awesome</mat-icon></span>
              <div class="ff-ai__body">
                <p class="ff-ai__narr">{{ ai.narrative }}</p>
                @if (ai.insights.length) {
                  <ul class="ff-ai__insights">
                    @for (ins of ai.insights; track $index) { <li>{{ ins }}</li> }
                  </ul>
                }
              </div>
            </section>
          }

          <!-- ─── TAB SWITCH: Spending | Recurring ─── -->
          <div class="ff-seg-wrap">
            <app-bs-segmented class="ff-seg"
              [segments]="tabSegments()" [value]="tab()" label="Show"
              (change)="setTab($event)" />
          </div>

          @if (tab() === 'spending') {
            <!-- by-category bars -->
            @if (categories().length) {
              <div class="ff-bars">
                @for (c of categories(); track c.category; let i = $index) {
                  <div class="ff-bar ff-reveal" [style.--ri]="i">
                    <div class="ff-bar__top">
                      <span class="ff-bar__name">{{ c.category }}</span>
                      <span class="ff-bar__amt mono-num">{{ money(c.amount) }}</span>
                    </div>
                    <div class="ff-bar__track" aria-hidden="true">
                      <span class="ff-bar__fill" [style.width.%]="barPct(c.amount)"></span>
                    </div>
                    <span class="ff-bar__pct mono-num">{{ c.pct | number:'1.0-0' }}%</span>
                  </div>
                }
              </div>

              <!-- His vs Hers vs Joint split -->
              @if (ownerRows().length) {
                <div class="ff-owners">
                  <span class="ff-owners__title">His · Hers · Joint</span>
                  @for (o of ownerRows(); track o.owner) {
                    <div class="ff-owner">
                      <span class="ff-owner__dot" [style.background]="ownerColor(o.owner)" aria-hidden="true"></span>
                      <span class="ff-owner__name">{{ ownerLabel(o.owner) }}</span>
                      <span class="ff-owner__bar" aria-hidden="true">
                        <span class="ff-owner__fill"
                          [style.width.%]="ownerPct(o.amount)" [style.background]="ownerColor(o.owner)"></span>
                      </span>
                      <span class="ff-owner__amt mono-num">{{ money(o.amount) }}</span>
                    </div>
                  }
                </div>
              }
            } @else {
              <div class="ff-mini-empty">
                <mat-icon aria-hidden="true">savings</mat-icon>
                <p>No spending recorded for {{ monthLabel() }}.</p>
              </div>
            }

            <!-- recent transactions for the month -->
            <div class="ff-txn-head">
              <span class="ff-txn-head__title">Recent transactions</span>
              @if (txnTotal()) { <span class="ff-txn-head__count mono-num">{{ txnTotal() | number }}</span> }
            </div>
            @if (txnLoading()) {
              <div class="ff-list" aria-hidden="true">
                @for (n of [0,1,2,3]; track n) { <app-bs-skeleton height="58px" radius="var(--r-tile)" /> }
              </div>
            } @else if (txns().length) {
              <div class="ff-list">
                @for (t of txns(); track t.id; let i = $index) {
                  <button type="button" class="ff-txn ff-reveal" [style.--ri]="i"
                          (click)="openTxn(t)" [attr.aria-label]="txnAria(t)">
                    <span class="ff-txn__glyph" [class]="'is-' + t.kind" aria-hidden="true">
                      <mat-icon>{{ kindIcon(t.kind) }}</mat-icon>
                    </span>
                    <span class="ff-txn__body">
                      <span class="ff-txn__merchant">{{ t.merchant }}</span>
                      <span class="ff-txn__meta">
                        {{ txnDate(t.date) }}
                        @if (t.category) { · {{ t.category }} }
                      </span>
                    </span>
                    <span class="ff-txn__amt mono-num" [class.is-in]="t.kind === 'income'">
                      {{ signedMoney(t) }}
                    </span>
                  </button>
                }
              </div>
            } @else {
              <div class="ff-mini-empty">
                <mat-icon aria-hidden="true">receipt_long</mat-icon>
                <p>No transactions in {{ monthLabel() }}.</p>
              </div>
            }

          } @else {
            <!-- ─── RECURRING: the deterministic Money-coach bills floor ─── -->
            @if (coachLoading()) {
              <div class="ff-list" aria-hidden="true">
                @for (n of [0,1,2,3]; track n) { <app-bs-skeleton height="62px" radius="var(--r-tile)" /> }
              </div>
            } @else if (coach(); as c) {
              <div class="ff-recur-total">
                <span class="ff-recur-total__l">Estimated monthly bills</span>
                <span class="ff-recur-total__n mono-num">{{ money(c.monthlyRecurringTotal) }}</span>
              </div>
              @if (c.narrative) {
                <section class="ff-ai ff-ai--tips">
                  <span class="ff-ai__spark" aria-hidden="true"><mat-icon>auto_awesome</mat-icon></span>
                  <div class="ff-ai__body">
                    <p class="ff-ai__narr">{{ c.narrative }}</p>
                    @if (c.tips.length) {
                      <ul class="ff-ai__insights">
                        @for (tip of c.tips; track $index) { <li>{{ tip }}</li> }
                      </ul>
                    }
                  </div>
                </section>
              }
              @if (c.recurring.length) {
                <div class="ff-list">
                  @for (r of c.recurring; track r.merchant; let i = $index) {
                    <div class="ff-recur ff-reveal" [style.--ri]="i">
                      <span class="ff-recur__glyph" aria-hidden="true"><mat-icon>autorenew</mat-icon></span>
                      <span class="ff-recur__body">
                        <span class="ff-recur__merchant">{{ r.merchant }}</span>
                        <span class="ff-recur__meta">
                          {{ r.cadence }} · seen {{ r.monthsSeen }} mo · last {{ txnDate(r.lastDate) }}
                        </span>
                      </span>
                      <span class="ff-recur__amt mono-num">{{ money(r.typicalAmount) }}</span>
                    </div>
                  }
                </div>
              } @else {
                <div class="ff-mini-empty">
                  <mat-icon aria-hidden="true">autorenew</mat-icon>
                  <p>No recurring bills detected yet.</p>
                </div>
              }
            } @else {
              <div class="ff-mini-empty">
                <mat-icon aria-hidden="true">autorenew</mat-icon>
                <p>Recurring bills will show up here once you've imported enough activity.</p>
              </div>
            }
          }

          <!-- import history strip (who, by NAME only) -->
          @if (imports().length) {
            <div class="ff-imports">
              <span class="ff-imports__title"><mat-icon aria-hidden="true">history</mat-icon> Recent imports</span>
              @for (im of imports().slice(0, 4); track im.id) {
                <div class="ff-import">
                  <span class="ff-import__file">{{ im.fileName }}</span>
                  <span class="ff-import__meta">
                    +<span class="mono-num">{{ im.importedCount }}</span> · {{ im.importedByName }} · {{ importWhen(im.createdUtc) }}
                  </span>
                </div>
              }
            </div>
          }
        }
      </div>
    </app-bs-pull-refresh>

    <!-- ─── IMPORT FAB (the only "add" path — a Rocket Money CSV) ─── -->
    @if (!loading() && !errored() && hasData()) {
      <app-bs-fab icon="upload_file" label="Import CSV" [extended]="true" [fixed]="true"
                  [disabled]="importing()" (action)="pickFile()" />
    }

    <!-- a hidden file input the FAB / empty-state CTA trigger -->
    <input #fileInput type="file" accept=".csv,text/csv" hidden (change)="onPick($event)" aria-hidden="true" />

    <!-- ─────────────── TRANSACTION DETAIL SHEET ─────────────── -->
    <app-bs-sheet [(open)]="txnOpen" detent="peek" [label]="selected()?.merchant || 'Transaction'">
      @if (selected(); as t) {
        <div class="td">
          <div class="td__head">
            <span class="td__glyph" [class]="'is-' + t.kind" aria-hidden="true">
              <mat-icon>{{ kindIcon(t.kind) }}</mat-icon>
            </span>
            <div class="td__titles">
              <h3 class="td__merchant">{{ t.merchant }}</h3>
              <span class="td__sub">{{ txnDate(t.date) }}</span>
            </div>
            <span class="td__amt mono-num" [class.is-in]="t.kind === 'income'">{{ signedMoney(t) }}</span>
          </div>
          <dl class="td__rows">
            <div class="td__row">
              <dt>Account</dt><dd>{{ t.accountName }}</dd>
            </div>
            <div class="td__row">
              <dt>Owner</dt>
              <dd>
                <span class="td__owner-dot" [style.background]="ownerColor(t.owner)" aria-hidden="true"></span>
                {{ ownerLabel(t.owner) }}
              </dd>
            </div>
            @if (t.category) {
              <div class="td__row"><dt>Category</dt><dd>{{ t.category }}</dd></div>
            }
            <div class="td__row">
              <dt>Type</dt><dd class="td__kind is-{{ t.kind }}">{{ kindLabel(t.kind) }}</dd>
            </div>
          </dl>
        </div>
      }
    </app-bs-sheet>

    <app-bs-toaster />
  `,
  styleUrl: './family-finance-mobile.page.scss',
})
export class FamilyFinanceMobilePage {
  private api = inject(Api);
  private toast = inject(ToastController);
  private destroyRef = inject(DestroyRef);

  // ---- page state ----
  readonly loading = signal(true);
  readonly errored = signal(false);
  readonly refreshing = signal(false);

  /** The viewed month as `yyyy-MM`; defaults to the current month and prev/next steps it. */
  readonly month = signal<string>(this.currentMonth());

  readonly summary = signal<FinanceSummary | null>(null);
  readonly accounts = signal<FinanceAccount[]>([]);
  readonly imports = signal<FinanceImportBatch[]>([]);

  // ---- ✨ AI (read-only narration; best-effort, never blocks) ----
  readonly aiSummary = signal<FinanceSummaryAiResult | null>(null);
  private aiSummaryMonth = '';
  readonly coach = signal<FinanceMoneyCoachResult | null>(null);
  readonly coachLoading = signal(false);

  // ---- transactions (recent strip for the viewed month) ----
  readonly txns = signal<FinanceTransaction[]>([]);
  readonly txnTotal = signal(0);
  readonly txnLoading = signal(false);

  // ---- detail sheet ----
  readonly txnOpen = signal(false);
  readonly selected = signal<FinanceTransaction | null>(null);

  // ---- import ----
  readonly importing = signal(false);
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  /** Which detail list the segmented control shows. */
  readonly tab = signal<DetailTab>('spending');

  readonly skeletonCells = Array.from({ length: 4 }, (_, i) => i);

  readonly tabSegments = computed<Segment[]>(() => [
    { key: 'spending', label: 'Spending' },
    { key: 'recurring', label: `Recurring${this.coach()?.recurring.length ? ' · ' + this.coach()!.recurring.length : ''}` },
  ]);

  /** A friendly "June 2026" label for the month stepper. */
  readonly monthLabel = computed(() => {
    const [y, m] = this.month().split('-').map(Number);
    if (!y || !m) return this.month();
    return new Date(y, m - 1, 1).toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
  });

  /** Net = income − spent for the headline card. */
  readonly net = computed(() => {
    const s = this.summary();
    return s ? s.totalIncome - s.totalSpent : 0;
  });

  /** By-category spending slices (sorted as the server returned them). */
  readonly categories = computed(() => this.summary()?.byCategory ?? []);

  /** The His/Hers/Joint owner split rows with a positive amount, in a stable order. */
  readonly ownerRows = computed(() => {
    const owners = this.summary()?.byOwner ?? [];
    const order: FinanceOwner[] = ['his', 'hers', 'joint', 'unassigned'];
    return order
      .map((o) => owners.find((x) => x.owner === o))
      .filter((x): x is NonNullable<typeof x> => !!x && x.amount > 0);
  });

  /** The largest category amount, for scaling the spending bars. */
  private readonly maxCategory = computed(() =>
    Math.max(1, ...this.categories().map((c) => c.amount)));

  /** The largest owner amount, for scaling the owner split bars. */
  private readonly maxOwner = computed(() =>
    Math.max(1, ...this.ownerRows().map((o) => o.amount)));

  /** Whether anything's been imported yet (drives the empty/first-run state). */
  readonly hasData = computed(
    () =>
      this.accounts().length > 0 ||
      this.categories().length > 0 ||
      this.imports().length > 0,
  );

  constructor() {
    void this.reload(true);
  }

  // ─────────────── LOAD ───────────────

  async reload(initial = false): Promise<void> {
    if (initial) this.loading.set(true); else this.refreshing.set(true);
    this.errored.set(false);
    try {
      const [summary, accounts, imports] = await Promise.all([
        firstValueFrom(this.api.financeSummary(this.month())),
        firstValueFrom(this.api.financeAccounts().pipe(catchError(() => of<FinanceAccount[]>([])))),
        firstValueFrom(this.api.financeImports().pipe(catchError(() => of<FinanceImportBatch[]>([])))),
      ]);
      this.summary.set(summary ?? null);
      this.accounts.set(accounts ?? []);
      this.imports.set(imports ?? []);
      // The server resolves the month (it may fall back to the latest with data) — follow it.
      if (summary?.month && summary.month !== this.month()) this.month.set(summary.month);
      this.loadTxns();
      this.loadAiSummary();
      this.loadCoach(!initial);
    } catch {
      this.errored.set(true);
    } finally {
      this.loading.set(false);
      if (!initial) {
        this.refreshing.set(false);
        this.toast.show('Finances refreshed', { tone: 'success', durationMs: 1600 });
      }
    }
  }

  /** Page-1 of the viewed month's transactions (newest-first); best-effort. */
  private loadTxns(): void {
    this.txnLoading.set(true);
    this.api
      .financeTransactions({ month: this.month(), page: 1 })
      .pipe(
        catchError(() => of<FinanceTransactionsPage | null>(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((p) => {
        if (p) {
          this.txns.set(p.items);
          this.txnTotal.set(p.total);
        }
        this.txnLoading.set(false);
      });
  }

  /** The read-only "✨ Explain this month" narration; degrades silently to hidden on any blip. */
  private loadAiSummary(force = false): void {
    const month = this.month();
    if (!force && month === this.aiSummaryMonth && this.aiSummary()) return;
    this.aiSummaryMonth = month;
    this.api
      .financeSummaryAi(month)
      .pipe(
        catchError(() => of<FinanceSummaryAiResult | null>(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((s) => {
        if (this.month() === month) this.aiSummary.set(s);
      });
  }

  /** The deterministic recurring-charges floor (+ optional warm narration); month-independent. */
  private loadCoach(force = false): void {
    if (this.coachLoading()) return;
    if (!force && this.coach()) return;
    this.coachLoading.set(true);
    this.api
      .financeMoneyCoachAi()
      .pipe(
        catchError(() => of<FinanceMoneyCoachResult | null>(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((c) => {
        this.coach.set(c);
        this.coachLoading.set(false);
      });
  }

  // ─────────────── MONTH STEPPER ───────────────

  stepMonth(delta: number): void {
    const [y, m] = this.month().split('-').map(Number);
    const d = new Date(y, m - 1 + delta, 1);
    this.month.set(`${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`);
    this.refreshMonth();
  }

  /** Re-pull the month-scoped data (summary + txns + AI) after a month change. */
  private refreshMonth(): void {
    this.api
      .financeSummary(this.month())
      .pipe(
        catchError(() => of<FinanceSummary | null>(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((s) => {
        if (s) {
          this.summary.set(s);
          if (s.month && s.month !== this.month()) this.month.set(s.month);
        }
      });
    this.loadTxns();
    this.loadAiSummary();
  }

  setTab(key: string): void {
    this.tab.set(key === 'recurring' ? 'recurring' : 'spending');
  }

  // ─────────────── DETAIL SHEET ───────────────

  openTxn(t: FinanceTransaction): void {
    this.selected.set(t);
    this.txnOpen.set(true);
  }

  // ─────────────── IMPORT (reuse the live CSV path verbatim) ───────────────

  /** Open the native file picker (FAB + first-run CTA). */
  pickFile(): void {
    if (this.importing()) return;
    this.fileInput()?.nativeElement.click();
  }

  onPick(e: Event): void {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) void this.readAndImport(file);
    input.value = ''; // allow re-picking the same file
  }

  /** Read the chosen CSV as text in the browser, POST it, toast the de-duped result, and refresh. */
  private async readAndImport(file: File): Promise<void> {
    if (this.importing()) return;
    if (!/\.csv$/i.test(file.name)) {
      this.toast.show('Please choose a .csv exported from Rocket Money.', { tone: 'warn' });
      return;
    }
    this.importing.set(true);
    try {
      const content = await file.text();
      const res = await firstValueFrom(this.api.importFinanceCsv(file.name, content));
      const dup = res.skipped === 1 ? 'duplicate' : 'duplicates';
      this.toast.show(`Imported ${res.imported}, skipped ${res.skipped} ${dup}`,
        { tone: 'success', durationMs: 2600 });
      await this.reload();
    } catch {
      this.toast.show("Couldn't import that file — try again", { tone: 'warn' });
    } finally {
      this.importing.set(false);
    }
  }

  // ─────────────── formatting + helpers ───────────────

  ownerLabel(o: FinanceOwner): string { return OWNER_LABEL[o] ?? o; }
  ownerColor(o: FinanceOwner): string { return OWNER_COLOR[o] ?? OWNER_COLOR.unassigned; }

  kindLabel(k: FinanceTxnKind): string {
    return k === 'expense' ? 'Expense' : k === 'income' ? 'Income' : 'Transfer';
  }
  kindIcon(k: FinanceTxnKind): string {
    return k === 'income' ? 'south_west' : k === 'transfer' ? 'swap_horiz' : 'north_east';
  }

  /** % width of a category bar relative to the month's biggest category. */
  barPct(amount: number): number {
    return Math.max(3, Math.round((amount / this.maxCategory()) * 100));
  }
  /** % width of an owner-split bar relative to the biggest owner slice. */
  ownerPct(amount: number): number {
    return Math.max(4, Math.round((amount / this.maxOwner()) * 100));
  }

  /** A signed currency string for a transaction (income +, expense −). */
  signedMoney(t: FinanceTransaction): string {
    const sign = t.kind === 'income' ? '' : '−';
    return `${sign}${this.money(t.magnitude)}`;
  }

  /** A currency string, e.g. "$1,234.56". */
  money(n: number): string {
    return n.toLocaleString(undefined, { style: 'currency', currency: 'USD' });
  }

  /** A friendly "Jun 18, 2026" from an ISO `yyyy-MM-dd`. */
  txnDate(iso: string): string {
    const d = new Date(`${iso}T00:00:00`);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  }

  /** A friendly absolute date+time for the import-history strip (from a UTC ISO string). */
  importWhen(utc: string): string {
    const d = new Date(utc);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  txnAria(t: FinanceTransaction): string {
    return `${t.merchant}, ${this.signedMoney(t)}, ${this.txnDate(t.date)}${t.category ? ', ' + t.category : ''}. Open details.`;
  }

  private currentMonth(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
  }
}
