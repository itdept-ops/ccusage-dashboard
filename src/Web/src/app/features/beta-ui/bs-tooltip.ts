import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, input, signal,
} from '@angular/core';

let tooltipSeq = 0;

/** Where the bubble prefers to sit relative to the trigger; it auto-flips if it would clip the viewport top. */
export type TooltipPlacement = 'top' | 'bottom';

/**
 * BETA-KIT Tooltip — a TAP-triggered floating info bubble anchored to a small "?"/info trigger. This
 * is the touch idiom (no hover): tapping the trigger toggles a glass popover carrying explanatory
 * text; tapping OUTSIDE, pressing Escape, or blurring the trigger dismisses it. The bubble prefers
 * `top` and auto-flips to `bottom` when it would clip the viewport. A new beta-kit primitive (no
 * flagship equivalent). The trigger is a real >=44px button (aria-expanded + aria-describedby wired
 * to the bubble's role=tooltip); the bubble is a plain container so callers project rich content
 * (text OR HTML) via <ng-content>, falling back to the `text` input when nothing is projected.
 * Honors reduced-motion via the page-host killswitch. Dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-tooltip
 *   inputs:    text (string, default '' — fallback body when no content is projected),
 *              label (string, default 'More info' — aria-label for the trigger button),
 *              glyph (string, default '?' — the character shown in the trigger),
 *              placement (TooltipPlacement 'top'|'bottom', default 'top'),
 *              disabled (boolean, default false)
 *   content:   optional projected bubble body via <ng-content> (overrides `text`)
 *
 * Usage: `<app-bs-tooltip text="Net carbs = total − fiber." label="What are net carbs?" />`
 *        `<app-bs-tooltip glyph="i"><b>Tip:</b> hold + to add faster.</app-bs-tooltip>`
 */
@Component({
  selector: 'app-bs-tooltip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'bs-tip',
    '(document:pointerdown)': 'onDocPointer($event)',
    '(document:keydown.escape)': 'close()',
  },
  template: `
    <button #trigger type="button" class="bs-tip-trigger"
            [attr.aria-label]="label()"
            [attr.aria-expanded]="open()"
            [attr.aria-describedby]="open() ? id : null"
            [disabled]="disabled()"
            (click)="toggle()"
            (blur)="onBlur($event)">
      <span aria-hidden="true">{{ glyph() }}</span>
    </button>

    @if (open()) {
      <div class="bs-tip-bubble" [class.pos-bottom]="resolved() === 'bottom'"
           role="tooltip" [id]="id">
        <ng-content>{{ text() }}</ng-content>
        <span class="bs-tip-arrow" aria-hidden="true"></span>
      </div>
    }
  `,
  styles: [`
    :host(.bs-tip) { position: relative; display: inline-flex; vertical-align: middle; }
    .bs-tip-trigger {
      width: 44px; height: 44px; min-width: 44px; min-height: 44px;
      display: inline-flex; align-items: center; justify-content: center;
      border: none; background: transparent; color: var(--ink-dim);
      cursor: pointer; -webkit-tap-highlight-color: transparent; touch-action: manipulation;
      border-radius: var(--r-pill);
    }
    /* the visible dot is a fixed 22px glass circle centered in the 44px hit target */
    .bs-tip-trigger > span {
      width: 22px; height: 22px; display: inline-flex; align-items: center; justify-content: center;
      border-radius: var(--r-pill); border: 1px solid var(--hairline);
      background: var(--glass); font-family: var(--font-ui); font-size: 13px; font-weight: 800;
      line-height: 1; transition: color 160ms var(--ease-out), border-color 160ms var(--ease-out);
    }
    .bs-tip-trigger[aria-expanded='true'] > span { color: var(--ink); border-color: var(--accent-a); }
    .bs-tip-trigger:disabled { opacity: .4; pointer-events: none; }
    .bs-tip-trigger:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }

    .bs-tip-bubble {
      position: absolute; left: 50%; bottom: calc(100% + 8px); transform: translateX(-50%);
      z-index: 40; width: max-content; max-width: min(260px, 78vw);
      padding: 10px 12px; border-radius: var(--r-tile);
      background: var(--glass); border: 1px solid var(--glass-edge);
      backdrop-filter: blur(var(--blur-glass)) saturate(1.4);
      -webkit-backdrop-filter: blur(var(--blur-glass)) saturate(1.4);
      box-shadow: var(--lift-3); color: var(--ink);
      font-family: var(--font-ui); font-size: 13px; font-weight: 600; line-height: 1.35;
      text-align: left; white-space: normal;
      animation: bs-tip-in 160ms var(--ease-out) both;
    }
    .bs-tip-bubble.pos-bottom { bottom: auto; top: calc(100% + 8px); }
    .bs-tip-arrow {
      position: absolute; left: 50%; top: 100%; transform: translate(-50%, -50%) rotate(45deg);
      width: 10px; height: 10px; background: var(--glass);
      border-right: 1px solid var(--glass-edge); border-bottom: 1px solid var(--glass-edge);
    }
    .bs-tip-bubble.pos-bottom .bs-tip-arrow {
      top: 0; border: none; border-left: 1px solid var(--glass-edge); border-top: 1px solid var(--glass-edge);
    }
    @keyframes bs-tip-in { from { opacity: 0; transform: translateX(-50%) translateY(4px); } }
    @media (prefers-reduced-motion: reduce) { .bs-tip-bubble { animation: none; } }
  `],
})
export class BetaTooltip {
  /** Fallback bubble body when nothing is projected via <ng-content>. */
  readonly text = input<string>('');
  /** aria-label for the trigger button. */
  readonly label = input<string>('More info');
  /** The character shown inside the trigger dot. */
  readonly glyph = input<string>('?');
  /** Preferred side; auto-flips to the other when it would clip the viewport top. */
  readonly placement = input<TooltipPlacement>('top');
  /** When true the trigger is inert. */
  readonly disabled = input<boolean>(false);

  private readonly hostEl = inject(ElementRef<HTMLElement>);
  private readonly destroyRef = inject(DestroyRef);

  /** Unique id linking the trigger's aria-describedby to the bubble's role=tooltip. */
  protected readonly id = `bs-tip-${++tooltipSeq}`;

  protected readonly open = signal(false);
  /** The side actually used this open (may differ from `placement` after the clip check). */
  private readonly placed = signal<TooltipPlacement | null>(null);
  protected readonly resolved = computed(() => this.placed() ?? this.placement());

  constructor() {
    this.destroyRef.onDestroy(() => this.open.set(false));
  }

  protected toggle(): void {
    if (this.disabled()) return;
    this.open() ? this.close() : this.show();
  }

  private show(): void {
    // Decide placement: prefer the requested side, but flip 'top' → 'bottom' if there isn't
    // room above the trigger for a typical bubble (measured against the live trigger rect).
    let side = this.placement();
    const rect = this.hostEl.nativeElement.getBoundingClientRect?.();
    if (side === 'top' && rect && rect.top < 96) side = 'bottom';
    this.placed.set(side);
    this.open.set(true);
  }

  protected close(): void {
    if (this.open()) this.open.set(false);
  }

  /** Dismiss when a pointerdown lands outside this component. */
  protected onDocPointer(e: Event): void {
    if (!this.open()) return;
    if (!this.hostEl.nativeElement.contains(e.target as Node)) this.close();
  }

  /** Dismiss when focus leaves the component entirely (e.g. Tab away). */
  protected onBlur(e: FocusEvent): void {
    const next = e.relatedTarget as Node | null;
    if (!next || !this.hostEl.nativeElement.contains(next)) this.close();
  }
}
