import { ChangeDetectionStrategy, Component, computed, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { forkJoin } from 'rxjs';

import { Api } from '../../core/api';
import {
  CacheEfficiency, GroupBy, IngestionSource, MachineStat, ModelStat, PagedResult,
  ProjectDto, SummaryResponse, UsageFilter, UsageRecord,
} from '../../core/models';

import { PulseHeroCard } from './hero/hero-card';
import { PulseTrendCard } from './cards/trend-card';
import { PulseBreakdownCard, BreakdownDim, BreakdownSlice } from './cards/breakdown-card';
import { PulseRecentFeed } from './cards/recent-feed';
import { PulseFilterSheet } from './sheets/filter-sheet';

const PAGE_SIZE = 25;

/**
 * Dashboard "Pulse" — the mobile-first, isolated redesign of the live usage dashboard.
 *
 * DATA PARITY: every figure is sourced from the SAME endpoints the live page uses — `Api.summary`
 * (hero totals + trend buckets + breakdown), `Api.records` (recent feed), `Api.cacheEfficiency`
 * (cache-hit chip). The server does all dedup + sidechain aggregation; this page never re-aggregates
 * client-side. Sending the identical {@link UsageFilter} + {@link GroupBy} guarantees identical
 * numbers. The deep-link query scheme (`from/to/p/m/s/mc/sc/g/preset`) mirrors the live page so
 * shared links interoperate.
 *
 * ISOLATION: all design tokens live on this component's `:host` (Pulse palette + bottom-sheet alias
 * tokens) — NO global `--tech-*`. No live page is imported.
 */
@Component({
  selector: 'app-dashboard-beta',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatIconModule,
    PulseHeroCard, PulseTrendCard, PulseBreakdownCard, PulseRecentFeed, PulseFilterSheet,
  ],
  template: `
    <!-- Sticky range strip: scroll-snap range pills + filter button with an active-badge. -->
    <header class="pb-strip">
      <div class="pb-pills" role="group" aria-label="Date range">
        @for (p of presets; track p.key) {
          <button type="button" class="pill" [class.pill--on]="activePreset() === p.key"
                  (click)="setDatePreset(p.key)">{{ p.label }}</button>
        }
      </div>
      <button type="button" class="pb-filter" (click)="openFilter()" aria-label="Filters">
        <mat-icon aria-hidden="true">tune</mat-icon>
        @if (activeFilterCount()) { <span class="pb-filter__badge">{{ activeFilterCount() }}</span> }
      </button>
    </header>

    <main class="pb-scroll">
      <app-pulse-hero
        [summary]="summary()" [cacheEff]="cacheEff()" [loading]="loading()" [rangeLabel]="rangeLabel()" />

      <section class="pb-card pb-defer">
        <app-pulse-trend
          [summary]="summary()" [loading]="loading()" [groupBy]="groupBy()"
          (groupByChange)="setGroupBy($event)" />
      </section>

      <section class="pb-card pb-defer">
        <app-pulse-breakdown
          [slices]="breakdownSlices()" [dim]="breakdownDim()" [loading]="loading()"
          (dimChange)="setBreakdownDim($event)" />
      </section>

      <section class="pb-card pb-defer">
        <app-pulse-recent
          [page]="records()" [loading]="loading()" [loadingMore]="loadingMore()"
          (more)="loadMore()" />
      </section>
    </main>

    <app-pulse-filter-sheet #sheet
      [(open)]="filterOpen"
      [projects]="projects()" [models]="modelStats()" [sources]="sources()" [machines]="machines()"
      [filter]="filter()" [groupBy]="groupBy()"
      (applied)="onApplyFilters($event)" />
  `,
  styleUrl: './dashboard-beta.page.scss',
})
export class DashboardBetaPage {
  private api = inject(Api);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private readonly sheet = viewChild.required(PulseFilterSheet);

  // ---- filter + view state (shapes copied from the live dashboard for parity) ----
  readonly filter = signal<UsageFilter>({ from: null, to: null, projectIds: [], models: [], sources: [], machine: [], includeSidechain: true });
  readonly groupBy = signal<GroupBy>('day');
  readonly activePreset = signal<string>('all');
  readonly presets = [
    { key: '7d', label: '7d' }, { key: '30d', label: '30d' }, { key: '90d', label: '90d' },
    { key: 'mtd', label: 'Month' }, { key: 'all', label: 'All' },
  ] as const;

  // ---- option catalogs (for the filter sheet) ----
  readonly projects = signal<ProjectDto[]>([]);
  readonly modelStats = signal<ModelStat[]>([]);
  readonly sources = signal<IngestionSource[]>([]);
  readonly machines = signal<MachineStat[]>([]);

  // ---- data ----
  readonly summary = signal<SummaryResponse | null>(null);
  readonly records = signal<PagedResult<UsageRecord> | null>(null);
  readonly cacheEff = signal<CacheEfficiency | null>(null);
  /** Per-dimension breakdown summary (grouped by model/source/project via the SAME summary endpoint). */
  readonly breakdownSummary = signal<SummaryResponse | null>(null);

  readonly loading = signal(false);
  readonly loadingMore = signal(false);

  readonly page = signal(1);

  // ---- filter sheet ----
  readonly filterOpen = signal(false);

  // ---- breakdown dimension (local flip, fetched on change) ----
  readonly breakdownDim = signal<BreakdownDim>('model');

  /** Count of non-default filter constraints, shown as a badge on the filter button. */
  readonly activeFilterCount = computed(() => {
    const f = this.filter();
    return f.projectIds.length + f.models.length + f.sources.length + f.machine.length + (f.includeSidechain ? 0 : 1);
  });

  readonly rangeLabel = computed(() => {
    const key = this.activePreset();
    const found = this.presets.find(p => p.key === key);
    if (found && key !== 'all') return found.label === 'Month' ? 'This month' : `Last ${found.label}`;
    const f = this.filter();
    if (f.from && f.to) return `${f.from} → ${f.to}`;
    return 'All time';
  });

  /** Breakdown slices for the active dimension, mapped from the SAME server summary buckets. */
  readonly breakdownSlices = computed<BreakdownSlice[]>(() => {
    const s = this.breakdownSummary();
    if (!s) return [];
    const dim = this.breakdownDim();
    // Model dimension can carry the placeholder-pricing flag from the models catalog.
    const placeholderByModel = new Map(this.modelStats().map(m => [m.model, m.isPlaceholderPricing]));
    return s.buckets
      .map(b => ({
        name: b.key,
        costUsd: b.costUsd,
        estimated: dim === 'model' ? (placeholderByModel.get(b.key) ?? false) : false,
      }))
      .sort((a, b) => b.costUsd - a.costUsd);
  });

  constructor() {
    this.hydrateFromUrl();
    this.loadOptions();
    this.reloadAll();
    this.reloadBreakdown();
  }

  // ---- URL deep-linking (same query scheme as the live dashboard) ----
  private hydrateFromUrl(): void {
    const p = this.route.snapshot.queryParamMap;
    if (![...p.keys].length) return;
    const list = (k: string) => (p.get(k)?.split(',').filter(Boolean) ?? []);
    this.filter.set({
      from: p.get('from') || null,
      to: p.get('to') || null,
      projectIds: list('p').map(Number).filter(n => !Number.isNaN(n)),
      models: list('m'),
      sources: list('s'),
      machine: list('mc'),
      includeSidechain: p.get('sc') !== '0',
    });
    if (p.get('g')) this.groupBy.set(p.get('g') as GroupBy);
    this.activePreset.set(p.get('preset') || 'all');
  }

  private shareParams(): Record<string, string> {
    const f = this.filter();
    const q: Record<string, string> = {};
    if (f.from) q['from'] = f.from;
    if (f.to) q['to'] = f.to;
    if (f.projectIds.length) q['p'] = f.projectIds.join(',');
    if (f.models.length) q['m'] = f.models.join(',');
    if (f.sources.length) q['s'] = f.sources.join(',');
    if (f.machine.length) q['mc'] = f.machine.join(',');
    if (!f.includeSidechain) q['sc'] = '0';
    if (this.groupBy() !== 'day') q['g'] = this.groupBy();
    if (this.activePreset() && this.activePreset() !== 'all') q['preset'] = this.activePreset();
    return q;
  }

  private syncUrl(): void {
    this.router.navigate([], { relativeTo: this.route, queryParams: this.shareParams(), replaceUrl: true });
  }

  // ---- range pills (apply instantly) ----
  setDatePreset(kind: string): void {
    // Local-date formatter copied VERBATIM from the live dashboard so ranges match exactly
    // (never toISOString, which would shift the boundary across timezones).
    const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    const today = new Date();
    let from: string | null = null;
    if (kind === 'mtd') {
      from = fmt(new Date(today.getFullYear(), today.getMonth(), 1));
    } else if (kind !== 'all') {
      const days = kind === '7d' ? 6 : kind === '30d' ? 29 : 89;
      const d = new Date(today);
      d.setDate(d.getDate() - days);
      from = fmt(d);
    }
    const to = kind === 'all' ? null : fmt(today);
    this.activePreset.set(kind);
    this.filter.update(f => ({ ...f, from, to }));
    this.applyAndReload();
  }

  // ---- filter sheet ----
  openFilter(): void {
    // Seed the sheet's draft from the committed filter, then open.
    this.sheet().seed();
    this.filterOpen.set(true);
  }

  onApplyFilters(e: { filter: UsageFilter; groupBy: GroupBy }): void {
    this.filter.set(e.filter);
    this.groupBy.set(e.groupBy);
    // A manual filter selection clears the named-preset highlight unless it's still all-time.
    const f = e.filter;
    if (!f.from && !f.to) {
      if (this.activePreset() !== 'all') this.activePreset.set('all');
    } else {
      this.activePreset.set('');
    }
    this.applyAndReload();
  }

  private applyAndReload(): void {
    this.page.set(1);
    this.syncUrl();
    this.reloadAll();
    this.reloadBreakdown();
  }

  // ---- trend groupBy toggle (only the summary refetches) ----
  setGroupBy(g: GroupBy): void {
    this.groupBy.set(g);
    this.syncUrl();
    this.reloadSummary();
  }

  // ---- breakdown dimension toggle (only the breakdown summary refetches) ----
  setBreakdownDim(dim: BreakdownDim): void {
    this.breakdownDim.set(dim);
    this.reloadBreakdown();
  }

  // ---- data loads ----
  private loadOptions(): void {
    this.api.projects().subscribe({ next: p => this.projects.set(p), error: () => { /* non-critical */ } });
    this.api.models().subscribe({ next: m => this.modelStats.set(m), error: () => { /* non-critical */ } });
    this.api.sources().subscribe({ next: s => this.sources.set(s), error: () => { /* non-critical */ } });
    this.api.machines().subscribe({ next: m => this.machines.set(m), error: () => { /* non-critical */ } });
  }

  private reloadAll(): void {
    this.loading.set(true);
    forkJoin({
      summary: this.api.summary(this.filter(), this.groupBy()),
      records: this.api.records(this.filter(), 1, PAGE_SIZE, 'timestamp', true),
      cacheEff: this.api.cacheEfficiency(this.filter()),
    }).subscribe({
      next: r => {
        this.summary.set(r.summary);
        this.records.set(r.records);
        this.cacheEff.set(r.cacheEff);
        this.loading.set(false);
      },
      // Degrade the cache chip independently: a cache-efficiency 4xx must not blank the page. If the
      // whole forkJoin fails we still clear loading so the cards show their resolved-empty state.
      error: () => {
        this.loading.set(false);
        // Best-effort: try the two critical streams alone so a single bad endpoint doesn't kill the page.
        this.api.summary(this.filter(), this.groupBy()).subscribe({ next: s => this.summary.set(s), error: () => {} });
        this.api.records(this.filter(), 1, PAGE_SIZE, 'timestamp', true).subscribe({ next: rec => this.records.set(rec), error: () => {} });
        this.cacheEff.set(null);
      },
    });
  }

  private reloadSummary(): void {
    this.api.summary(this.filter(), this.groupBy()).subscribe({
      next: s => this.summary.set(s),
      error: () => { /* keep prior chart */ },
    });
  }

  /** Fetch the breakdown using the SAME summary endpoint, grouped by the active dimension. */
  private reloadBreakdown(): void {
    this.api.summary(this.filter(), this.breakdownDim()).subscribe({
      next: s => this.breakdownSummary.set(s),
      error: () => { /* keep prior breakdown */ },
    });
  }

  // ---- infinite scroll: append the next records page ----
  loadMore(): void {
    const cur = this.records();
    if (!cur || this.loadingMore()) return;
    if (cur.page >= Math.ceil(cur.total / cur.pageSize)) return;
    const next = cur.page + 1;
    this.loadingMore.set(true);
    this.api.records(this.filter(), next, PAGE_SIZE, 'timestamp', true).subscribe({
      next: r => {
        this.records.set({ ...r, items: [...cur.items, ...r.items] });
        this.page.set(next);
        this.loadingMore.set(false);
      },
      error: () => this.loadingMore.set(false),
    });
  }
}
