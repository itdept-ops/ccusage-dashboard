// ============================================================================
// BETA-KIT — shared beta-UI foundation (the "Strata" design system, generalized).
//
// The SCSS token contract lives in ./_beta-kit.scss (adopt via `@use '../beta-ui/beta-kit'`).
// This barrel re-exports the standalone Angular primitives the (non-flagship) beta pages adopt.
// Everything is dependency-free + tree-shakeable; no imports from the flagship tracker-beta.
// ============================================================================

export { BetaBottomSheet, type SheetDetent } from './bottom-sheet';
export { BetaSwipeRow, type SwipeSide } from './swipe-row';
export { BetaPullRefresh } from './pull-to-refresh';
export { BetaSegmentedControl, type Segment } from './segmented-control';
export { BetaFab } from './fab';
export { BetaToaster, ToastController, type ToastMsg, type ToastTone } from './toast';
export { BetaSkeleton } from './skeleton';
export { BetaSectionHeader } from './section-header';
export { BetaStatTile } from './stat-tile';
export { BetaSvgRing } from './svg-ring';
export { BetaEmptyState, BetaErrorState } from './state-block';

// ---- FiMobile-adopted primitives (Wave A) ----
export { BetaStepper, type StepperSize } from './bs-stepper';
export { BetaChip, BetaChipGroup, type ChipVariant } from './bs-chip';
export { BetaTooltip, type TooltipPlacement } from './bs-tooltip';
export { BetaAccordion, BetaAccordionItem } from './bs-accordion';
export { BetaGauge } from './bs-gauge';
export { BetaDonut, type DonutSegment } from './bs-donut';
export { BetaTimeline, type TimelineItem, type TimelineColumn } from './bs-timeline';
export { BetaTree, type TreeNode } from './bs-tree';
export { BetaSuccess } from './bs-success';
