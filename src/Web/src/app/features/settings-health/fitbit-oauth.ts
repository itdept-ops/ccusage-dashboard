// ============================================================================
// fitbit-oauth.ts — the browser half of the Fitbit OAuth 2.0 auth-code + PKCE
// flow. PKCE (S256) means no client secret ever touches the SPA: we generate a
// random `code_verifier`, send only its SHA-256 challenge to Fitbit's authorize
// endpoint, stash the verifier in sessionStorage, and on the callback hand the
// raw verifier + the one-time code to POST /api/health/connect for the exchange.
//
// Shared by the desktop (settings-health) + mobile (settings-health-mobile) pages.
// ============================================================================

const AUTHORIZE_URL = 'https://www.fitbit.com/oauth2/authorize';
/** Where Fitbit redirects back to — the same value sent verbatim to /connect. */
const REDIRECT_PATH = '/settings/health';
const SS_VERIFIER = 'uiq.fitbit.pkceVerifier';
const SS_REDIRECT = 'uiq.fitbit.redirectUri';
const SS_STATE = 'uiq.fitbit.state';

/** The absolute callback URL Fitbit redirects to (origin-relative so it works in every environment). */
export function fitbitRedirectUri(): string {
  return `${window.location.origin}${REDIRECT_PATH}`;
}

/** base64url (no padding) of an ArrayBuffer — the PKCE challenge encoding. */
function base64Url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** A high-entropy URL-safe code_verifier (RFC 7636 — 43..128 chars). */
function randomVerifier(): string {
  const bytes = new Uint8Array(64);
  crypto.getRandomValues(bytes);
  return base64Url(bytes.buffer);
}

async function challengeFor(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64Url(digest);
}

/**
 * Begin the connect flow: mint a PKCE verifier, persist it (+ the redirect URI) for the callback, build the
 * Fitbit authorize URL with the S256 challenge, and navigate the browser there. The function does not return
 * in practice (the page unloads on redirect).
 */
export async function beginFitbitAuthorize(clientId: string, scopes: string): Promise<void> {
  const verifier = randomVerifier();
  const challenge = await challengeFor(verifier);
  const state = randomVerifier(); // a high-entropy CSRF token bound to this attempt
  const redirectUri = fitbitRedirectUri();

  try {
    sessionStorage.setItem(SS_VERIFIER, verifier);
    sessionStorage.setItem(SS_REDIRECT, redirectUri);
    sessionStorage.setItem(SS_STATE, state);
  } catch {
    /* sessionStorage unavailable (private mode) — the callback will surface a friendly retry */
  }

  const params = new URLSearchParams({
    response_type: 'code',
    client_id: clientId,
    scope: scopes,
    redirect_uri: redirectUri,
    code_challenge: challenge,
    code_challenge_method: 'S256',
    state,
  });
  window.location.assign(`${AUTHORIZE_URL}?${params.toString()}`);
}

/** What the callback hands to POST /connect. */
export interface PendingFitbitCode {
  code: string;
  redirectUri: string;
  verifier: string;
}

/**
 * If the current URL is an OAuth callback (carries `?code`), pull the code, pair it with the stashed PKCE
 * verifier + redirect URI, CLEAR them, and strip the OAuth params from the address bar so a refresh can't
 * re-trigger the exchange. Returns null when there's no pending callback (the normal page load).
 */
export function consumePendingFitbitCode(): PendingFitbitCode | null {
  const url = new URL(window.location.href);
  const code = url.searchParams.get('code');
  const returnedState = url.searchParams.get('state');
  const hasOAuthParams = code != null || url.searchParams.has('error');
  if (!hasOAuthParams) return null;

  let verifier: string | null = null;
  let redirectUri: string | null = null;
  let storedState: string | null = null;
  try {
    verifier = sessionStorage.getItem(SS_VERIFIER);
    redirectUri = sessionStorage.getItem(SS_REDIRECT);
    storedState = sessionStorage.getItem(SS_STATE);
    sessionStorage.removeItem(SS_VERIFIER);
    sessionStorage.removeItem(SS_REDIRECT);
    sessionStorage.removeItem(SS_STATE);
  } catch {
    /* ignore */
  }

  // Strip ?code/?error/?state so a refresh doesn't replay a now-spent code.
  url.searchParams.delete('code');
  url.searchParams.delete('error');
  url.searchParams.delete('state');
  window.history.replaceState({}, '', url.pathname + url.search + url.hash);

  // CSRF defense: the returned state MUST match the one we generated for this attempt; reject otherwise
  // (PKCE already binds the code to our verifier server-side — this closes the callback-CSRF gap too).
  if (!code || !verifier || !storedState || returnedState !== storedState) return null;
  return { code, redirectUri: redirectUri ?? fitbitRedirectUri(), verifier };
}
