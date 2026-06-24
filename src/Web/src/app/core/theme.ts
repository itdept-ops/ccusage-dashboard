import { Injectable, signal, computed, effect } from '@angular/core';

/** The three user-selectable theme modes. 'system' follows the OS `prefers-color-scheme`. */
export type ThemeMode = 'system' | 'light' | 'dark';

/** The actually-applied palette after resolving 'system'. */
export type ResolvedTheme = 'light' | 'dark';

/** localStorage key — MUST match the no-flash bootstrap in index.html. */
const THEME_KEY = 'uiq.theme';

/**
 * Owns the app's light/dark theme at runtime.
 *
 * The COLD-START application of the saved theme lives in an inline script in index.html (so the very
 * first paint already carries the right `data-theme` — no flash). This service then takes over:
 *   - exposes the user's chosen {@link ThemeMode} (persisted to localStorage) as a signal,
 *   - re-applies `data-theme` on <html> whenever the mode (or, for 'system', the OS preference) changes,
 *   - listens to `matchMedia('(prefers-color-scheme: …)')` so 'system' tracks the OS live.
 *
 * `data-theme` drives the `[data-theme="light"]` palette override in styles.scss; absence/`"dark"` is the
 * default dark console. The inline bootstrap and this service use the SAME key + resolution logic.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  /** The user's chosen mode (System / Light / Dark), seeded from localStorage. */
  private readonly _mode = signal<ThemeMode>(this.readMode());
  readonly mode = this._mode.asReadonly();

  /** The OS preference (only consulted when mode === 'system'), kept live by the matchMedia listener. */
  private readonly _systemDark = signal<boolean>(this.systemPrefersDark());

  /** The palette actually in effect, after resolving 'system' against the OS preference. */
  readonly resolved = computed<ResolvedTheme>(() => {
    const m = this._mode();
    if (m === 'light') return 'light';
    if (m === 'dark') return 'dark';
    return this._systemDark() ? 'dark' : 'light';
  });

  constructor() {
    // Keep <html data-theme> + the persisted choice in sync with the signals. Runs once on boot too,
    // which harmlessly re-affirms what the index.html bootstrap already set (idempotent).
    effect(() => {
      const resolved = this.resolved();
      if (typeof document !== 'undefined') {
        document.documentElement.dataset['theme'] = resolved;
      }
    });

    // Live OS-preference tracking for 'system' mode.
    if (typeof window !== 'undefined' && window.matchMedia) {
      const mq = window.matchMedia('(prefers-color-scheme: dark)');
      const onChange = (e: MediaQueryListEvent) => this._systemDark.set(e.matches);
      // addEventListener is the modern API; the deprecated addListener is the Safari <14 fallback.
      if (typeof mq.addEventListener === 'function') {
        mq.addEventListener('change', onChange);
      } else if (typeof mq.addListener === 'function') {
        // eslint-disable-next-line @typescript-eslint/no-deprecated
        mq.addListener(onChange);
      }
    }
  }

  /** Switch the active mode and persist it; the effect re-applies `data-theme`. */
  setMode(mode: ThemeMode): void {
    this._mode.set(mode);
    try {
      localStorage.setItem(THEME_KEY, mode);
    } catch {
      /* private mode / storage disabled — runtime still updates, just not persisted */
    }
  }

  private readMode(): ThemeMode {
    try {
      const v = localStorage.getItem(THEME_KEY);
      if (v === 'light' || v === 'dark' || v === 'system') return v;
    } catch {
      /* ignore */
    }
    return 'system';
  }

  private systemPrefersDark(): boolean {
    try {
      return !(
        typeof window !== 'undefined' &&
        window.matchMedia &&
        window.matchMedia('(prefers-color-scheme: light)').matches
      );
    } catch {
      return true;
    }
  }
}
