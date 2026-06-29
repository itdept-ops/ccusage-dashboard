import {
  ChangeDetectionStrategy, Component, computed, inject, signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { catchError, of } from 'rxjs';

import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { Api } from '../../core/api';
import { AgentInbox, AgentInboxItem, AgentInboxPeriod } from '../../core/models';
import { BetaEmptyState, BetaErrorState } from '../beta-ui';

/** Presentation metadata for one agent kind — its glyph + accent hue. Mirrors the /agents catalog so an
 *  inbox item visually matches the agent that produced it. Unknown kinds fall back to a neutral assistant. */
interface KindMeta {
  icon: string;
  /** A hue (deg) for the per-card accent tint, kept subtle on the --tech-* shell. */
  hue: number;
}

const KIND_META: Readonly<Record<string, KindMeta>> = {
  morningBriefing: { icon: 'wb_sunny', hue: 38 },
  streakRescue: { icon: 'local_fire_department', hue: 14 },
  budgetAlert: { icon: 'savings', hue: 150 },
  lowStaples: { icon: 'shopping_basket', hue: 265 },
  medicationDue: { icon: 'medication', hue: 200 },
  agent: { icon: 'smart_toy', hue: 220 },
};

/** Friendly header copy for each period bucket the backend emits. */
const PERIOD_META: Readonly<Record<AgentInboxPeriod, { label: string; icon: string }>> = {
  overnight: { label: 'Overnight', icon: 'bedtime' },
  today: { label: 'Today', icon: 'wb_twilight' },
  earlier: { label: 'Earlier', icon: 'history' },
};

const PERIOD_ORDER: readonly AgentInboxPeriod[] = ['overnight', 'today', 'earlier'];

/** One inbox item enriched with its presentation metadata. */
interface InboxRow extends AgentInboxItem {
  meta: KindMeta;
}

/** One period section: its header copy + the rows under it. */
interface InboxSection {
  period: AgentInboxPeriod;
  label: string;
  icon: string;
  rows: InboxRow[];
}

/**
 * Agent Inbox / "Overnight" (/inbox) — a dedicated, browsable view of "what your OS did for you". Where the
 * bell is a transient drip, this is the companion surface: the caller's OWN proactive-agent deliveries
 * grouped under period headers (Overnight / Today / Earlier), each a card with the agent's glyph + accent, a
 * one-line summary, a relative time, an un-handled marker, a deep-link to ACT on it, and a one-tap
 * mark-handled. A handle-all clears the lot.
 *
 * SCOPE + PRIVACY: every item is fetched from the self-scoped {@link Api.agentInbox} endpoint (owner-scoped
 * server-side; gated `agents.use`). "Handled" reuses the existing notification read flag — no migration.
 * An AgentNudge carries no actor, so only the friendly agent label is ever shown; no email appears here.
 * A holder with no agent deliveries gets a tasteful empty state pointing at /agents.
 */
@Component({
  selector: 'app-inbox',
  standalone: true,
  imports: [MatIconModule, MatProgressSpinnerModule, BetaEmptyState, BetaErrorState],
  templateUrl: './inbox.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './inbox.scss',
})
export class Inbox {
  private api = inject(Api);
  private router = inject(Router);

  readonly loading = signal(true);
  readonly error = signal(false);
  readonly busyAll = signal(false);

  /** The raw items (flat, newest-first within each period); sections are derived. */
  private readonly items = signal<InboxRow[]>([]);
  readonly unhandled = signal(0);

  readonly sections = computed<InboxSection[]>(() => {
    const byPeriod = new Map<AgentInboxPeriod, InboxRow[]>();
    for (const r of this.items()) {
      const list = byPeriod.get(r.period) ?? [];
      list.push(r);
      byPeriod.set(r.period, list);
    }
    return PERIOD_ORDER
      .filter((p) => byPeriod.has(p))
      .map((p) => ({ period: p, label: PERIOD_META[p].label, icon: PERIOD_META[p].icon, rows: byPeriod.get(p)! }));
  });

  readonly isEmpty = computed(() => !this.loading() && !this.error() && this.items().length === 0);

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api
      .agentInbox()
      .pipe(
        catchError(() => {
          this.error.set(true);
          return of(null);
        }),
      )
      .subscribe((res) => {
        if (res) this.absorb(res);
        this.loading.set(false);
      });
  }

  /** Flatten the grouped payload into enriched rows (period order is re-derived by {@link sections}). */
  private absorb(res: AgentInbox): void {
    const rows: InboxRow[] = [];
    for (const g of res.groups) {
      for (const it of g.items) rows.push({ ...it, meta: KIND_META[it.agentKind] ?? KIND_META['agent'] });
    }
    this.items.set(rows);
    this.unhandled.set(res.unhandledCount);
  }

  /** Follow an item's deep-link, marking it handled on the way (it's been acted on). */
  act(row: InboxRow): void {
    if (row.deepLink) {
      this.markHandled(row);
      void this.router.navigateByUrl(row.deepLink);
    }
  }

  /** Mark one item handled (optimistic; reuses the server read flag). */
  markHandled(row: InboxRow): void {
    if (row.handled) return;
    this.patch(row.id, (r) => ({ ...r, handled: true }));
    this.unhandled.update((n) => Math.max(0, n - 1));
    this.api
      .handleAgentInbox([row.id])
      .pipe(catchError(() => of(null)))
      .subscribe((res) => {
        if (res) this.unhandled.set(res.unhandledCount);
      });
  }

  /** Mark every un-triaged item handled. */
  handleAll(): void {
    if (this.busyAll() || this.unhandled() === 0) return;
    this.busyAll.set(true);
    this.api
      .handleAllAgentInbox()
      .pipe(catchError(() => of(null)))
      .subscribe((res) => {
        if (res) {
          this.items.update((rows) => rows.map((r) => ({ ...r, handled: true })));
          this.unhandled.set(0);
        }
        this.busyAll.set(false);
      });
  }

  private patch(id: number, fn: (r: InboxRow) => InboxRow): void {
    this.items.update((rows) => rows.map((r) => (r.id === id ? fn(r) : r)));
  }

  /** A compact relative time ("just now", "3h ago", "2d ago", or a date) from a UTC ISO string. */
  relTime(iso: string): string {
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return '';
    const secs = Math.max(0, Math.round((Date.now() - then) / 1000));
    if (secs < 60) return 'just now';
    const mins = Math.round(secs / 60);
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.round(mins / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 7) return `${days}d ago`;
    return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  trackSection = (_: number, s: InboxSection) => s.period;
  trackRow = (_: number, r: InboxRow) => r.id;
}
