import { TourDef } from '../../core/tour';

/**
 * First-run DESKTOP tour. Anchored entirely on the dashboard + the persistent top nav so it never
 * navigates mid-tour (robust: a route change would unmount anchors). Each step targets a stable
 * `data-tour` id; any whose target is absent (a permission-gated nav group the user can't see) is
 * skipped gracefully by {@link GuidedTour}.
 *
 * Order teaches breadth: where your usage lives → the breadth of sections (Fitness, Family, Social,
 * Tools, the Ask/command entry) → wraps on a key dashboard element.
 */
const DESKTOP_TOUR: TourDef = {
  id: 'home_v1',
  steps: [
    {
      anchor: 'dash-kpis',
      title: 'Your usage at a glance',
      blurb:
        'Cost, tokens, input vs. output and active hours for the current range — your AI spend, summarized.',
      placement: 'bottom',
    },
    {
      anchor: 'nav-fitness',
      title: 'Fitness & habits',
      blurb: 'Tracker, 75 Hard, trophies and your activity feed — health goals live here alongside usage.',
      placement: 'bottom',
    },
    {
      anchor: 'nav-tools',
      title: 'Ask — AI that acts',
      blurb:
        'Tools is home to Ask: ask in plain language and it does the work — log a meal, add a bill, plan groceries.',
      placement: 'bottom',
    },
    {
      anchor: 'nav-social',
      title: 'Social & people',
      blurb: 'Chat with your team and manage the people across your contacts, family and fleet.',
      placement: 'bottom',
    },
    {
      anchor: 'nav-family',
      title: 'The Family Hub',
      blurb: 'A private space for calendar, lists, chores and finances — shared with the people you live with.',
      placement: 'bottom',
    },
    {
      anchor: 'nav-ask',
      title: 'Search anything — ⌘K',
      blurb:
        'Press ⌘K (or click here) anytime to search and run commands from anywhere in the app.',
      placement: 'left',
    },
  ],
};

/**
 * First-run MOBILE tour. The mobile bottom-tab shell has a different anchor set, so it gets its own short
 * tour over the fixed tabs + the camera FAB + "More". Same robustness: missing tabs are skipped.
 */
const MOBILE_TOUR: TourDef = {
  id: 'home_mobile_v1',
  steps: [
    {
      anchor: 'tab-dashboard',
      title: 'Home',
      blurb: 'Your dashboard — usage cost, tokens and activity at a glance. Tap to come back here anytime.',
      placement: 'top',
    },
    {
      anchor: 'tab-tracker',
      title: 'Tracker',
      blurb: 'Log food, weight and workouts toward your goals — one tap away on the bar.',
      placement: 'top',
    },
    {
      anchor: 'snap-fab',
      title: 'Snap & route',
      blurb: 'Point your camera at a receipt, label or meal and it routes the photo to the right place.',
      placement: 'top',
    },
    {
      anchor: 'tab-family',
      title: 'Family',
      blurb: 'Your shared hub — calendar, lists, chores and finances for everyone at home.',
      placement: 'top',
    },
    {
      anchor: 'tab-more',
      title: 'Everything else',
      blurb: 'Tap More for every other section, plus Ask, settings and your account.',
      placement: 'top',
    },
  ],
};

/** Pick the right first-run tour for the active shell. */
export function tourForPlatform(isMobile: boolean): TourDef {
  return isMobile ? MOBILE_TOUR : DESKTOP_TOUR;
}
