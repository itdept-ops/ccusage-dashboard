import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed,
  inject, signal,
} from '@angular/core';
import { catchError, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { Api } from '../../core/api';
import { PublicBuiltWithDto } from '../../core/models';

/** One rendered metric in the badge strip: a (count-animated) value + a static suffix + a label. */
interface BadgeStat {
  /** The target numeric value the counter eases up to. */
  value: number;
  /** A short suffix appended after the count (e.g. "M", "k", "$" handled via prefix). */
  suffix: string;
  /** Render the value as a compact dollar figure ("$1.2k") rather than a plain integer. */
  money?: boolean;
  /** Render the value as a compact token figure ("12.4M") rather than a plain integer. */
  compact?: boolean;
  label: string;
}

/**
 * <app-built-with-badge> — a PUBLIC, presentational "Built with Usage IQ" proof strip for the Aurora landing.
 * It augments the curated stat band with LIVE, owner-scoped aggregate figures pulled anonymously from
 * {@link Api.builtWith} (GET /api/public/built-with) — total tokens, total spend, reporting agents, coding
 * sessions, and active days.
 *
 * PRIVACY: the endpoint returns AGGREGATE NUMBERS ONLY (no email/name/project/model) and the figures are
 * identical for every caller (the response is server-cached), so this is safe to render anonymously on the
 * marketing page. Marketing-only — no app gate, no mobile twin. The counters ease up once on first paint
 * (skipped under prefers-reduced-motion); a fetch failure simply hides the strip (the curated band stays).
 */
@Component({
  selector: 'app-built-with-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './built-with-badge.scss',
  template: `
    @if (data(); as d) {
      <aside class="bw" aria-label="Live Usage IQ figures">
        <p class="bw__eyebrow">
          <span class="bw__spark" aria-hidden="true"></span>
          Built with Usage IQ · live, from the owner's own account
        </p>
        <div class="bw__grid">
          @for (s of stats(); track s.label; let i = $index) {
            <div class="bw__stat">
              <span class="bw__value">{{ render(i) }}</span>
              <span class="bw__label">{{ s.label }}</span>
            </div>
          }
        </div>
        <p class="bw__foot">Aggregate figures only · {{ d.asOf }} · no personal data ever leaves the box</p>
      </aside>
    }
  `,
})
export class BuiltWithBadge {
  private api = inject(Api);
  private host = inject<ElementRef<HTMLElement>>(ElementRef);
  private destroyRef = inject(DestroyRef);

  /** The fetched figures; null until loaded (and stays null on a fetch failure, hiding the strip). */
  readonly data = signal<PublicBuiltWithDto | null>(null);

  /** Per-metric eased progress 0..1, mirrors {@link stats} by index (1 = settled). */
  private readonly progress = signal<number[]>([0, 0, 0, 0, 0]);

  /** The metric definitions, derived from the loaded payload. */
  readonly stats = computed<BadgeStat[]>(() => {
    const d = this.data();
    if (!d) return [];
    return [
      { value: d.totalTokens, suffix: '', compact: true, label: 'Tokens tracked' },
      { value: d.totalCostUsd, suffix: '', money: true, label: 'Spend priced' },
      { value: d.agentCount, suffix: '', label: 'Reporting agents' },
      { value: d.sessionCount, suffix: '', label: 'Coding sessions' },
      { value: d.activeDays, suffix: '', label: 'Active days' },
    ];
  });

  constructor() {
    this.api
      .builtWith()
      .pipe(
        catchError(() => of<PublicBuiltWithDto | null>(null)),
        takeUntilDestroyed(),
      )
      .subscribe((d) => {
        if (d) this.data.set(d);
      });

    afterNextRender(() => this.armCounters());
  }

  /** Render one metric's current (animated) display string. */
  render(i: number): string {
    const s = this.stats()[i];
    if (!s) return '';
    const p = this.progress()[i] ?? 1;
    const cur = s.value * p;
    if (s.money) return BuiltWithBadge.money(cur);
    if (s.compact) return BuiltWithBadge.compact(cur);
    return Math.round(cur).toLocaleString();
  }

  /** Compact integer formatting ("12.4M", "8.1k", "742") for big token counts. */
  private static compact(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 10_000) return `${(n / 1_000).toFixed(0)}k`;
    if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`;
    return Math.round(n).toLocaleString();
  }

  /** Compact dollar formatting ("$1.2k", "$842", "$12.40"). */
  private static money(n: number): string {
    if (n >= 1_000) return `$${(n / 1_000).toFixed(1)}k`;
    if (n >= 100) return `$${Math.round(n).toLocaleString()}`;
    return `$${n.toFixed(2)}`;
  }

  /** Ease every counter up once the strip scrolls into view (snaps to final under reduced-motion). */
  private armCounters(): void {
    const settle = () => this.progress.set(this.stats().map(() => 1));
    const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce || !('IntersectionObserver' in window)) {
      settle();
      return;
    }
    const el = this.host.nativeElement;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          io.disconnect();
          clearTimeout(failsafe);
          this.runCounters();
        }
      },
      { threshold: 0.35 },
    );
    io.observe(el);
    // If the observer never fires (throttled tab), snap to final values.
    const failsafe = setTimeout(() => {
      io.disconnect();
      settle();
    }, 3000);
    this.destroyRef.onDestroy(() => {
      io.disconnect();
      clearTimeout(failsafe);
    });
  }

  private runCounters(): void {
    const start = performance.now();
    const dur = 1400;
    const n = this.stats().length || 5;
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / dur);
      const e = 1 - Math.pow(1 - t, 3); // easeOutCubic
      this.progress.set(Array.from({ length: n }, () => e));
      if (t < 1) requestAnimationFrame(tick);
      else this.progress.set(Array.from({ length: n }, () => 1));
    };
    requestAnimationFrame(tick);
  }
}
