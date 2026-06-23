import { Injectable, signal } from '@angular/core';

/**
 * Tiny shared state for the global command palette. The shell's key listener (⌘K / Ctrl-K / "/") and the
 * toolbar trigger button flip `open`; the {@link CommandPalette} component renders off it. Keeping this in
 * an injectable (rather than a `@ViewChild`) lets any caller drive the palette without a template round-trip
 * and keeps the shell edits minimal/additive.
 */
@Injectable({ providedIn: 'root' })
export class CommandPaletteService {
  /** Whether the palette overlay is currently shown. */
  readonly open = signal(false);

  toggle(): void { this.open.update(v => !v); }
  show(): void { this.open.set(true); }
  hide(): void { this.open.set(false); }
}
