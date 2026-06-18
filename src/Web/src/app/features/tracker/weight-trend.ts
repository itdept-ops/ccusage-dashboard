import { Component, computed, input } from '@angular/core';
import type { EChartsOption } from 'echarts';

import { WeightPointDto } from '../../core/models';
import { ChartComponent } from '../../shared/chart';
import { kgToLb, weightUnit } from './units';

/**
 * Weight-over-time trend (ECharts line) for the tracker dashboard. Points come from
 * GET /api/tracker/weight (metric kg, oldest-first); the chart converts to the chosen display unit and
 * draws an optional goal-weight reference line. Shows an empty state when there are no entries.
 */
@Component({
  selector: 'app-weight-trend',
  imports: [ChartComponent],
  template: `
    @if (points().length > 0) {
      <div class="wt-chart-host" role="img" [attr.aria-label]="ariaLabel()">
        <app-chart class="wt-chart" [option]="option()" />
      </div>
    } @else {
      <div class="wt-empty">
        <p>No weight logged yet.</p>
        <p class="wt-empty-sub">Use “Log weight” to start your trend.</p>
      </div>
    }
  `,
  styles: `
    .wt-chart-host { display: block; width: 100%; height: 240px; min-height: 240px; }
    .wt-chart { display: block; width: 100%; height: 100%; min-height: 240px; }
    .wt-empty { display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 2px; min-height: 180px; color: var(--tech-text-tertiary); }
    .wt-empty p { margin: 0; font-size: var(--tech-fs-body); }
    .wt-empty-sub { font-size: var(--tech-fs-label) !important; }
  `,
})
export class WeightTrend {
  readonly points = input.required<WeightPointDto[]>();
  /** Goal weight in kg (metric), or null for no reference line. */
  readonly goalWeightKg = input<number | null | undefined>(null);
  readonly imperial = input<boolean>(false);

  private readonly unit = computed(() => (this.imperial() ? 'lb' : 'kg'));

  /** Honor the OS "reduce motion" setting — disable chart animation/curve easing when set. */
  private readonly reduceMotion =
    typeof window !== 'undefined' &&
    !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

  private toDisp(kg: number): number {
    return this.imperial() ? Math.round(kgToLb(kg) * 10) / 10 : Math.round(kg * 10) / 10;
  }

  /**
   * Visually-hidden text equivalent of the populated chart for screen readers: latest weight, the
   * change across the window, the entry count, and the goal weight (which also covers the goal
   * reference line conveyed only by colour/position in the canvas). Mirrors the user's display units.
   */
  readonly ariaLabel = computed(() => {
    const pts = this.points();
    const unit = weightUnit(this.imperial());
    const n = pts.length;
    const latest = this.toDisp(pts[n - 1].weightKg);
    const first = this.toDisp(pts[0].weightKg);
    const delta = Math.round((latest - first) * 10) / 10;
    const days = pts.length > 1 ? this.spanDays(pts[0].date, pts[n - 1].date) : 0;

    const parts = [`Weight trend. Latest ${latest} ${unit}.`];
    if (n > 1) {
      const dir = delta > 0 ? 'up' : delta < 0 ? 'down' : 'no change';
      const mag = Math.abs(delta);
      const change = delta === 0 ? 'no change' : `${dir} ${mag} ${unit}`;
      const over = days > 0 ? ` over ${days} day${days === 1 ? '' : 's'}` : '';
      parts.push(`${change}${over}.`);
    }
    parts.push(`${n} entr${n === 1 ? 'y' : 'ies'}.`);

    const goal = this.goalWeightKg();
    if (goal != null && goal > 0) parts.push(`Goal ${this.toDisp(goal)} ${unit}.`);

    return parts.join(' ');
  });

  /** Whole days between two `yyyy-MM-dd` dates (oldest → newest). */
  private spanDays(from: string, to: string): number {
    const a = new Date(from + 'T00:00:00').getTime();
    const b = new Date(to + 'T00:00:00').getTime();
    return Math.max(0, Math.round((b - a) / 86_400_000));
  }

  readonly option = computed<EChartsOption>(() => {
    const pts = this.points();
    const unit = this.unit();
    const dates = pts.map(p => p.date);
    const values = pts.map(p => this.toDisp(p.weightKg));
    const goal = this.goalWeightKg();
    const goalDisp = goal != null && goal > 0 ? this.toDisp(goal) : null;

    const reduceMotion = this.reduceMotion;

    return {
      animation: !reduceMotion,
      grid: { left: 48, right: 16, top: 16, bottom: 28 },
      tooltip: {
        trigger: 'axis',
        valueFormatter: (v) => (typeof v === 'number' ? `${v} ${unit}` : String(v)),
      },
      xAxis: {
        type: 'category',
        data: dates,
        boundaryGap: false,
        axisLabel: {
          formatter: (d: string) => {
            const dt = new Date(d + 'T00:00:00');
            return dt.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
          },
        },
      },
      yAxis: {
        type: 'value',
        scale: true,
        axisLabel: { formatter: (v: number) => `${v}` },
      },
      series: [
        {
          type: 'line',
          name: `Weight (${unit})`,
          data: values,
          smooth: !reduceMotion,
          showSymbol: pts.length <= 60,
          symbolSize: 6,
          lineStyle: { width: 2 },
          areaStyle: { opacity: 0.12 },
          ...(goalDisp != null
            ? {
                markLine: {
                  silent: true,
                  symbol: 'none',
                  lineStyle: { color: '#3dd68c', type: 'dashed', width: 1.5 },
                  label: { formatter: `Goal ${goalDisp} ${unit}`, color: '#3dd68c', position: 'insideEndTop' },
                  data: [{ yAxis: goalDisp }],
                },
              }
            : {}),
        },
      ],
    };
  });
}
