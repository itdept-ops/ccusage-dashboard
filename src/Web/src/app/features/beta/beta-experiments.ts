/**
 * The single source of truth for the Beta section's experimental pages. Both the Beta hub
 * ({@link BetaHubPage}) grid AND the top-nav / mobile-drawer "Beta" dropdown iterate this list, so the
 * nav and the hub can never drift. Add a future beta page by appending ONE entry here (title, blurb,
 * route, icon, and an optional `perm` gate).
 *
 * Every entry additionally requires `platform.mobile` — that is the gate on the Beta dropdown trigger and
 * the route guard, so it is NOT repeated per entry. A `perm`, when set, is an ADDITIONAL feature gate
 * (e.g. `bills.use`, `family.use`) layered on top of `platform.mobile`.
 */
export interface BetaExperiment {
  readonly title: string;
  readonly blurb: string;
  readonly route: string;
  readonly icon: string;
  /** Optional ADDITIONAL permission gate (on top of platform.mobile) — the entry only renders if held. */
  readonly perm?: string;
  /**
   * Optional ADDITIONAL any-of permission gate (on top of platform.mobile) — the entry renders if the caller
   * holds AT LEAST ONE of these. Mirrors the route's `anyPermissionGuard(...)` for surfaces whose live
   * twin aggregates more than one feature (e.g. People = chat.read ∪ family.use, Fleet = fleet.view ∪
   * reporter.manage), so a caller holding only one of them still discovers the card.
   */
  readonly anyPerm?: readonly string[];
}

/**
 * Whether the caller may SEE this experiment in the hub grid / nav dropdown — its `perm` (if any) AND its
 * any-of `anyPerm` (if any) must both pass. `has` is a `hasPermission`-style probe. `platform.mobile` itself is
 * checked separately (the Beta dropdown trigger + the route guard), so it is not re-tested here.
 */
export function canSeeExperiment(x: BetaExperiment, has: (perm: string) => boolean): boolean {
  if (x.perm && !has(x.perm)) return false;
  if (x.anyPerm && !x.anyPerm.some(has)) return false;
  return true;
}

export const BETA_EXPERIMENTS: readonly BetaExperiment[] = [
  {
    title: 'Strata',
    blurb: 'Mobile-first clean-sheet fitness tracker (Strata)',
    route: '/tracker-beta',
    icon: 'fitness_center',
    perm: 'platform.mobile',
  },
  {
    title: 'Bills',
    blurb: 'Snap a receipt, split it, share a claim link — mobile-first',
    route: '/beta/bills',
    icon: 'receipt_long',
    perm: 'bills.use',
  },
  {
    title: 'Home',
    blurb: 'Your cross-domain glance surface — rings, events, who\'s online',
    route: '/beta/home',
    icon: 'space_dashboard',
    // No `perm` → visible to anyone who holds platform.mobile. The page's own widgets self-gate on their
    // domain perms, and the route guard re-checks platform.mobile on direct nav.
  },
  {
    title: 'Dashboard',
    blurb: 'Your token + cost analytics, glanceable on mobile',
    route: '/beta/dashboard',
    icon: 'insights',
    // No `perm` → the route guard re-checks platform.mobile; same data as the live dashboard.
  },
  {
    title: 'Family',
    blurb: 'Your household at a glance — mobile-first',
    route: '/beta/family',
    icon: 'cottage',
    // Gated on `family.use` (the feature); the route additionally STACKS platform.mobile + family.use, so a
    // direct nav re-checks both. Mirrors the live family glance — never surfaces cycle/finance data.
    perm: 'family.use',
  },
  {
    title: 'Wrapped',
    blurb: 'Your Hub, the highlight reel',
    route: '/beta/wrapped',
    icon: 'auto_awesome',
    // No `perm` → the route guard re-checks platform.mobile; the page itself is gated server-side by
    // tracker.self (the /api/wrapped endpoint), and only ever shows the caller's OWN data.
  },
  {
    title: 'Settings',
    blurb: 'Your quick toggles, mobile-first',
    route: '/beta/settings',
    icon: 'tune',
    // No `perm` → the route guard re-checks platform.mobile; the page mirrors the live Settings hub's quick
    // toggles and reuses the same per-user Api methods (each toggle still self-gates by its own perm).
  },
  {
    title: 'Messenger',
    blurb: 'Your channels and DMs — fast, native-feel chat with bubbles, reactions & live typing',
    route: '/beta/chat',
    icon: 'chat_bubble',
    // Gated on `chat.read` (the feature); the route additionally STACKS platform.mobile + chat.read, so a
    // direct nav re-checks both. Mirrors the live /chat over the same realtime data.
    perm: 'chat.read',
  },
  {
    title: 'Ask my life',
    blurb: 'Chat with an AI grounded in your own numbers',
    route: '/beta/ask',
    icon: 'auto_awesome',
    // Gated on `tracker.ai` (the OFF-by-default text-AI perm, same as the live /ask page + POST /api/ai/ask);
    // the route additionally STACKS platform.mobile + tracker.ai, so a direct nav re-checks both.
    perm: 'tracker.ai',
  },
  {
    title: 'Meals',
    blurb: 'Plan your week, swipe your days, fill the cart — mobile-first',
    route: '/beta/meals',
    icon: 'restaurant_menu',
    // Gated on `meals.use` (the feature); the route additionally STACKS platform.mobile + meals.use, so a
    // direct nav re-checks both. Mirrors the live /meal-planner over the same household meal/grocery data.
    perm: 'meals.use',
  },
  {
    title: 'People',
    blurb: 'Your circle, online-first — contacts + household, message or nudge in a tap',
    route: '/beta/people',
    icon: 'groups',
    // Any-of chat.read | family.use — mirrors the live /people aggregation gate; the route STACKS
    // platform.mobile + that any-of, so a chat-only OR family-only caller still discovers + can open it.
    anyPerm: ['chat.read', 'family.use'],
  },
  {
    title: 'Fleet',
    blurb: 'Every machine + reporter — live pulses, per-machine spend, and a top-user board',
    route: '/beta/fleet',
    icon: 'dns',
    // Any-of fleet.view | reporter.manage — mirrors the live /fleet gate; the route STACKS platform.mobile +
    // that any-of, so a caller holding only one of them still sees the card.
    anyPerm: ['fleet.view', 'reporter.manage'],
  },
  {
    title: 'Trophies',
    blurb: 'Your achievements wall — earned badges gleam, locked ones tease what\'s next',
    route: '/beta/trophies',
    icon: 'emoji_events',
    // Gated on `tracker.self`; the route STACKS platform.mobile + tracker.self. The page shows only the
    // caller's OWN milestones (name + userId, never an email).
    perm: 'tracker.self',
  },
  {
    title: 'Automations',
    blurb: 'Your if-this-then-that rules as WHEN → THEN cards — toggle, swipe, add on the go',
    route: '/beta/automations',
    icon: 'bolt',
    // Gated on `automations.use`; the route STACKS platform.mobile + automations.use. Mirrors the live
    // /automations page over the caller's own rules.
    perm: 'automations.use',
  },
];
