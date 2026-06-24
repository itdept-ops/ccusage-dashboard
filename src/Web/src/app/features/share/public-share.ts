import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import type { EChartsOption } from 'echarts';

import { MatProgressBarModule } from '@angular/material/progress-bar';

import { Api } from '../../core/api';
import { PublicShare } from '../../core/models';
import { ChartComponent } from '../../shared/chart';
import { CompactPipe } from '../../shared/format';

@Component({
  selector: 'app-public-share',
  imports: [CommonModule, MatProgressBarModule, ChartComponent, CompactPipe],
  templateUrl: './public-share.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './public-share.scss',
})
export class PublicShareView {
  private api = inject(Api);
  private route = inject(ActivatedRoute);

  readonly data = signal<PublicShare | null>(null);
  readonly loading = signal(true);
  readonly error = signal(false);

  constructor() {
    const token = this.route.snapshot.paramMap.get('token') ?? '';
    this.api.publicShare(token).subscribe({
      next: (d) => {
        this.data.set(d);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  private label(key: string): string {
    return key.length > 24 ? key.slice(0, 10) + '…' + key.slice(-6) : key;
  }
  private short(v: number): string {
    if (Math.abs(v) >= 1e9) return (v / 1e9).toFixed(1) + 'B';
    if (Math.abs(v) >= 1e6) return (v / 1e6).toFixed(1) + 'M';
    if (Math.abs(v) >= 1e3) return (v / 1e3).toFixed(1) + 'K';
    return `${v}`;
  }

  readonly mainChart = computed<EChartsOption>(() => {
    const s = this.data()?.summary;
    if (!s || !s.buckets.length)
      return {
        title: { text: 'No data', left: 'center', top: 'center', textStyle: { color: '#5e6c82' } },
      };

    if (s.groupBy === 'day' || s.groupBy === 'month') {
      const keys = s.buckets.map((b) => b.key);
      return {
        tooltip: { trigger: 'axis' },
        legend: { data: ['Cost (USD)', 'Tokens'], top: 0 },
        grid: { left: 56, right: 60, top: 34, bottom: 48 },
        xAxis: { type: 'category', data: keys, axisLabel: { rotate: keys.length > 14 ? 45 : 0 } },
        yAxis: [
          { type: 'value', name: 'USD', axisLabel: { formatter: '${value}' } },
          { type: 'value', name: 'Tokens', axisLabel: { formatter: (v: number) => this.short(v) } },
        ],
        series: [
          {
            name: 'Cost (USD)',
            type: 'bar',
            data: s.buckets.map((b) => +b.costUsd.toFixed(2)),
            itemStyle: { color: '#f472b6', borderRadius: [4, 4, 0, 0] },
          },
          {
            name: 'Tokens',
            type: 'line',
            yAxisIndex: 1,
            smooth: true,
            symbol: 'none',
            data: s.buckets.map((b) => b.totalTokens),
            itemStyle: { color: '#3fd8d0' },
            lineStyle: { width: 2, color: '#3fd8d0' },
          },
        ],
      };
    }

    const top = s.buckets.slice(0, 15).reverse();
    return {
      tooltip: { trigger: 'axis', valueFormatter: (v) => '$' + Number(v).toLocaleString() },
      grid: { left: 150, right: 28, top: 12, bottom: 32 },
      xAxis: { type: 'value', axisLabel: { formatter: '${value}' } },
      yAxis: { type: 'category', data: top.map((b) => this.label(b.key)) },
      series: [
        {
          type: 'bar',
          data: top.map((b) => +b.costUsd.toFixed(2)),
          itemStyle: { color: '#f472b6', borderRadius: [0, 4, 4, 0] },
        },
      ],
    };
  });

  readonly modelChart = computed<EChartsOption>(() => {
    const ms = (this.data()?.models.buckets ?? []).filter((b) => b.costUsd > 0);
    return {
      tooltip: {
        trigger: 'item',
        formatter: (p: any) => `${p.name}: $${Number(p.value).toLocaleString()} (${p.percent}%)`,
      },
      legend: { bottom: 0, type: 'scroll' },
      series: [
        {
          type: 'pie',
          radius: ['45%', '72%'],
          avoidLabelOverlap: true,
          label: { show: false },
          itemStyle: { borderColor: '#111722', borderWidth: 2 },
          data: ms.map((b) => ({ name: b.key, value: +b.costUsd.toFixed(2) })),
        },
      ],
    };
  });
}
