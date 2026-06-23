import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { CacheEfficiency, SummaryResponse } from '../../../core/models';
import { CompactPipe } from '../../../shared/format';

/**
 * The HERO glass card — the glanceable headline: total cost (big, tabular numerals) over the active
 * range, with a secondary stat row (tokens / records / cache-hit %). All figures come straight from
 * `summary.total` and `cacheEfficiency`, so they match the live dashboard exactly.
 *
 * Cache-hit degrades GRACEFULLY: when `cacheEff` is null (errored) or empty, the chip is hidden
 * rather than blocking the card — never block first paint on the cache endpoint.
 */
@Component({
  selector: 'app-pulse-hero',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CompactPipe],
  template: `
    <div class="hero" [class.hero--load]="loading() && !summary()">
      <div class="hero__cost-wrap">
        <span class="hero__cost-cur" aria-hidden="true">$</span>
        <span class="hero__cost">{{ costText() }}</span>
      </div>
      <p class="hero__sub">{{ rangeLabel() }}</p>

      <div class="hero__stats">
        <div class="stat">
          <span class="stat__val">{{ (summary()?.total?.totalTokens ?? 0) | compact }}</span>
          <span class="stat__key">tokens</span>
        </div>
        <div class="stat">
          <span class="stat__val">{{ (summary()?.total?.records ?? 0) | compact }}</span>
          <span class="stat__key">records</span>
        </div>
        @if (showCache()) {
          <div class="stat stat--cache">
            <span class="stat__val">{{ cachePct() }}%</span>
            <span class="stat__key">cache hit</span>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .hero {
      display: flex; flex-direction: column; align-items: flex-start; gap: 4px;
      padding: 24px 22px;
      border-radius: var(--r-glass);
      background:
        radial-gradient(120% 90% at 0% 0%, color-mix(in srgb, var(--cost-a) 16%, transparent), transparent 60%),
        var(--pulse-glass);
      backdrop-filter: blur(var(--blur-glass)) saturate(1.4);
      -webkit-backdrop-filter: blur(var(--blur-glass)) saturate(1.4);
      border: 1px solid var(--pulse-edge);
      box-shadow: var(--lift-2);
    }

    .hero__cost-wrap { display: flex; align-items: baseline; gap: 4px; line-height: 1; }
    .hero__cost-cur {
      font-family: var(--hero-num-family); font-weight: 700; font-size: 24px; color: var(--cost-a);
    }
    .hero__cost {
      font-family: var(--hero-num-family); font-weight: 700; font-size: var(--hero-num-size);
      line-height: 1; color: var(--pulse-ink); font-variant-numeric: tabular-nums; letter-spacing: -0.02em;
    }
    .hero__sub { margin: 6px 0 0; font-size: 13px; color: var(--pulse-ink-dim); }

    .hero__stats {
      display: flex; flex-wrap: wrap; gap: 22px; margin-top: 18px;
      width: 100%; padding-top: 16px; border-top: 1px solid var(--pulse-edge);
    }
    .stat { display: flex; flex-direction: column; gap: 2px; }
    .stat__val { font-size: 19px; font-weight: 700; color: var(--pulse-ink); font-variant-numeric: tabular-nums; }
    .stat__key { font-size: 11px; letter-spacing: .05em; text-transform: uppercase; color: var(--pulse-ink-dim); }
    .stat--cache .stat__val { color: var(--cache-hit); }
  `],
})
export class PulseHeroCard {
  readonly summary = input<SummaryResponse | null>(null);
  readonly cacheEff = input<CacheEfficiency | null>(null);
  readonly loading = input<boolean>(false);
  readonly rangeLabel = input<string>('All time');

  /** Big cost numeral with grouping + 2 decimals (matches the live dashboard's tabular cost). */
  readonly costText = computed(() => {
    const c = this.summary()?.total?.costUsd ?? 0;
    return c.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  });

  readonly cachePct = computed(() => Math.round((this.cacheEff()?.cacheReadRatio ?? 0) * 100));

  /** Empty-state guard copied from the live dashboard (no input + no cache reads => hide). */
  readonly showCache = computed(() => {
    const c = this.cacheEff();
    if (!c) return false;
    return !(c.cacheReadTokens === 0 && c.inputTokens === 0 && c.cacheWriteTokens === 0);
  });
}
