import { Directive, input, output } from '@angular/core';

/**
 * The shared reorder contract every Atrium widget exposes to the page: a `reordering` input (long-press
 * mode on/off) and three outputs the {@link AtriumWidgetShell}'s move/hide buttons emit. Widgets extend
 * this so the page can wire `[reordering]`, `(moveUp)`, `(moveDown)`, `(hide)` uniformly. Beta-only; no
 * live imports.
 */
@Directive()
export abstract class ReorderableWidget {
  /** Whether the parent page is in long-press reorder mode. */
  readonly reordering = input<boolean>(false);

  readonly moveUp = output<void>();
  readonly moveDown = output<void>();
  readonly hide = output<void>();
}
