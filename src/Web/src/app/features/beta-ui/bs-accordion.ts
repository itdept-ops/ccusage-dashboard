import {
  ChangeDetectionStrategy, Component, ElementRef, contentChildren, effect,
  inject, input, model, output, signal, viewChild,
} from '@angular/core';

/**
 * BETA-KIT Accordion — a stack of expand/collapse rows that reveal projected content inline. A
 * new beta-kit primitive (no flagship equivalent). Each row is a header button (>=44px) with a
 * label + optional right-side hint and a chevron that rotates 90° when open; the body reveals via
 * a smooth grid-template-rows 0fr→1fr height transition (--ease-out) which is content-agnostic and
 * collapses to instant under the page reduced-motion killswitch.
 *
 * TWO PARTS, composed by the CONSUMER:
 *   • app-bs-accordion       — the container. When `single` is true it enforces one-open-at-a-time
 *                              (opening a row closes its siblings); otherwise rows toggle freely.
 *   • app-bs-accordion-item  — one row. Header text via `label` (+ optional `hint`); body via
 *                              <ng-content>. Two-way `open` so the host can control/observe a row.
 *
 * The container discovers its items via contentChildren and coordinates them; an item used
 * standalone (no container) still works as a self-contained toggle. Sediment surface
 * (--bg-rise + --hairline + --r-tile); reads the page cascade for all color/motion tokens.
 * Dependency-free + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   app-bs-accordion
 *     inputs:   single (boolean, default false — enforce one row open at a time)
 *   app-bs-accordion-item
 *     inputs:   label (string, required — the header text), hint (string, default '' — right-side muted text),
 *               open (model<boolean>, two-way — the row's expanded state), disabled (boolean, default false)
 *     outputs:  toggled (boolean — the new open state, on user toggle)
 *     content:  the row body, projected via <ng-content>
 *
 * Usage:
 *   <app-bs-accordion [single]="true">
 *     <app-bs-accordion-item label="Macros" hint="3 items"> …body… </app-bs-accordion-item>
 *     <app-bs-accordion-item label="Notes"> …body… </app-bs-accordion-item>
 *   </app-bs-accordion>
 */
@Component({
  selector: 'app-bs-accordion-item',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'bs-acc-item' },
  template: `
    <button type="button" class="bs-acc-head" [attr.aria-expanded]="open()"
            [disabled]="disabled()" (click)="toggle()">
      <span class="bs-acc-label">{{ label() }}</span>
      @if (hint()) { <span class="bs-acc-hint">{{ hint() }}</span> }
      <span class="bs-acc-chev" [class.is-open]="open()" aria-hidden="true">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none"
             stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
          <polyline points="9 6 15 12 9 18" />
        </svg>
      </span>
    </button>
    <div class="bs-acc-panel" [class.is-open]="open()" [attr.aria-hidden]="!open()">
      <div class="bs-acc-panel-inner"><ng-content></ng-content></div>
    </div>
  `,
  styles: [`
    :host(.bs-acc-item) {
      display: block; border-radius: var(--r-tile);
      background: var(--bg-rise); border: 1px solid var(--hairline);
      box-shadow: var(--lift-1); overflow: hidden;
    }
    .bs-acc-head {
      width: 100%; min-height: 44px; box-sizing: border-box;
      display: flex; align-items: center; gap: 10px;
      padding: 12px 14px; border: none; background: transparent; text-align: left;
      font-family: var(--font-ui); font-size: 15px; font-weight: 700; color: var(--ink);
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
    }
    .bs-acc-head:disabled { opacity: .45; pointer-events: none; }
    .bs-acc-head:focus-visible { outline: 2px solid var(--focus); outline-offset: -2px; border-radius: var(--r-tile); }
    .bs-acc-label { flex: 1 1 auto; min-width: 0; }
    .bs-acc-hint {
      flex: 0 0 auto; font-size: 13px; font-weight: 600; color: var(--ink-dim);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 45%;
    }
    .bs-acc-chev {
      flex: 0 0 auto; display: inline-flex; color: var(--ink-dim);
      transition: transform 260ms var(--ease-out), color 200ms var(--ease-out);
    }
    .bs-acc-chev.is-open { transform: rotate(90deg); color: var(--accent-a); }
    /* grid 0fr→1fr gives a content-agnostic smooth height reveal (no fixed max-height guessing). */
    .bs-acc-panel {
      display: grid; grid-template-rows: 0fr;
      transition: grid-template-rows 300ms var(--ease-out);
    }
    .bs-acc-panel.is-open { grid-template-rows: 1fr; }
    .bs-acc-panel-inner {
      min-height: 0; overflow: hidden;
      padding: 0 14px;
      transition: padding 300ms var(--ease-out);
    }
    .bs-acc-panel.is-open .bs-acc-panel-inner { padding-bottom: 14px; }
    @media (prefers-reduced-motion: reduce) {
      .bs-acc-chev, .bs-acc-panel, .bs-acc-panel-inner { transition: none; }
    }
  `],
})
export class BetaAccordionItem {
  /** The header text. */
  readonly label = input.required<string>();
  /** Optional muted text on the right of the header (e.g. a count). */
  readonly hint = input<string>('');
  /** Two-way expanded state; the container may flip this to enforce single-open. */
  readonly open = model<boolean>(false);
  /** When true the row cannot be toggled. */
  readonly disabled = input<boolean>(false);
  /** Fired with the new open state on a user toggle. */
  readonly toggled = output<boolean>();

  protected toggle(): void {
    if (this.disabled()) return;
    const next = !this.open();
    this.open.set(next);
    this.toggled.emit(next);
  }
}

@Component({
  selector: 'app-bs-accordion',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'bs-acc', role: 'group' },
  template: `<ng-content></ng-content>`,
  styles: [`
    :host(.bs-acc) { display: flex; flex-direction: column; gap: 10px; }
  `],
})
export class BetaAccordion {
  /** Enforce one-open-at-a-time: opening a row closes its siblings. */
  readonly single = input<boolean>(false);

  private readonly items = contentChildren(BetaAccordionItem);
  /** Tracks which item is currently the one enforced-open (single mode). */
  private readonly lastOpen = signal<BetaAccordionItem | null>(null);

  constructor() {
    // In single mode, watch every item's open() and close the others when one opens.
    effect(() => {
      if (!this.single()) return;
      const items = this.items();
      const nowOpen = items.filter(i => i.open());
      const prev = this.lastOpen();
      // A NEW item opened (one not previously the tracked-open one) => close all others.
      const fresh = nowOpen.find(i => i !== prev);
      if (fresh) {
        for (const i of items) if (i !== fresh && i.open()) i.open.set(false);
        this.lastOpen.set(fresh);
      } else if (nowOpen.length === 0) {
        this.lastOpen.set(null);
      }
    });
  }
}
