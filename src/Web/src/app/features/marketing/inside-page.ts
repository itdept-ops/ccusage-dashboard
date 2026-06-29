import {
  AfterViewInit,
  Component,
  DestroyRef,
  ElementRef,
  NgZone,
  OnDestroy,
  ChangeDetectionStrategy,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { Api } from '../../core/api';
import { PublicBuiltWithDto } from '../../core/models';
import { MarketingNav } from './marketing-nav';
import { MarketingFooter } from './marketing-footer';

/** One live readout in the system-status panel: a (count-animated) value + a label + a unit. */
interface Metric {
  /** Stable key for @for tracking. */
  key: string;
  /** The target numeric value the counter eases up to. */
  value: number;
  /** A short caption under the value. */
  label: string;
  /** Render the value as a compact dollar figure ("$1.2k"). */
  money?: boolean;
  /** Render the value as a compact figure ("12.4M", "8.1k"). */
  compact?: boolean;
}

/** A layer in the architecture stack — name + role + icon. */
interface StackLayer {
  icon: string;
  name: string;
  role: string;
  /** which aurora hue tints the layer chip */
  hue: 'a' | 'b' | 'c';
}

/** An agentic-layer capability — the trigger and what the OS does with it. */
interface AgenticItem {
  icon: string;
  title: string;
  text: string;
}

/**
 * Public "Inside the OS" page — a PII-safe look under the hood of The Agentic Life OS.
 *
 * Three substance blocks, all anonymous + aggregate + static:
 *   1. LIVE SYSTEM METRICS pulled anonymously from {@link Api.builtWith} (GET /api/public/built-with) —
 *      aggregate, owner-scoped, cached NUMBERS ONLY (tokens, spend, agents, sessions, active days, as-of).
 *      Presented as a "system status" readout; the counters ease up once on first paint.
 *   2. ARCHITECTURE + STACK — a static, accurate map of the real stack and the agentic layer.
 *   3. THE BUILDER — a short "built solo with agentic AI" note (founder + veteran + engineering focus only).
 *
 * PRIVACY: the only network call is the anonymous, server-cached built-with badge (aggregate numbers, no PII);
 * everything else is static copy. The page renders fully for a logged-OUT visitor and carries no app gate.
 *
 * Bare layout (own chrome): marketing nav + footer + the shared Aurora canvas. The IntersectionObserver
 * scroll-reveal is armed only behind `.js-reveal`, so a no-JS / reduced-motion render shows everything.
 */
@Component({
  selector: 'app-inside-page',
  imports: [RouterLink, MatIconModule, MarketingNav, MarketingFooter],
  templateUrl: './inside-page.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./marketing-page.scss', './inside-page.scss'],
})
export class InsidePage implements AfterViewInit, OnDestroy {
  private api = inject(Api);
  private host = inject<ElementRef<HTMLElement>>(ElementRef);
  private zone = inject(NgZone);
  private destroyRef = inject(DestroyRef);

  /** The fetched aggregate figures; null until loaded (and stays null on a fetch failure). */
  readonly data = signal<PublicBuiltWithDto | null>(null);

  /** Whole-panel eased progress 0..1 (1 = settled); the readouts count up together. */
  private readonly progress = signal(0);

  /** The "as of" caption for the readout — falls back to the canonical all-time label. */
  readonly asOf = computed(() => this.data()?.asOf ?? 'all time');

  /** The live metric definitions, derived from the loaded payload (empty until it lands). */
  readonly metrics = computed<Metric[]>(() => {
    const d = this.data();
    if (!d) return [];
    return [
      { key: 'tokens', value: d.totalTokens, compact: true, label: 'Tokens metered' },
      { key: 'cost', value: d.totalCostUsd, money: true, label: 'Spend priced' },
      { key: 'agents', value: d.agentCount, label: 'Reporting agents' },
      { key: 'sessions', value: d.sessionCount, label: 'Coding sessions' },
      { key: 'days', value: d.activeDays, label: 'Active days' },
    ];
  });

  /** Static architecture facts — the layers of the real stack, top (surface) to bottom (deploy). */
  readonly stack: StackLayer[] = [
    {
      icon: 'web',
      name: 'Angular 22 SPA',
      role: 'Standalone-component, signals-driven front end — one responsive surface plus a desktop / mobile platform split.',
      hue: 'a',
    },
    {
      icon: 'dns',
      name: '.NET 9 minimal API',
      role: 'The kernel: REST ingest, query, and auth, with a SignalR hub for real-time chat, notifications, and force-logout.',
      hue: 'b',
    },
    {
      icon: 'database',
      name: 'PostgreSQL + EF Core 9',
      role: 'The single source of truth — token rows, users, chat, the tracker — with migrations applied automatically on startup.',
      hue: 'c',
    },
    {
      icon: 'auto_awesome',
      name: 'Gemini, floored',
      role: 'Server-side AI behind a deterministic baseline: a calculation runs first, the model only refines. Off by default, permission-gated.',
      hue: 'a',
    },
    {
      icon: 'install_mobile',
      name: 'Deep PWA',
      role: 'Installable, offline-aware, native-feeling on the phone — the same data on a grand desktop surface and a native mobile shell.',
      hue: 'b',
    },
    {
      icon: 'rocket_launch',
      name: 'AWS, push-to-main',
      role: 'One small instance behind nginx + auto-HTTPS. A push to main builds images to ECR and rolls them out keylessly via GitHub OIDC + SSM.',
      hue: 'c',
    },
  ];

  /** Static facts — the agentic layer: what turns the app into an OS that acts. */
  readonly agentic: AgenticItem[] = [
    {
      icon: 'schedule',
      title: 'Scheduled agents',
      text: 'Background agents run on a cadence — a daily coach reads your day, a weekly review reads your week — and surface what matters without being asked.',
    },
    {
      icon: 'bolt',
      title: 'Ask that acts',
      text: 'Type or speak an intent — “jogged two miles”, “what can I eat with 500 calories left?” — and the OS drafts the structured action. It prefills; you confirm.',
    },
    {
      icon: 'insights',
      title: 'Insights that watch',
      text: 'The OS reads across domains — spend, macros, calendar, goals — and turns the patterns into plain-language insights, not another chart.',
    },
  ];

  /** Static facts — the trust spine, mirrored from the marketing technology page (no PII). */
  readonly principles: { icon: string; title: string; text: string }[] = [
    {
      icon: 'shield_person',
      title: 'Server-enforced capabilities',
      text: 'Granular permissions are re-checked on the server every request — never hidden in the UI. Google-pinned identity, full audit log, real-time force-logout.',
    },
    {
      icon: 'lock',
      title: 'No personal data ever leaves the box',
      text: 'The live numbers below are aggregate counts for one owner account. No email, name, project, or per-user anything — the response is identical for every visitor.',
    },
    {
      icon: 'dns',
      title: 'Self-hosted, no telemetry',
      text: 'Docker-composed to run anywhere, deployed keylessly to AWS. Provider keys live in git-ignored config or AWS SSM. No seat pricing. Nothing phones home.',
    },
  ];

  constructor() {
    this.api
      .builtWith()
      .pipe(
        catchError(() => of<PublicBuiltWithDto | null>(null)),
        takeUntilDestroyed(),
      )
      .subscribe((d) => {
        if (d) {
          this.data.set(d);
          this.armReadout();
        }
      });
  }

  /** Render one metric's current (animated) display string. */
  render(m: Metric): string {
    const cur = m.value * this.progress();
    if (m.money) return InsidePage.money(cur);
    if (m.compact) return InsidePage.compact(cur);
    return Math.round(cur).toLocaleString();
  }

  /** Compact integer formatting ("12.4M", "8.1k", "742") for big counts. */
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

  /** Ease the readout up once (snaps to final under reduced-motion / no rAF). */
  private armReadout(): void {
    const reduce =
      typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce || typeof requestAnimationFrame === 'undefined') {
      this.progress.set(1);
      return;
    }
    this.zone.runOutsideAngular(() => {
      const start = performance.now();
      const dur = 1400;
      const tick = (now: number) => {
        const t = Math.min(1, (now - start) / dur);
        const e = 1 - Math.pow(1 - t, 3); // easeOutCubic
        this.zone.run(() => this.progress.set(e));
        if (t < 1) requestAnimationFrame(tick);
        else this.zone.run(() => this.progress.set(1));
      };
      requestAnimationFrame(tick);
    });
  }

  // ── Scroll-reveal (mirrors technology-page) ────────────────────────────────
  private observer?: IntersectionObserver;
  private revealFailsafe?: ReturnType<typeof setTimeout>;

  ngAfterViewInit(): void {
    const reduce =
      typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
    const els = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>('[data-reveal]'));

    if (reduce || typeof IntersectionObserver === 'undefined') {
      els.forEach((el) => el.classList.add('is-in'));
      return;
    }

    this.host.nativeElement.classList.add('js-reveal');

    this.zone.runOutsideAngular(() => {
      this.observer = new IntersectionObserver(
        (entries) => {
          for (const e of entries) {
            if (e.isIntersecting) {
              e.target.classList.add('is-in');
              this.observer?.unobserve(e.target);
            }
          }
        },
        { threshold: 0.16, rootMargin: '0px 0px -8% 0px' },
      );
      els.forEach((el) => this.observer!.observe(el));
      this.revealFailsafe = setTimeout(() => els.forEach((el) => el.classList.add('is-in')), 2500);
    });
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
    if (this.revealFailsafe !== undefined) clearTimeout(this.revealFailsafe);
  }
}
