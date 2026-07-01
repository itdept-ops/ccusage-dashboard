import {
  booleanAttribute, ChangeDetectionStrategy, Component, effect,
  input, model, output, signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';

/** A node in the checkbox tree. Recursive via `children`. */
export interface TreeNode {
  /** Stable, unique identifier — the value emitted in the selected set. */
  id: string | number;
  /** The visible label. */
  label: string;
  /** Optional leading glyph (Material ligature). */
  icon?: string;
  /** Nested children; a node with children acts as a parent group. */
  children?: TreeNode[];
  /** Start this branch expanded (parents only). Defaults to collapsed. */
  expanded?: boolean;
  /** When true this node (and its subtree) is inert. */
  disabled?: boolean;
}

/** The tri-state of a checkbox: off, on, or partially-selected (some descendants on). */
type CheckState = 'off' | 'on' | 'partial';

/**
 * BETA-KIT Tree — an expandable, nested checkbox tree with parent⇄child selection propagation.
 * Checking a PARENT toggles every (enabled) descendant on/off; a parent with a mix of checked and
 * unchecked leaves shows the INDETERMINATE state; checking the last child auto-checks the parent.
 * Each node carries an optional icon; the whole forest is driven by a recursive `nodes` input, and
 * the current selection is a two-way `selected` Set of node ids (also emitted via `selectedChange`
 * and the convenience `change` output).
 *
 * SELECTION MODEL: only LEAF ids live in the emitted set — a parent's state is DERIVED from its
 * leaves, so consumers get a clean "which leaves are chosen" set without having to interpret parent
 * ids. (Set `emitBranches` to also include a fully-checked parent's own id.) Disabled nodes are
 * skipped by propagation and never added by a parent toggle.
 *
 * Accents/checks read --accent-a/--accent-b; text --ink/--ink-dim; rows sit on --hairline guides.
 * DUAL-TOKEN: every token resolves the kit value FIRST with a --tech-* fallback, so the SAME
 * primitive renders on a kit-host page AND on a plain --tech-* desktop vertical. Rows are >=44px
 * touch targets; the disclosure caret + checkbox are keyboard-operable; the expand chevron rotates
 * with --ease-out (collapses to instant under reduced-motion via the page killswitch). Dependency-
 * free (Material icons only) + tree-shakeable.
 *
 * CONTRACT (next phase depends on this VERBATIM):
 *   selector:  app-bs-tree
 *   inputs:    nodes (TreeNode[], required — the recursive forest),
 *              selected (model<Set<string|number>>, two-way — the chosen LEAF ids),
 *              label (string, default '' — aria-label for the tree),
 *              emitBranches (boolean via booleanAttribute, default false — also include fully-checked parent ids in the set),
 *              disabled (boolean via booleanAttribute, default false — makes the whole tree inert)
 *   outputs:   change (Set<string|number>) — the new selection whenever it changes (mirrors selectedChange)
 *
 * Usage:
 *   <app-bs-tree [nodes]="categories" [(selected)]="chosen" label="Categories" (change)="onPick($event)" />
 */
@Component({
  selector: 'app-bs-tree',
  standalone: true,
  imports: [MatIconModule, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'tree', '[attr.aria-label]': 'label() || null' },
  template: `
    @for (n of nodes(); track n.id) {
      <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ n, depth: 0 }" />
    }

    <ng-template #row let-n="n" let-depth="depth">
      @let hasChildren = !!n.children?.length;
      @let state = stateOf(n);
      @let isOpen = openOf(n);
      <div class="tr-row" role="treeitem"
           [attr.aria-level]="depth + 1"
           [attr.aria-expanded]="hasChildren ? isOpen : null"
           [attr.aria-selected]="state === 'on'"
           [style.--tr-indent.px]="depth * 20">
        <button type="button" class="tr-twist"
                [class.tr-twist--leaf]="!hasChildren"
                [class.is-open]="isOpen"
                [disabled]="disabled() || n.disabled || !hasChildren"
                [attr.aria-hidden]="hasChildren ? null : 'true'"
                [attr.tabindex]="hasChildren ? 0 : -1"
                (click)="toggleOpen(n)">
          @if (hasChildren) { <mat-icon>chevron_right</mat-icon> }
        </button>

        <button type="button" class="tr-check"
                role="checkbox"
                [class.on]="state === 'on'"
                [class.partial]="state === 'partial'"
                [attr.aria-checked]="state === 'partial' ? 'mixed' : (state === 'on')"
                [attr.aria-label]="n.label"
                [disabled]="disabled() || n.disabled"
                (click)="toggleNode(n)">
          <span class="tr-box" aria-hidden="true">
            @if (state === 'on') { <mat-icon>check</mat-icon> }
            @else if (state === 'partial') { <span class="tr-dash"></span> }
          </span>
          @if (n.icon) { <mat-icon class="tr-glyph" aria-hidden="true">{{ n.icon }}</mat-icon> }
          <span class="tr-label">{{ n.label }}</span>
        </button>
      </div>

      @if (hasChildren && isOpen) {
        <div class="tr-children" role="group">
          @for (c of n.children; track c.id) {
            <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ n: c, depth: depth + 1 }" />
          }
        </div>
      }
    </ng-template>
  `,
  styles: [`
    /* Resolve each token once with a --tech-* fallback so the tree works on kit + tech pages alike. */
    :host {
      display: block;
      --tr-accent-a: var(--accent-a, var(--tech-accent, #7c8cff));
      --tr-accent-b: var(--accent-b, var(--tech-accent-2, var(--tech-accent, #3b82f6)));
      --tr-on-accent: var(--ink-on-accent, #fff);
      --tr-ink: var(--ink, var(--tech-text, #e6edf6));
      --tr-ink-dim: var(--ink-dim, var(--tech-text-secondary, #9ba9bd));
      --tr-edge: var(--hairline, var(--tech-border-subtle, rgba(255,255,255,.12)));
      --tr-r: var(--r-tile, var(--tech-r-control, 8px));
      --tr-focus: var(--focus, var(--tech-accent, #7c8cff));
      --tr-ease: var(--ease-out, cubic-bezier(.22,1,.36,1));
      --tr-font-ui: var(--font-ui, inherit);
    }
    .tr-row {
      display: flex; align-items: center; gap: 2px;
      min-height: 44px;
      padding-left: var(--tr-indent, 0px);
    }
    .tr-twist {
      flex: 0 0 auto; width: 30px; height: 44px;
      display: grid; place-items: center;
      border: 0; background: transparent; color: var(--tr-ink-dim);
      cursor: pointer; touch-action: manipulation; -webkit-tap-highlight-color: transparent;
    }
    .tr-twist--leaf { visibility: hidden; cursor: default; }
    .tr-twist mat-icon {
      font-size: 22px; width: 22px; height: 22px;
      transition: transform 180ms var(--tr-ease);
    }
    .tr-twist.is-open mat-icon { transform: rotate(90deg); }
    .tr-twist:disabled { cursor: default; }
    .tr-twist:focus-visible { outline: 2px solid var(--tr-focus); outline-offset: -2px; border-radius: var(--tr-r); }

    .tr-check {
      flex: 1 1 auto; min-width: 0; min-height: 44px;
      display: flex; align-items: center; gap: 10px;
      padding: 0 8px; border: 0; background: transparent;
      cursor: pointer; text-align: left;
      touch-action: manipulation; -webkit-tap-highlight-color: transparent;
      border-radius: var(--tr-r);
    }
    .tr-check:disabled { opacity: .45; pointer-events: none; }
    .tr-check:focus-visible { outline: 2px solid var(--tr-focus); outline-offset: -2px; }
    .tr-box {
      flex: 0 0 auto; width: 22px; height: 22px;
      display: grid; place-items: center;
      border-radius: 6px; border: 2px solid var(--tr-edge);
      color: var(--tr-on-accent);
      transition: background 140ms var(--tr-ease), border-color 140ms var(--tr-ease);
    }
    .tr-check.on .tr-box, .tr-check.partial .tr-box {
      border-color: transparent;
      background: linear-gradient(135deg, var(--tr-accent-a), var(--tr-accent-b));
    }
    .tr-box mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .tr-dash { width: 10px; height: 2px; border-radius: 1px; background: var(--tr-on-accent); }
    .tr-glyph { flex: 0 0 auto; font-size: 18px; width: 18px; height: 18px; color: var(--tr-ink-dim); }
    .tr-label {
      flex: 1 1 auto; min-width: 0;
      font-family: var(--tr-font-ui); font-size: 15px; font-weight: 600; color: var(--tr-ink);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .tr-children { display: block; }
  `],
})
export class BetaTree {
  /** The recursive forest. */
  readonly nodes = input.required<TreeNode[]>();
  /** Two-way set of selected LEAF ids (plus fully-checked parents when emitBranches). */
  readonly selected = model<Set<string | number>>(new Set());
  /** aria-label for the tree. */
  readonly label = input<string>('');
  /** Also include a fully-checked parent's own id in the emitted set. */
  readonly emitBranches = input<boolean, unknown>(false, { transform: booleanAttribute });
  /** Makes the whole tree inert (accepts the bare `disabled` attribute). */
  readonly disabled = input<boolean, unknown>(false, { transform: booleanAttribute });
  /** Fired with the new selection whenever it changes (mirrors selectedChange). */
  readonly change = output<Set<string | number>>();

  /** Per-node expand overrides (id -> open). Seeded from `node.expanded` lazily via openOf(). */
  private readonly openMap = signal<Map<string | number, boolean>>(new Map());

  constructor() {
    // Seed the open map from any node.expanded flags whenever the forest changes.
    effect(() => {
      const map = new Map<string | number, boolean>();
      const walk = (list: TreeNode[]) => {
        for (const n of list) {
          if (n.children?.length) {
            if (n.expanded) map.set(n.id, true);
            walk(n.children);
          }
        }
      };
      walk(this.nodes());
      this.openMap.set(map);
    });
  }

  /** Whether a branch is currently open (defaults to collapsed). */
  protected openOf(n: TreeNode): boolean {
    return this.openMap().get(n.id) ?? !!n.expanded;
  }

  protected toggleOpen(n: TreeNode): void {
    if (this.disabled() || n.disabled || !n.children?.length) return;
    const next = new Map(this.openMap());
    next.set(n.id, !this.openOf(n));
    this.openMap.set(next);
  }

  /** All enabled LEAF ids beneath a node (the node itself if it's an enabled leaf). */
  private leafIds(n: TreeNode, acc: (string | number)[] = []): (string | number)[] {
    if (n.disabled) return acc;
    if (n.children?.length) {
      for (const c of n.children) this.leafIds(c, acc);
    } else {
      acc.push(n.id);
    }
    return acc;
  }

  /** Tri-state of a node derived from the selection of its enabled leaves. */
  protected stateOf(n: TreeNode): CheckState {
    const sel = this.selected();
    if (!n.children?.length) return sel.has(n.id) ? 'on' : 'off';
    const leaves = this.leafIds(n);
    if (leaves.length === 0) return 'off';
    let on = 0;
    for (const id of leaves) if (sel.has(id)) on++;
    if (on === 0) return 'off';
    if (on === leaves.length) return 'on';
    return 'partial';
  }

  /** Toggle a node: leaf flips itself; a parent turns its whole enabled subtree on (unless already full → off). */
  protected toggleNode(n: TreeNode): void {
    if (this.disabled() || n.disabled) return;
    const next = new Set(this.selected());

    if (!n.children?.length) {
      if (next.has(n.id)) next.delete(n.id); else next.add(n.id);
    } else {
      const leaves = this.leafIds(n);
      const turnOn = this.stateOf(n) !== 'on'; // off or partial => turn all on; full => clear
      for (const id of leaves) { if (turnOn) next.add(id); else next.delete(id); }
    }

    this.reconcileBranches(next);
    this.selected.set(next);
    this.change.emit(next);
  }

  /** When emitBranches is on, add/remove every parent id to mirror whether its subtree is fully selected. */
  private reconcileBranches(set: Set<string | number>): void {
    if (!this.emitBranches()) return;
    const visit = (n: TreeNode): boolean => {
      if (!n.children?.length) return set.has(n.id);
      const leaves = this.leafIds(n);
      const full = leaves.length > 0 && leaves.every(id => set.has(id));
      if (full) set.add(n.id); else set.delete(n.id);
      for (const c of n.children) visit(c);
      return full;
    };
    for (const n of this.nodes()) visit(n);
  }
}
