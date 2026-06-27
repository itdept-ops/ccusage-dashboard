import { NavGroup, PageDef, PAGE_REGISTRY } from './page-registry';

/**
 * The nav derivation from {@link PAGE_REGISTRY}: the grouped nav, the bottom tabs, and home-route normalization.
 * This replaces the two hand-written nav copies that lived in `app.html` (the desktop dropdowns + the mobile
 * drawer) with a single registry-derived source, so they cannot drift.
 *
 * Pure module: no Angular deps beyond the `NavGroup`/`PageDef` types. Callers pass a `has(perm)` predicate (wire it
 * to {@link AuthService.hasPermission}) so the gating stays reactive in the caller's signals/computeds.
 */

/** A single navigable page (a dropdown item / bottom tab / drawer row). */
export interface NavItem {
  id: string;
  path: string;       // canonical absolute route ('/' for home, '/' + path otherwise)
  label: string;
  icon: string;
  group: NavGroup;
}

/** A nav group with its (already permission-filtered) items. Emitted only when it has at least one item. */
export interface NavGroupModel {
  group: NavGroup;
  items: NavItem[];
}

/** The canonical absolute route for a page (`/` for the home page, `/` + path otherwise). */
function routeOf(p: PageDef): string {
  return p.path === '' ? '/' : '/' + p.path;
}

/**
 * Whether the caller can see a page: the gate is satisfied when its `perm` (if any) is held AND its `anyPerm` (if
 * any) has at least one held. A page with no `perm`/`anyPerm` (authOnly or ungated) is visible to any authed caller.
 */
function canSee(p: PageDef, has: (perm: string) => boolean): boolean {
  if (p.perm && !has(p.perm)) return false;
  if (p.anyPerm && !p.anyPerm.some(has)) return false;
  return true;
}

/** Map a registry page that carries `nav` into a {@link NavItem}. */
function toNavItem(p: PageDef): NavItem {
  return { id: p.id, path: routeOf(p), label: p.nav!.label, icon: p.nav!.icon, group: p.nav!.group };
}

/**
 * The nav grouped by {@link NavGroup}, in registry/declaration order, permission-filtered, with empty groups omitted.
 * Drives the desktop dropdowns and the mobile section list.
 */
export function navGroups(has: (perm: string) => boolean): NavGroupModel[] {
  const order: NavGroup[] = [];
  const byGroup = new Map<NavGroup, NavItem[]>();

  for (const p of PAGE_REGISTRY) {
    if (!p.nav || !canSee(p, has)) continue;
    const g = p.nav.group;
    let items = byGroup.get(g);
    if (!items) {
      items = [];
      byGroup.set(g, items);
      order.push(g);
    }
    items.push(toNavItem(p));
  }

  return order.map(group => ({ group, items: byGroup.get(group)! }));
}

/** The primary bottom tabs (pages flagged `nav.tab === true`) the caller can access — e.g. Home, Tracker, Chat, Family. */
export function bottomTabs(has: (perm: string) => boolean): NavItem[] {
  return PAGE_REGISTRY
    .filter(p => p.nav?.tab && canSee(p, has))
    .map(toNavItem);
}

/** Legacy mobile/beta home routes → the canonical registry page. Unknown routes pass through unchanged. */
const HOME_ALIASES: Readonly<Record<string, string>> = {
  '/tracker-beta': '/tracker',
  '/beta': '/',
  '/beta/home': '/',
  '/beta/dashboard': '/',
  '/beta/wrapped': '/',
  '/beta/family': '/family',
  '/beta/bills': '/bills',
  '/beta/chat': '/chat',
  '/beta/ask': '/ask',
  '/beta/meals': '/meal-planner',
  '/beta/people': '/people',
  '/beta/fleet': '/fleet',
  '/beta/trophies': '/trophies',
  '/beta/automations': '/automations',
  '/beta/settings': '/settings',
};

/** Normalize a (possibly legacy beta/mobile) home route to its canonical page; returns the input unchanged if not aliased. */
export function normalizeHome(route: string): string {
  return HOME_ALIASES[route] ?? route;
}
