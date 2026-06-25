import {
  ChangeDetectionStrategy, Component, ElementRef, NgZone, computed, inject,
  input, output, signal,
} from '@angular/core';

/**
 * BETA-KIT PullToRefresh — a wrapper component that adds native-feel pull-to-refresh to its
 * projected scroll content, generalized from the Atrium `atrPullRefresh` directive. When the
 * user drags DOWN from the very top (scrollTop === 0) past a threshold and releases, it emits
 * `refresh`; while pulling it shows a live accent SvgRing-style spinner whose arc tracks the
 * pull distance, then spins while `busy` is true. Touch-driven, listeners attached outside
 * Angular so touchmove doesn't thrash change detection, and it never preventDefaults normal
 * scrolling, so a mis-tuned gesture can't break the page.
 *
 * The wrapper IS the scroll container (it owns overflow-y:auto); put your scrollable content
 * inside it. Inherits --accent-a, --ease-out, safe-area from the beta-kit host. Honors
 * reduced-motion (the spinner stops animating; the gesture still fires). Dependency-free.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-pull-refresh
 *   inputs:    busy (boolean, default false — show the spinning indicator while a refresh is in flight),
 *              threshold (number px, default 70 — pull distance that commits), disabled (boolean, default false)
 *   outputs:   refresh (void) — fired once when a past-threshold pull is released
 *   content:   projected scroll content via <ng-content> (this wrapper is the scroller)
 *
 * Usage: `<app-bs-pull-refresh [busy]="loading()" (refresh)="reload()"> …scrolling content… </app-bs-pull-refresh>`
 */
@Component({
  selector: 'app-bs-pull-refresh',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="ptr-indicator" aria-hidden="true"
         [class.spinning]="busy()"
         [style.height.px]="indicatorH()"
         [style.opacity]="indicatorOpacity()">
      <svg viewBox="0 0 36 36" class="ptr-ring" [style.transform]="'rotate(' + spin() + 'deg)'">
        <defs>
          <linearGradient id="ptr-grad" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stop-color="var(--accent-a)" />
            <stop offset="1" stop-color="var(--accent-b)" />
          </linearGradient>
        </defs>
        <circle class="ptr-track" cx="18" cy="18" r="15" fill="none" stroke-width="3" />
        <circle class="ptr-arc" cx="18" cy="18" r="15" fill="none" stroke-width="3"
                stroke-linecap="round" stroke="url(#ptr-grad)"
                transform="rotate(-90 18 18)"
                [attr.stroke-dasharray]="CIRC"
                [attr.stroke-dashoffset]="arcOffset()" />
      </svg>
    </div>
    <div #scroller class="ptr-scroll"><ng-content></ng-content></div>
  `,
  styles: [`
    :host { display: block; position: relative; height: 100%; overflow: hidden; }
    .ptr-indicator {
      position: absolute; top: 0; left: 0; right: 0; z-index: 2;
      display: flex; align-items: center; justify-content: center;
      overflow: hidden; pointer-events: none;
      padding-top: var(--safe-top, 0px);
    }
    .ptr-ring { width: 28px; height: 28px; display: block; }
    .ptr-track { stroke: var(--hairline); }
    .ptr-indicator.spinning .ptr-ring { animation: ptr-spin .8s linear infinite; }
    .ptr-indicator.spinning .ptr-arc { stroke-dashoffset: 70; }
    @keyframes ptr-spin { to { transform: rotate(360deg); } }
    .ptr-scroll {
      height: 100%; overflow-y: auto; overflow-x: hidden;
      overscroll-behavior-y: contain; -webkit-overflow-scrolling: touch;
    }
    @media (prefers-reduced-motion: reduce) {
      .ptr-indicator.spinning .ptr-ring { animation: none; }
    }
  `],
})
export class BetaPullRefresh {
  /** Show the spinning indicator while a refresh is in flight (the host flips this off when done). */
  readonly busy = input<boolean>(false);
  /** Pull distance in px that commits a refresh on release. */
  readonly threshold = input<number>(70);
  /** When true the gesture is inert. */
  readonly disabled = input<boolean>(false);
  /** Fired once when a past-threshold pull is released. */
  readonly refresh = output<void>();

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly zone = inject(NgZone);

  protected readonly CIRC = 2 * Math.PI * 15;

  /** Live pull distance (px) while dragging from the top. */
  private readonly pull = signal(0);
  /** Continuous rotation used for the spin (also nudged by pull for a wind-up feel). */
  protected readonly spin = computed(() => this.pull() * 2);

  private scrollerEl: HTMLElement | null = null;
  private startY = 0;
  private pulling = false;

  /** Indicator reveal height — grows with the pull, capped just past threshold. */
  protected readonly indicatorH = computed(() => {
    if (this.busy()) return 56;
    return Math.min(this.pull() * 0.6, this.threshold());
  });
  protected readonly indicatorOpacity = computed(() => {
    if (this.busy()) return 1;
    return Math.min(1, this.pull() / this.threshold());
  });
  /** Arc fills 0→full as the pull approaches threshold. */
  protected readonly arcOffset = computed(() => {
    const frac = Math.min(1, this.pull() / this.threshold());
    return this.CIRC * (1 - frac);
  });

  ngAfterViewInit(): void {
    // Bind to the inner scroller outside Angular so touchmove doesn't trigger change detection.
    this.zone.runOutsideAngular(() => {
      const el = this.host.nativeElement.querySelector('.ptr-scroll') as HTMLElement | null;
      this.scrollerEl = el;
      if (!el) return;
      el.addEventListener('touchstart', this.onStart, { passive: true });
      el.addEventListener('touchmove', this.onMove, { passive: true });
      el.addEventListener('touchend', this.onEnd, { passive: true });
    });
  }

  ngOnDestroy(): void {
    const el = this.scrollerEl;
    if (!el) return;
    el.removeEventListener('touchstart', this.onStart);
    el.removeEventListener('touchmove', this.onMove);
    el.removeEventListener('touchend', this.onEnd);
  }

  private onStart = (e: TouchEvent): void => {
    if (this.disabled() || this.busy()) { this.pulling = false; return; }
    if ((this.scrollerEl?.scrollTop ?? 1) <= 0 && e.touches.length === 1) {
      this.startY = e.touches[0].clientY;
      this.pulling = true;
    } else {
      this.pulling = false;
    }
  };

  private onMove = (e: TouchEvent): void => {
    if (!this.pulling) return;
    if ((this.scrollerEl?.scrollTop ?? 1) > 0) { this.pulling = false; this.pull.set(0); return; }
    const dy = (e.touches[0]?.clientY ?? this.startY) - this.startY;
    // Reflect the live pull (positive only) so the indicator can grow. A signal write here is
    // cheap; CD coalesces. We deliberately do NOT preventDefault so native scroll stays intact.
    this.pull.set(Math.max(0, dy));
  };

  private onEnd = (e: TouchEvent): void => {
    if (!this.pulling) return;
    const dy = (e.changedTouches[0]?.clientY ?? this.startY) - this.startY;
    this.pulling = false;
    const commit = dy >= this.threshold();
    this.zone.run(() => {
      this.pull.set(0);
      if (commit) this.refresh.emit();
    });
  };
}
