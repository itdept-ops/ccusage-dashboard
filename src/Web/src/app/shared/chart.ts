import {
  Component,
  ElementRef,
  OnDestroy,
  afterNextRender,
  effect,
  inject,
  input,
  viewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import * as echarts from 'echarts/core';
import type { EChartsOption } from 'echarts';
import { LineChart, BarChart, PieChart, ScatterChart, HeatmapChart } from 'echarts/charts';
import {
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  VisualMapComponent,
  CalendarComponent,
  MarkLineComponent,
  MarkPointComponent,
  MarkAreaComponent,
  DatasetComponent,
  DataZoomComponent,
  ToolboxComponent,
  GraphicComponent,
  AriaComponent,
} from 'echarts/components';
import { LabelLayout, UniversalTransition } from 'echarts/features';
import { CanvasRenderer } from 'echarts/renderers';

import { ThemeService } from '../core/theme';

// Tree-shaken ECharts registration (replaces the whole-library `import * as echarts from 'echarts'`).
// EXHAUSTIVE list of what the app's chart options actually use, enumerated from a full grep of
// src/app for every series `type:` and every component-mapping option key. Series: line, bar, pie,
// scatter, heatmap. Components: grid (xAxis/yAxis), tooltip, legend, title, visualMap, calendar,
// markLine. The rest (markPoint/markArea/dataset/dataZoom/toolbox/graphic/aria + label/transition
// features) are included defensively — each costs ~1KB but a MISSING one breaks a chart silently at
// runtime. If a NEW chart introduces a series/component not listed here, add it or that chart renders blank.
echarts.use([
  LineChart,
  BarChart,
  PieChart,
  ScatterChart,
  HeatmapChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  VisualMapComponent,
  CalendarComponent,
  MarkLineComponent,
  MarkPointComponent,
  MarkAreaComponent,
  DatasetComponent,
  DataZoomComponent,
  ToolboxComponent,
  GraphicComponent,
  AriaComponent,
  LabelLayout,
  UniversalTransition,
  CanvasRenderer,
]);

/**
 * The categorical series palette — Claude blue, Codex violet, then data accents (cyan, success, warn,
 * error, lit blue/violet). These saturated hues read on BOTH the dark console and a light canvas, so the
 * palette is shared; only the chrome (axes, labels, tooltip, grid) flips with the theme below.
 */
const SERIES_COLORS = ['#3d8bff', '#8b7cff', '#3fd8d0', '#3dd68c', '#f2b340', '#ff5c6c', '#5ba3ff', '#a99bff'];

/** Per-theme chrome colors (everything that must contrast with the surface, not the series). */
interface ChartChrome {
  text: string; // legend / general text
  title: string; // chart title
  inactive: string; // dimmed legend item
  axisLine: string;
  axisLabel: string;
  splitLine: string;
  tooltipBg: string;
  tooltipBorder: string;
  tooltipText: string;
  tooltipShadow: string;
  pointer: string;
}

const DARK_CHROME: ChartChrome = {
  text: '#9ba9bd',
  title: '#e6edf6',
  inactive: '#5e6c82',
  axisLine: '#26303f',
  axisLabel: '#5e6c82',
  splitLine: 'rgba(28,37,51,0.7)',
  tooltipBg: 'rgba(16,21,32,0.86)',
  tooltipBorder: '#33425a',
  tooltipText: '#e6edf6',
  tooltipShadow: '0 24px 60px -20px rgba(0,0,0,.8)',
  pointer: 'rgba(61,139,255,0.5)',
};

const LIGHT_CHROME: ChartChrome = {
  text: '#4a5a70',
  title: '#16202e',
  inactive: '#9aa7b8',
  axisLine: '#d4dae4',
  axisLabel: '#6b7a8f',
  splitLine: 'rgba(15,23,42,0.08)',
  tooltipBg: 'rgba(255,255,255,0.94)',
  tooltipBorder: '#cbd4e1',
  tooltipText: '#16202e',
  tooltipShadow: '0 24px 56px -24px rgba(15,23,42,.34)',
  pointer: 'rgba(37,99,235,0.45)',
};

/** Read the live theme off <html data-theme> (set by the no-flash bootstrap + ThemeService). */
function currentChrome(): ChartChrome {
  const t =
    typeof document !== 'undefined' ? document.documentElement.dataset['theme'] : undefined;
  return t === 'light' ? LIGHT_CHROME : DARK_CHROME;
}

/** Build the base option (legend/tooltip/title chrome) for the given theme. */
function chartBase(c: ChartChrome): EChartsOption {
  return {
    backgroundColor: 'transparent',
    color: SERIES_COLORS,
    textStyle: { fontFamily: 'Inter, system-ui, sans-serif', color: c.text },
    title: { textStyle: { color: c.title, fontFamily: 'Inter, system-ui, sans-serif' } },
    legend: {
      textStyle: { color: c.text, fontFamily: 'Inter, system-ui, sans-serif', fontSize: 12 },
      icon: 'roundRect',
      itemWidth: 10,
      itemHeight: 10,
      inactiveColor: c.inactive,
    },
    tooltip: {
      backgroundColor: c.tooltipBg,
      borderColor: c.tooltipBorder,
      borderWidth: 1,
      textStyle: {
        color: c.tooltipText,
        fontFamily: 'JetBrains Mono, ui-monospace, monospace',
        fontSize: 12,
      },
      extraCssText: `border-radius:8px; backdrop-filter:blur(14px); box-shadow:${c.tooltipShadow};`,
      axisPointer: { type: 'line', lineStyle: { color: c.pointer, type: 'dashed' } },
    },
  };
}

/** Per-axis styling merged into every category/value axis, themed by the active chrome. */
function chartAxis(c: ChartChrome) {
  return {
    axisLine: { lineStyle: { color: c.axisLine } },
    axisTick: { show: false },
    axisLabel: {
      color: c.axisLabel,
      fontFamily: 'JetBrains Mono, ui-monospace, monospace',
      fontSize: 11,
    },
    splitLine: { lineStyle: { color: c.splitLine, type: 'dashed' } },
  };
}

/** Deep-merge AXON theme defaults (resolved for the CURRENT theme) under the caller's option. */
function withAxonTheme(option: EChartsOption): EChartsOption {
  const c = currentChrome();
  const base = chartBase(c);
  const axisDefaults = chartAxis(c);
  const themeAxis = (axis: unknown): unknown => {
    if (Array.isArray(axis)) return axis.map((a) => ({ ...axisDefaults, ...(a as object) }));
    if (axis && typeof axis === 'object') return { ...axisDefaults, ...(axis as object) };
    return axis;
  };
  const merged: EChartsOption = {
    ...base,
    ...option,
    title: { ...base.title, ...(option.title as object) },
    legend: { ...base.legend, ...(option.legend as object) },
    tooltip: { ...base.tooltip, ...(option.tooltip as object) },
  };
  if (option.color) merged.color = option.color;
  if ('xAxis' in option && option.xAxis)
    merged.xAxis = themeAxis(option.xAxis) as EChartsOption['xAxis'];
  if ('yAxis' in option && option.yAxis)
    merged.yAxis = themeAxis(option.yAxis) as EChartsOption['yAxis'];
  return merged;
}

/** Thin wrapper around echarts.init — pass an [option] and it (re)renders + auto-resizes. */
@Component({
  selector: 'app-chart',
  standalone: true,
  template: `<div #host class="chart-host"></div>`,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: `
    .chart-host {
      display: block;
      width: 100%;
      height: 100%;
      min-height: 300px;
    }
  `,
})
export class ChartComponent implements OnDestroy {
  readonly option = input.required<EChartsOption>();
  private host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private chart?: echarts.ECharts;
  private ro?: ResizeObserver;
  private rafId = 0;
  private readonly theme = inject(ThemeService);

  constructor() {
    afterNextRender(() => {
      this.chart = echarts.init(this.host().nativeElement, undefined, { renderer: 'canvas' });
      this.chart.setOption(withAxonTheme(this.option()));
      // Defer resize to the next frame so echarts' own layout change doesn't
      // re-trigger the observer synchronously ("ResizeObserver loop" errors).
      this.ro = new ResizeObserver(() => {
        if (this.rafId) cancelAnimationFrame(this.rafId);
        this.rafId = requestAnimationFrame(() => this.chart?.resize());
      });
      this.ro.observe(this.host().nativeElement);
    });

    // Re-apply on either a new [option] OR a live theme switch: reading theme.resolved() registers the
    // effect as a dependency, so toggling light/dark re-themes every mounted chart's axes/tooltip/labels.
    effect(() => {
      const opt = this.option();
      this.theme.resolved();
      this.chart?.setOption(withAxonTheme(opt), true);
    });
  }

  ngOnDestroy(): void {
    if (this.rafId) cancelAnimationFrame(this.rafId);
    this.ro?.disconnect();
    this.chart?.dispose();
  }
}
