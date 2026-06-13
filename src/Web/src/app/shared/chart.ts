import { Component, ElementRef, OnDestroy, afterNextRender, effect, input, viewChild } from '@angular/core';
import * as echarts from 'echarts';
import type { EChartsOption } from 'echarts';

/** Thin wrapper around echarts.init — pass an [option] and it (re)renders + auto-resizes. */
@Component({
  selector: 'app-chart',
  standalone: true,
  template: `<div #host class="chart-host"></div>`,
  styles: `.chart-host { display: block; width: 100%; height: 100%; min-height: 300px; }`,
})
export class ChartComponent implements OnDestroy {
  readonly option = input.required<EChartsOption>();
  private host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private chart?: echarts.ECharts;
  private ro?: ResizeObserver;
  private rafId = 0;

  constructor() {
    afterNextRender(() => {
      this.chart = echarts.init(this.host().nativeElement, undefined, { renderer: 'canvas' });
      this.chart.setOption(this.option());
      // Defer resize to the next frame so echarts' own layout change doesn't
      // re-trigger the observer synchronously ("ResizeObserver loop" errors).
      this.ro = new ResizeObserver(() => {
        if (this.rafId) cancelAnimationFrame(this.rafId);
        this.rafId = requestAnimationFrame(() => this.chart?.resize());
      });
      this.ro.observe(this.host().nativeElement);
    });

    effect(() => {
      const opt = this.option();
      this.chart?.setOption(opt, true);
    });
  }

  ngOnDestroy(): void {
    if (this.rafId) cancelAnimationFrame(this.rafId);
    this.ro?.disconnect();
    this.chart?.dispose();
  }
}
