import { Component, ElementRef, afterNextRender, inject, signal, viewChildren } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MarketingNav } from '../marketing/marketing-nav';
import { MarketingFooter } from '../marketing/marketing-footer';

interface Feature { icon: string; title: string; text: string; }
interface Stat { value: number; suffix: string; label: string; }
interface Source { name: string; tag: string; }
interface Step { n: string; title: string; text: string; }

@Component({
  selector: 'app-login',
  imports: [MatIconModule, RouterLink, MarketingNav, MarketingFooter],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class Login {
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Elements opted into scroll-reveal via the #reveal template ref. */
  private reveals = viewChildren<ElementRef<HTMLElement>>('reveal');

  /** Forwarded to /signin so a deep-linked guard redirect survives the marketing detour. */
  readonly returnUrl = signal<string | null>(null);

  readonly sources: Source[] = [
    { name: 'Claude Code', tag: 'Anthropic' },
    { name: 'OpenAI Codex', tag: 'Codex CLI' },
    { name: 'Self-hosted', tag: 'Your infra' },
    { name: 'PostgreSQL', tag: 'Your data' },
  ];

  /** Marquee of the stack Usage IQ speaks to — duplicated in the template for a seamless loop. */
  readonly marquee: string[] = [
    'Claude Code', 'OpenAI Codex', 'Anthropic API', 'PostgreSQL', 'Self-hosted',
    'Token tiers', 'Cache classes', 'Role-based access', 'Activity heatmap', 'Share links',
  ];

  /** Animated counter band — values count up when scrolled into view. */
  readonly stats: Stat[] = [
    { value: 2, suffix: '', label: 'Agents unified' },
    { value: 14, suffix: 'M+', label: 'Tokens tracked' },
    { value: 6, suffix: '', label: 'Token tiers priced' },
    { value: 100, suffix: '%', label: 'Self-hosted' },
  ];

  /** Live count for each stat, mirrors `stats` by index. */
  readonly counts = signal<number[]>(this.stats.map(() => 0));

  readonly features: Feature[] = [
    { icon: 'hub', title: 'Multi-source', text: 'Claude Code and OpenAI Codex usage, de-duplicated and unified into one view.' },
    { icon: 'insights', title: 'Cost & tokens', text: 'Break spend down by day, project, model, or session — with an editable pricing table.' },
    { icon: 'calendar_month', title: 'Activity calendar', text: 'A GitHub-style heatmap of every active hour, with session-level drill-down.' },
    { icon: 'shield_person', title: 'Role-based access', text: 'Google sign-in with per-user permissions, re-checked on every request.' },
    { icon: 'ios_share', title: 'Shareable views', text: 'Public, time-limited links to a read-only dashboard — revoke them anytime.' },
    { icon: 'sync', title: 'Always fresh', text: 'A background reporter posts new usage on a timer; the bar shows when it last ran.' },
  ];

  readonly steps: Step[] = [
    { n: '01', title: 'Run the reporter', text: 'A tiny agent on your machine reads Claude Code & Codex logs and posts new usage to your server.' },
    { n: '02', title: 'It lands in Postgres', text: 'Records are de-duplicated, priced from your editable rate table, and bucketed by your timezone.' },
    { n: '03', title: 'You see everything', text: 'Filter by date, project, model or session. Cost, tokens, cache tiers — all on one screen.' },
  ];

  readonly terminal: string[] = [
    '$ usage-iq reporter --watch',
    '  scanning ~/.claude/projects … 2,264 files',
    '  + 318 new records  (412 deduped)',
    '  posting → https://usageiq.online/api/ingest',
    '  ✓ synced 14.15M tokens · $182.4',
    '  next run in 30:00 …',
  ];

  constructor() {
    const ru = this.route.snapshot.queryParamMap.get('returnUrl');
    if (ru) this.returnUrl.set(ru);

    afterNextRender(() => {
      this.observeReveals();
      this.armCounters();
    });
  }

  /** [queryParams] binding — only forwards returnUrl when one is present. */
  signinParams(): Record<string, string> {
    const ru = this.returnUrl();
    return ru ? { returnUrl: ru } : {};
  }

  /** Progressive scroll-reveal: add `.in` as each opted-in element enters the viewport.
   *  Content is visible by default; we only arm the hidden→rise state (via the
   *  `js-reveal` host class) when IO is supported and motion is allowed. A safety
   *  timer reveals everything if the observer hasn't fired, so nothing can get
   *  stuck invisible on a backgrounded/throttled tab. */
  private observeReveals(): void {
    const els = this.reveals().map(r => r.nativeElement);
    if (!els.length) return;

    const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce || !('IntersectionObserver' in window)) return; // stay visible, no arming

    const hostEl = this.host.nativeElement;
    hostEl.classList.add('js-reveal');

    const revealAll = () => els.forEach(el => el.classList.add('in'));

    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting) {
          e.target.classList.add('in');
          io.unobserve(e.target);
        }
      }
    }, { threshold: 0.16, rootMargin: '0px 0px -8% 0px' });
    els.forEach(el => io.observe(el));

    // Fallback: if the observer never delivers (throttled tab), reveal everything.
    const failsafe = setTimeout(revealAll, 2500);
    // Reveal-on-first-paint guard: also clear the failsafe once anything reveals.
    const armed = els[0];
    const mo = new MutationObserver(() => {
      if (armed.classList.contains('in')) { clearTimeout(failsafe); mo.disconnect(); }
    });
    mo.observe(armed, { attributes: true, attributeFilter: ['class'] });
  }

  private settleCounts(): void {
    this.counts.set(this.stats.map(s => s.value));
  }

  /** Count the stat band up once it scrolls into view (skipped under reduced-motion). */
  private armCounters(): void {
    const band = this.host.nativeElement.querySelector<HTMLElement>('.stats');
    if (!band) return;
    const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduce || !('IntersectionObserver' in window)) {
      this.settleCounts();
      return;
    }
    const io = new IntersectionObserver((entries) => {
      if (entries.some(e => e.isIntersecting)) {
        io.disconnect();
        clearTimeout(failsafe);
        this.runCounters();
      }
    }, { threshold: 0.4 });
    io.observe(band);
    // If the observer never fires (throttled tab), snap to final values.
    const failsafe = setTimeout(() => { io.disconnect(); this.settleCounts(); }, 3000);
  }

  private runCounters(): void {
    const start = performance.now();
    const dur = 1400;
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / dur);
      // easeOutCubic for a punchy settle
      const e = 1 - Math.pow(1 - t, 3);
      this.counts.set(this.stats.map(s => Math.round(s.value * e)));
      if (t < 1) requestAnimationFrame(tick);
      else this.counts.set(this.stats.map(s => s.value));
    };
    requestAnimationFrame(tick);
  }

  scrollTop(ev: Event): void {
    ev.preventDefault();
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }
}
