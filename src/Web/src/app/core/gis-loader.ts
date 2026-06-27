/**
 * On-demand loader for Google Identity Services (the `accounts.google.com/gsi/client` library).
 *
 * The GIS script used to be a global `<script>` in index.html, so it downloaded on EVERY route (all the
 * marketing pages + the ~40 mobile twins) even though it's only needed on the handful of surfaces that
 * actually render Google sign-in / OAuth (the /signin button + One Tap, and the family calendar's Google
 * Calendar connect flow). This loads it lazily instead: the first consumer that calls {@link ensureGis}
 * injects the async script, and the shared promise resolves once `window.google.accounts` is ready.
 *
 * Behaviour:
 *  - Idempotent: the script is injected at most once; concurrent + later callers share the one promise.
 *  - Resolves with the `google` global so callers can use `.accounts.id` / `.accounts.oauth2`.
 *  - Rejects (after a ~10s poll) if the script never loads or `accounts` never appears, so callers can show
 *    their existing "couldn't load Google sign-in" fallback. A rejection clears the cache so a later retry
 *    (e.g. the user clicking Connect again after a flaky network) re-attempts the injection cleanly.
 */

const GIS_SRC = 'https://accounts.google.com/gsi/client';

/** The in-flight / resolved load promise; null until first requested (or after a failed attempt). */
let gisPromise: Promise<any> | null = null;

/** True once `window.google.accounts` is present — both `.id` and `.oauth2` hang off `accounts`. */
function gisReady(): boolean {
  return !!(window as unknown as { google?: any }).google?.accounts;
}

/**
 * Ensure the GIS library is loaded, resolving with the `google` global. Injects the script on the first
 * call and shares the resulting promise; safe to call from multiple components / repeatedly.
 */
export function ensureGis(): Promise<any> {
  if (gisReady()) return Promise.resolve((window as unknown as { google: any }).google);
  if (gisPromise) return gisPromise;

  gisPromise = new Promise<any>((resolve, reject) => {
    const start = Date.now();
    // Poll for `google.accounts` to appear once the script executes. The id/oauth2 namespaces attach a
    // tick after the script's onload, so we poll rather than resolve straight from onload.
    const poll = () => {
      if (gisReady()) {
        resolve((window as unknown as { google: any }).google);
      } else if (Date.now() - start > 10_000) {
        gisPromise = null; // allow a later retry to re-inject
        reject(new Error('Google Identity Services failed to load'));
      } else {
        setTimeout(poll, 100);
      }
    };

    // Reuse an existing tag if one is already on the page (e.g. a prior call, or a future re-add).
    const existing = document.querySelector<HTMLScriptElement>(`script[src="${GIS_SRC}"]`);
    if (existing) {
      existing.addEventListener('error', () => {
        gisPromise = null;
        reject(new Error('Google Identity Services failed to load'));
      });
      poll();
      return;
    }

    const script = document.createElement('script');
    script.src = GIS_SRC;
    script.async = true;
    script.defer = true;
    script.addEventListener('error', () => {
      gisPromise = null;
      reject(new Error('Google Identity Services failed to load'));
    });
    script.addEventListener('load', poll);
    document.head.appendChild(script);
    // Belt-and-suspenders: also start polling immediately in case `load` already fired (cached script).
    poll();
  });

  return gisPromise;
}
