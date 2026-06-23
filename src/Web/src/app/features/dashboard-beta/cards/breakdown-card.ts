import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { EChartsOption } from 'echarts';

import { ChartComponent } from '../../../shared/chart';

/** One ranked breakdown row — name + cost, dimension-agnostic. */
export interface BreakdownSlice {
  readonly name: string;
  readonly costUsd: number;
  /** True when this slice's pricing is estimated (drives the "estimated" chip). Only models carry it. */
  readonly estimated?: boolean;
}

export type BreakdownDim = 'model' | 'source' | 'project';

/**
 * Tap-to-flip BREAKDOWN card. A donut (lifted from the live dashboard's `modelChart`) plus a ranked
 * top-5 list with proportional cost meters. A 3-seg toggle flips the dimension (Model / Source /
 * Project); the page supplies the slices per dimension (fetched via the SAME `Api.summary` grouped by
 * that dimension, so totals match the live page). An "estimated" chip appears when any visible model
 * slice uses placeholder pricing.
 */
@Component({
  selector: 'app-pulse-breakdown',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChartComponent, DecimalPipe],
  template: `
    <div class="bd">
      <header class="bd__head">
        <h2 class="bd__title">Breakdown</h2>
        @if (hasEstimated()) {
          <span class="bd__chip" title="Some pricing is estimated (placeholder rates)">estimated</span>
        }
      </header>

      <div class="bd__seg" role="group" aria-label="Breakdown dimension">
        <button type="button" class="seg" [class.seg--on]="dim() === 'model'"
                (click)="dimChange.emit('model')">Model</button>
        <button type="button" class="seg" [class.seg--on]="dim() === 'source'"
                (click)="dimChange.emit('source')">Source</button>
        <button type="button" class="seg" [class.seg--on]="dim() === 'project'"
                (click)="dimChange.emit('project')">Project</button>
      </div>

      @if (top().length) {
        <div class="bd__chart">
          <app-chart [option]="donut()"></app-chart>
        </div>

        <ol class="bd__list">
          @for (s of top(); track s.name; let i = $index) {
            <li class="row">
              <span class="row__rank">{{ i + 1 }}</span>
              <span class="row__name" [title]="s.name">{{ s.name }}</span>
              <span class="row__cost">\${{ s.costUsd | number:'1.2-2' }}</span>
              <span class="row__meter" aria-hidden="true">
                <span class="row__fill" [style.width.%]="pct(s.costUsd)"></span>
              </span>
            </li>
          }
        </ol>
      } @else {
        <p class="bd__empty">{{ loading() ? 'Loading…' : 'No cost in this range' }}</p>
      }
    </div>
  `,
  // number pipe is in CommonModule; import it via the standalone DecimalPipe below to stay lean.
  styles: [`
    :host { display: block; }
    .bd { display: flex; flex-direction: column; gap: 14px; }
    .bd__head { display: flex; align-items: center; gap: 10px; }
    .bd__title { margin: 0; font-size: 16px; font-weight: 700; color: var(--pulse-ink); }
    .bd__chip {
      font-size: 11px; font-weight: 600; letter-spacing: .03em; padding: 3px 9px; border-radius: var(--r-pill);
      background: color-mix(in srgb, var(--warn, #f0a35a) 22%, var(--pulse-rise));
      color: var(--warn, #f0a35a); border: 1px solid color-mix(in srgb, var(--warn, #f0a35a) 40%, transparent);
    }

    .bd__seg {
      display: inline-flex; gap: 4px; background: var(--pulse-rise); border: 1px solid var(--pulse-edge);
      border-radius: var(--r-pill); padding: 4px; width: fit-content;
    }
    .seg {
      min-height: 44px; padding: 0 16px; border: 0; border-radius: var(--r-pill);
      background: none; color: var(--pulse-ink-dim); font: inherit; font-size: 13px; cursor: pointer;
      transition: background 160ms var(--ease-out), color 160ms var(--ease-out);
    }
    .seg--on { background: var(--tok-a); color: #07101f; font-weight: 600; }

    .bd__chart { width: 100%; height: 220px; }
    .bd__chart app-chart { display: block; width: 100%; height: 100%; }

    .bd__list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
    .row {
      display: grid; grid-template-columns: 22px 1fr auto; grid-template-rows: auto auto;
      align-items: center; gap: 2px 10px;
    }
    .row__rank {
      grid-row: 1 / 3; font-size: 13px; font-weight: 700; color: var(--pulse-ink-dim);
      width: 22px; text-align: center; font-variant-numeric: tabular-nums;
    }
    .row__name { font-size: 14px; color: var(--pulse-ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row__cost { font-size: 14px; font-weight: 700; color: var(--pulse-ink); font-variant-numeric: tabular-nums; }
    .row__meter {
      grid-column: 2 / 4; height: 5px; border-radius: var(--r-pill); background: var(--pulse-edge); overflow: hidden;
    }
    .row__fill {
      display: block; height: 100%; border-radius: var(--r-pill);
      background: linear-gradient(90deg, var(--tok-a), var(--cost-a));
    }
    .bd__empty { margin: 8px 0; color: var(--pulse-ink-dim); font-size: 14px; }
  `],
})
export class PulseBreakdownCard {
  /** Full ranked slices for the active dimension (page supplies, sorted desc by cost). */
  readonly slices = input<BreakdownSlice[]>([]);
  readonly dim = input.required<BreakdownDim>();
  readonly loading = input<boolean>(false);

  /** Emitted when the user flips the dimension toggle (page swaps the slices). */
  readonly dimChange = output<BreakdownDim>();

  /** Top 5 cost-bearing slices. */
  readonly top = computed(() => this.slices().filter(s => s.costUsd > 0).slice(0, 5));

  readonly hasEstimated = computed(() => this.top().some(s => s.estimated));

  private readonly maxCost = computed(() => Math.max(0, ...this.top().map(s => s.costUsd)));
  pct(c: number): number {
    const m = this.maxCost();
    return m > 0 ? (c / m) * 100 : 0;
  }

  readonly donut = computed<EChartsOption>(() => {
    const data = this.slices().filter(s => s.costUsd > 0);
    if (data.length === 0) {
      const text = this.loading() ? 'Loading…' : 'No data';
      return { title: { text, left: 'center', top: 'center', textStyle: { color: '#5e6c82' } } };
    }
    return {
      tooltip: { trigger: 'item', formatter: (p: any) => `${p.name}: $${Number(p.value).toLocaleString()} (${p.percent}%)` },
      legend: { show: false },
      series: [{
        type: 'pie', radius: ['45%', '72%'], avoidLabelOverlap: true, label: { show: false },
        itemStyle: { borderColor: '#0a0d16', borderWidth: 2 },
        data: data.map(s => ({ name: s.name, value: +s.costUsd.toFixed(2) })),
      }],
    };
  });
}
