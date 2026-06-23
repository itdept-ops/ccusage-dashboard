import { Directive, ElementRef, NgZone, OnDestroy, OnInit, inject, output } from '@angular/core';

/**
 * A lightweight pull-to-refresh for the Atrium scroll column. When the user drags DOWN from the very top
 * (scrollTop === 0) past a threshold and releases, it emits {@link refresh}. Touch-only and passive where
 * possible; it never preventDefaults normal scrolling, so it can't break the page if the gesture math is
 * off. Beta-only, no live imports.
 */
@Directive({
  selector: '[atrPullRefresh]',
  standalone: true,
})
export class PullToRefreshDirective implements OnInit, OnDestroy {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly zone = inject(NgZone);

  readonly atrPullRefresh = output<void>();

  private startY = 0;
  private pulling = false;
  private readonly THRESHOLD = 70;

  ngOnInit(): void {
    // Outside Angular so passive listeners don't trigger change detection on every touchmove.
    this.zone.runOutsideAngular(() => {
      const el = this.host.nativeElement;
      el.addEventListener('touchstart', this.onStart, { passive: true });
      el.addEventListener('touchmove', this.onMove, { passive: true });
      el.addEventListener('touchend', this.onEnd, { passive: true });
    });
  }

  ngOnDestroy(): void {
    const el = this.host.nativeElement;
    el.removeEventListener('touchstart', this.onStart);
    el.removeEventListener('touchmove', this.onMove);
    el.removeEventListener('touchend', this.onEnd);
  }

  private onStart = (e: TouchEvent): void => {
    if (this.host.nativeElement.scrollTop <= 0 && e.touches.length === 1) {
      this.startY = e.touches[0].clientY;
      this.pulling = true;
    } else {
      this.pulling = false;
    }
  };

  private onMove = (e: TouchEvent): void => {
    if (!this.pulling) return;
    // Cancel if we've scrolled away from the top mid-gesture.
    if (this.host.nativeElement.scrollTop > 0) this.pulling = false;
  };

  private onEnd = (e: TouchEvent): void => {
    if (!this.pulling) return;
    const dy = (e.changedTouches[0]?.clientY ?? this.startY) - this.startY;
    this.pulling = false;
    if (dy >= this.THRESHOLD) {
      this.zone.run(() => this.atrPullRefresh.emit());
    }
  };
}
