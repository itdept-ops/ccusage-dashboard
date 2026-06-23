import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { EChartsOption } from 'echarts';

import { GroupBy, SummaryResponse } from '../../../core/models';
import { ChartComponent } from '../../../shared/chart';

type Metric = 'cost' | 'tokens';

/**
 * ONE honest time-series card. A single ECharts series — Cost OR Tokens, flipped by a local 2-seg
 * toggle (no network) — over the day/month buckets. The day/month toggle changes `groupBy`, which is
 * the ONLY control here that re-fetches (emitted up to the page). The chart option is lifted from the
 * live dashboard's time branch (same `s.buckets` mapping, same grid), so the curve matches the live
 * page for the same filter. `ChartComponent` applies the AXON theme + a reduced-motion-safe render
 * (it is canvas + ResizeObserver; no entry animation is forced).
 */
@Component({
  selector: 'app-pulse-trend',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChartComponent],
  template: `
    <div class="trend">
      <header class="trend__head">
        <h2 class="trend__title">Trend</h2>
        <div class="trend__seg" role="group" aria-label="Group by">
          <button type="button" class="seg" [class.seg--on]="groupBy() === 'day'"
                  (click)="setGroup('day')">Day</button>
          <button type="button" class="seg" [class.seg--on]="groupBy() === 'month'"
                  (click)="setGroup('month')">Month</button>
        </div>
      </header>

      <div class="trend__seg trend__metric" role="group" aria-label="Metric">
        <button type="button" class="seg" [class.seg--cost]="metric() === 'cost'"
                [class.seg--on]="metric() === 'cost'" (click)="metric.set('cost')">Cost</button>
        <button type="button" class="seg" [class.seg--tok]="metric() === 'tokens'"
                [class.seg--on]="metric() === 'tokens'" (click)="metric.set('tokens')">Tokens</button>
      </div>

      <div class="trend__chart">
        <app-chart [option]="chart()"></app-chart>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .trend { display: flex; flex-direction: column; gap: 12px; }
    .trend__head { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
    .trend__title { margin: 0; font-size: 16px; font-weight: 700; color: var(--pulse-ink); }

    .trend__seg {
      display: inline-flex; gap: 4px; background: var(--pulse-rise); border: 1px solid var(--pulse-edge);
      border-radius: var(--r-pill); padding: 4px; width: fit-content;
    }
    .trend__metric { align-self: flex-start; }
    .seg {
      min-height: 44px; padding: 0 18px; border: 0; border-radius: var(--r-pill);
      background: none; color: var(--pulse-ink-dim); font: inherit; font-size: 13px; cursor: pointer;
      transition: background 160ms var(--ease-out), color 160ms var(--ease-out);
    }
    .seg--on { color: #07101f; font-weight: 600; background: var(--pulse-ink-dim); }
    .seg--on.seg--cost { background: var(--cost-a); }
    .seg--on.seg--tok { background: var(--tok-a); }

    .trend__chart { width: 100%; height: 300px; }
    .trend__chart app-chart { display: block; width: 100%; height: 100%; }
  `],
})
export class PulseTrendCard {
  readonly summary = input<SummaryResponse | null>(null);
  readonly loading = input<boolean>(false);
  readonly groupBy = input.required<GroupBy>();

  /** Bubbled up so the page can re-fetch the summary with the new groupBy. */
  readonly groupByChange = output<GroupBy>();

  readonly metric = signal<Metric>('cost');

  setGroup(g: GroupBy): void {
    if (this.groupBy() !== g) this.groupByChange.emit(g);
  }

  readonly chart = computed<EChartsOption>(() => {
    const s = this.summary();
    // Loading vs resolved-empty guard (copied from the live dashboard).
    if (!s) {
      const text = this.loading() ? 'Loading…' : 'No data';
      return { title: { text, left: 'center', top: 'center', textStyle: { color: '#5e6c82' } } };
    }
    if (s.buckets.length === 0) {
      return { title: { text: 'No data', left: 'center', top: 'center', textStyle: { color: '#5e6c82' } } };
    }

    const keys = s.buckets.map(b => b.key);
    const isCost = this.metric() === 'cost';
    const data = isCost
      ? s.buckets.map(b => +b.costUsd.toFixed(2))
      : s.buckets.map(b => b.totalTokens);
    const seriesColor = isCost ? '#f472b6' : '#3d8bff';
    const areaTop = isCost ? 'rgba(244,114,182,0.28)' : 'rgba(61,139,255,0.28)';

    return {
      tooltip: {
        trigger: 'axis',
        valueFormatter: (v) => isCost ? '$' + Number(v).toLocaleString(undefined, { maximumFractionDigits: 2 }) : shortNum(Number(v)),
      },
      grid: { left: 8, right: 8, top: 24, bottom: 28, containLabel: true },
      xAxis: { type: 'category', data: keys, axisLabel: { rotate: keys.length > 14 ? 45 : 0 } },
      yAxis: {
        type: 'value',
        axisLabel: { formatter: (v: number) => isCost ? '$' + shortNum(v) : shortNum(v) },
      },
      series: [{
        name: isCost ? 'Cost (USD)' : 'Tokens',
        type: 'line', smooth: true, symbol: 'none',
        data,
        itemStyle: { color: seriesColor },
        lineStyle: { width: 2.5, color: seriesColor },
        areaStyle: {
          color: {
            type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
            colorStops: [{ offset: 0, color: areaTop }, { offset: 1, color: 'rgba(0,0,0,0)' }],
          },
        },
      }],
    };
  });
}

/** B/M/K compact formatter (copied from the live dashboard's shortNum). */
function shortNum(v: number): string {
  if (Math.abs(v) >= 1e9) return (v / 1e9).toFixed(1) + 'B';
  if (Math.abs(v) >= 1e6) return (v / 1e6).toFixed(1) + 'M';
  if (Math.abs(v) >= 1e3) return (v / 1e3).toFixed(1) + 'K';
  return `${v}`;
}
