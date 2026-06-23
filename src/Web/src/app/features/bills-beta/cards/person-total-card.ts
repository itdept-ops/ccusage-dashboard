import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

import { PersonTotalDto, PaymentHandlesDto } from '../../../core/models';

/** One pay-me link rendered from the owner's configured handles. Inlined to keep bills-beta isolated. */
interface PayLink {
  label: string;
  icon: string;
  url: string;
}

/**
 * Tally per-person total card — one slide in the horizontal snap rail. Shows the person's big total
 * numeral, their items-total + tax/tip-share breakdown, and inline pay chips from the owner's payment
 * handles. The amber "Unclaimed" variant (driven by `BillDto.unclaimedTotal`) reuses the same card with
 * `unclaimed=true` and never renders red.
 *
 * Pure presentation: no `Api`, no store. Inherits the Tally palette tokens from the page `:host`.
 */
@Component({
  selector: 'app-person-total-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencyPipe, MatIconModule],
  template: `
    <article class="ptc" [class.ptc--unclaimed]="unclaimed()">
      <header class="ptc__head">
        <span class="ptc__name">{{ unclaimed() ? 'Unclaimed' : person()!.name }}</span>
        @if (unclaimed()) {
          <mat-icon class="ptc__warn" aria-hidden="true">error_outline</mat-icon>
        }
      </header>

      <p class="ptc__total">{{ total() | currency: 'USD' }}</p>

      @if (!unclaimed()) {
        <dl class="ptc__split">
          <div><dt>Items</dt><dd>{{ person()!.itemsTotal | currency: 'USD' }}</dd></div>
          <div><dt>Tax + tip</dt><dd>{{ person()!.taxTipShare | currency: 'USD' }}</dd></div>
        </dl>

        @if (payLinks().length) {
          <div class="ptc__pay">
            @for (p of payLinks(); track p.label) {
              <a class="ptc__chip" [href]="p.url" target="_blank" rel="noopener"
                 (click)="$event.stopPropagation()">
                <mat-icon aria-hidden="true">{{ p.icon }}</mat-icon>{{ p.label }}
              </a>
            }
          </div>
        }
      } @else {
        <p class="ptc__hint">Items nobody has claimed yet — share the link or assign them.</p>
      }
    </article>
  `,
  styles: [`
    .ptc {
      flex: 0 0 auto;
      scroll-snap-align: start;
      width: 232px;
      box-sizing: border-box;
      padding: 16px;
      border-radius: var(--r-card);
      background: var(--paper-rise);
      box-shadow: var(--lift-2);
      border: 1px solid var(--rule);
      display: flex; flex-direction: column; gap: 8px;
    }
    .ptc--unclaimed {
      border-color: color-mix(in srgb, var(--owed) 40%, transparent);
      background: color-mix(in srgb, var(--owed) 10%, var(--paper-rise));
    }
    .ptc__head { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
    .ptc__name {
      font: 600 14px/1.2 var(--font-ui); color: var(--ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .ptc__warn { color: var(--owed); font-size: 18px; width: 18px; height: 18px; }
    .ptc__total {
      margin: 0; font: 700 30px/1 var(--font-num);
      color: var(--ink);
    }
    .ptc--unclaimed .ptc__total { color: var(--owed); }
    .ptc__split {
      margin: 0; display: flex; flex-direction: column; gap: 4px;
      div { display: flex; justify-content: space-between; gap: 8px; }
      dt { margin: 0; font: 500 12px/1.2 var(--font-ui); color: var(--ink-dim); }
      dd { margin: 0; font: 600 12px/1.2 var(--font-num); color: var(--ink-dim); }
    }
    .ptc__hint { margin: 0; font: 500 12px/1.4 var(--font-ui); color: var(--ink-dim); }
    .ptc__pay { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 2px; }
    .ptc__chip {
      display: inline-flex; align-items: center; gap: 4px;
      min-height: 36px; padding: 0 12px;
      border-radius: var(--r-pill);
      border: 1px solid var(--rule);
      background: var(--paper-sink);
      color: var(--me);
      font: 600 12px/1 var(--font-ui);
      text-decoration: none;
      mat-icon { font-size: 16px; width: 16px; height: 16px; }
    }
  `],
})
export class PersonTotalCard {
  /** The person roll-up to render (omitted/ignored when `unclaimed` is true). */
  readonly person = input<PersonTotalDto | null>(null);
  /** The owner's configured payment handles, used to build the inline pay chips. */
  readonly payments = input<PaymentHandlesDto | null>(null);
  /** When true the card renders the amber "Unclaimed" variant from `amount` (never red). */
  readonly unclaimed = input<boolean>(false);
  /** The unclaimed amount (only read when `unclaimed` is true). */
  readonly amount = input<number>(0);

  /** Unused output reserved for future "tap to focus this person" — kept off the template for now. */
  readonly select = output<void>();

  protected readonly total = computed(() =>
    this.unclaimed() ? this.amount() : (this.person()?.total ?? 0));

  /** Inline pay-link mapper (copied from the live Bills.toPayLinks 3-liner to preserve isolation). */
  protected readonly payLinks = computed<PayLink[]>(() => {
    if (this.unclaimed()) return [];
    const p = this.payments();
    if (!p) return [];
    const out: PayLink[] = [];
    if (p.cashApp) out.push({ label: 'CashApp', icon: 'attach_money', url: p.cashApp });
    if (p.payPal) out.push({ label: 'PayPal', icon: 'account_balance_wallet', url: p.payPal });
    if (p.venmo) out.push({ label: 'Venmo', icon: 'payments', url: p.venmo });
    return out;
  });
}
