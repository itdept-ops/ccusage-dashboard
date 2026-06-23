import {
  ChangeDetectionStrategy, Component, computed, input, model, output, signal,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import {
  GroupBy, IngestionSource, MachineStat, ModelStat, ProjectDto, UsageFilter,
} from '../../../core/models';
// Beta -> beta cross-import: both surfaces are isolated; the bottom-sheet shell is shared verbatim.
import { BottomSheet } from '../../tracker-beta/ui/bottom-sheet';

/**
 * The full filter surface, exiled into a bottom sheet (the live page's 4x mat-select is unusable at
 * 390px). Edits a LOCAL draft of the filter so Cancel discards and Apply commits — the page only
 * re-fetches on Apply. Chip grids are 44px+ touch targets. A live result-count hint and a sticky
 * Apply in the thumb zone keep the commit obvious.
 *
 * Parity note: this only edits a {@link UsageFilter} (+ {@link GroupBy}); the page hands the exact
 * same filter to `Api.summary`, so the numbers match the live dashboard. No client aggregation here.
 */
@Component({
  selector: 'app-pulse-filter-sheet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatIconModule, BottomSheet],
  template: `
    <app-bottom-sheet [(open)]="open" detent="full" label="Filter usage" (closed)="onClosed()">
      <div class="fs">
        <header class="fs__head">
          <h2 class="fs__title">Filters</h2>
          <button type="button" class="fs__reset" (click)="reset()">Reset</button>
        </header>

        @if (projects().length) {
          <section class="fs__group">
            <h3 class="fs__label">Projects</h3>
            <div class="fs__chips">
              @for (p of projects(); track p.id) {
                <button type="button" class="chip" [class.chip--on]="hasProject(p.id)"
                        (click)="toggleProject(p.id)">{{ p.name }}</button>
              }
            </div>
          </section>
        }

        @if (models().length) {
          <section class="fs__group">
            <h3 class="fs__label">Models</h3>
            <div class="fs__chips">
              @for (m of models(); track m.model) {
                <button type="button" class="chip" [class.chip--on]="hasModel(m.model)"
                        (click)="toggleModel(m.model)">{{ m.model }}</button>
              }
            </div>
          </section>
        }

        @if (sources().length) {
          <section class="fs__group">
            <h3 class="fs__label">Sources</h3>
            <div class="fs__chips">
              @for (s of sources(); track s.name) {
                <button type="button" class="chip" [class.chip--on]="hasSource(s.name)"
                        (click)="toggleSource(s.name)">{{ s.name }}</button>
              }
            </div>
          </section>
        }

        @if (machines().length) {
          <section class="fs__group">
            <h3 class="fs__label">Machines</h3>
            <div class="fs__chips">
              @for (mc of machines(); track mc.name) {
                <button type="button" class="chip" [class.chip--on]="hasMachine(mc.name)"
                        (click)="toggleMachine(mc.name)">{{ mc.label }}</button>
              }
            </div>
          </section>
        }

        <section class="fs__group">
          <h3 class="fs__label">Group time by</h3>
          <div class="fs__seg" role="group" aria-label="Group time by">
            <button type="button" class="seg" [class.seg--on]="draftGroupBy() === 'day'"
                    (click)="draftGroupBy.set('day')">Day</button>
            <button type="button" class="seg" [class.seg--on]="draftGroupBy() === 'month'"
                    (click)="draftGroupBy.set('month')">Month</button>
          </div>
        </section>

        <section class="fs__group">
          <button type="button" class="fs__toggle" (click)="toggleSidechain()"
                  [attr.aria-pressed]="draft().includeSidechain">
            <span class="fs__toggle-text">
              <span class="fs__toggle-title">Include subagents</span>
              <span class="fs__toggle-sub">Side-chain (subagent) calls in the totals</span>
            </span>
            <span class="switch" [class.switch--on]="draft().includeSidechain" aria-hidden="true">
              <span class="switch__dot"></span>
            </span>
          </button>
        </section>
      </div>

      <div class="fs__apply">
        <button type="button" class="fs__cancel" (click)="cancel()">Cancel</button>
        <button type="button" class="fs__commit" (click)="apply()">
          Apply{{ activeCount() ? ' · ' + activeCount() : '' }}
        </button>
      </div>
    </app-bottom-sheet>
  `,
  styles: [`
    :host { display: contents; }

    .fs { display: flex; flex-direction: column; gap: 18px; padding: 4px 0 88px; }
    .fs__head { display: flex; align-items: center; justify-content: space-between; padding-top: 4px; }
    .fs__title { margin: 0; font-size: 20px; font-weight: 700; color: var(--pulse-ink); }
    .fs__reset {
      background: none; border: 0; color: var(--pulse-ink-dim); font: inherit; font-size: 14px;
      min-height: 44px; padding: 0 8px; cursor: pointer; border-radius: var(--r-pill);
    }
    .fs__reset:active { color: var(--pulse-ink); }

    .fs__group { display: flex; flex-direction: column; gap: 10px; }
    .fs__label {
      margin: 0; font-size: 12px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase;
      color: var(--pulse-ink-dim);
    }
    .fs__chips { display: flex; flex-wrap: wrap; gap: 8px; }

    .chip {
      min-height: 44px; padding: 0 16px; border-radius: var(--r-pill);
      background: var(--pulse-rise); color: var(--pulse-ink-dim);
      border: 1px solid var(--pulse-edge); font: inherit; font-size: 14px;
      cursor: pointer; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      transition: background 160ms var(--ease-out), color 160ms var(--ease-out), border-color 160ms var(--ease-out);
    }
    .chip--on {
      background: color-mix(in srgb, var(--tok-a) 22%, var(--pulse-rise));
      color: var(--pulse-ink);
      border-color: color-mix(in srgb, var(--tok-a) 55%, var(--pulse-edge));
    }

    .fs__seg, .fs__seg { display: inline-flex; gap: 6px; background: var(--pulse-rise);
      border: 1px solid var(--pulse-edge); border-radius: var(--r-pill); padding: 4px; width: fit-content; }
    .seg {
      min-height: 44px; padding: 0 22px; border: 0; border-radius: var(--r-pill);
      background: none; color: var(--pulse-ink-dim); font: inherit; font-size: 14px; cursor: pointer;
      transition: background 160ms var(--ease-out), color 160ms var(--ease-out);
    }
    .seg--on { background: var(--tok-a); color: #07101f; font-weight: 600; }

    .fs__toggle {
      display: flex; align-items: center; justify-content: space-between; gap: 16px;
      width: 100%; min-height: 56px; padding: 10px 16px; border-radius: var(--r-card);
      background: var(--pulse-rise); border: 1px solid var(--pulse-edge); cursor: pointer; text-align: left;
    }
    .fs__toggle-text { display: flex; flex-direction: column; gap: 2px; }
    .fs__toggle-title { font-size: 15px; color: var(--pulse-ink); font-weight: 600; }
    .fs__toggle-sub { font-size: 12px; color: var(--pulse-ink-dim); }

    .switch {
      flex: 0 0 auto; width: 46px; height: 28px; border-radius: var(--r-pill);
      background: var(--pulse-edge); position: relative; transition: background 200ms var(--ease-out);
    }
    .switch--on { background: var(--cache-hit); }
    .switch__dot {
      position: absolute; top: 3px; left: 3px; width: 22px; height: 22px; border-radius: 50%;
      background: #fff; transition: transform 200ms var(--ease-out);
    }
    .switch--on .switch__dot { transform: translateX(18px); }

    /* Sticky thumb-zone commit bar, inside the scrolling sheet body. */
    .fs__apply {
      position: sticky; bottom: 0; display: flex; gap: 10px;
      padding: 12px 0 calc(12px + var(--safe-bottom));
      background: linear-gradient(to top, var(--glass) 72%, transparent);
      backdrop-filter: blur(var(--blur-glass));
      -webkit-backdrop-filter: blur(var(--blur-glass));
    }
    .fs__cancel, .fs__commit {
      flex: 1 1 auto; min-height: 52px; border-radius: var(--r-pill); font: inherit;
      font-size: 16px; font-weight: 600; cursor: pointer; border: 1px solid var(--pulse-edge);
    }
    .fs__cancel { flex: 0 0 38%; background: var(--pulse-rise); color: var(--pulse-ink-dim); }
    .fs__commit {
      background: linear-gradient(120deg, var(--tok-a), var(--tok-b)); color: #07101f; border: 0;
      box-shadow: 0 8px 22px -8px var(--tok-a);
    }
  `],
})
export class PulseFilterSheet {
  /** Two-way open state, owned by the page (it sets true when the filter button is tapped). */
  readonly open = model<boolean>(false);

  /** Option catalogs (read-only) supplied by the page. */
  readonly projects = input<ProjectDto[]>([]);
  readonly models = input<ModelStat[]>([]);
  readonly sources = input<IngestionSource[]>([]);
  readonly machines = input<MachineStat[]>([]);

  /** The committed filter + groupBy the page currently holds (used to seed the draft on open). */
  readonly filter = input.required<UsageFilter>();
  readonly groupBy = input.required<GroupBy>();

  /** Emitted on Apply with the committed draft (page re-fetches). */
  readonly applied = output<{ filter: UsageFilter; groupBy: GroupBy }>();

  /** Local editable draft — Cancel discards, Apply commits. */
  private readonly draftFilter = signal<UsageFilter>(this.blankFilter());
  readonly draftGroupBy = signal<GroupBy>('day');
  readonly draft = this.draftFilter.asReadonly();

  /** Called by the page right before opening, to copy the live filter into the draft. */
  seed(): void {
    this.draftFilter.set({ ...this.filter(), projectIds: [...this.filter().projectIds], models: [...this.filter().models], sources: [...this.filter().sources], machine: [...this.filter().machine] });
    this.draftGroupBy.set(this.groupBy());
  }

  private blankFilter(): UsageFilter {
    return { from: null, to: null, projectIds: [], models: [], sources: [], machine: [], includeSidechain: true };
  }

  readonly activeCount = computed(() => {
    const f = this.draftFilter();
    return f.projectIds.length + f.models.length + f.sources.length + f.machine.length + (f.includeSidechain ? 0 : 1);
  });

  hasProject(id: number): boolean { return this.draftFilter().projectIds.includes(id); }
  hasModel(m: string): boolean { return this.draftFilter().models.includes(m); }
  hasSource(s: string): boolean { return this.draftFilter().sources.includes(s); }
  hasMachine(mc: string): boolean { return this.draftFilter().machine.includes(mc); }

  toggleProject(id: number): void { this.draftFilter.update(f => ({ ...f, projectIds: toggle(f.projectIds, id) })); }
  toggleModel(m: string): void { this.draftFilter.update(f => ({ ...f, models: toggle(f.models, m) })); }
  toggleSource(s: string): void { this.draftFilter.update(f => ({ ...f, sources: toggle(f.sources, s) })); }
  toggleMachine(mc: string): void { this.draftFilter.update(f => ({ ...f, machine: toggle(f.machine, mc) })); }
  toggleSidechain(): void { this.draftFilter.update(f => ({ ...f, includeSidechain: !f.includeSidechain })); }

  reset(): void { this.draftFilter.set(this.blankFilter()); this.draftGroupBy.set('day'); }

  apply(): void {
    this.applied.emit({ filter: this.draftFilter(), groupBy: this.draftGroupBy() });
    this.open.set(false);
  }

  cancel(): void { this.open.set(false); }

  /** Sheet dismissed via grip/scrim/Esc — treat as cancel (no commit). */
  onClosed(): void { /* draft is reseeded on next open via seed(); nothing to persist */ }
}

/** Immutable toggle of a value in an array (add if absent, remove if present). */
function toggle<T>(arr: readonly T[], v: T): T[] {
  return arr.includes(v) ? arr.filter(x => x !== v) : [...arr, v];
}
