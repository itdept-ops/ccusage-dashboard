/*
 * Usage IQ custom service worker.
 *
 * Wraps Angular's generated ngsw-worker.js (which owns the app-shell caching + the SwPush/SwUpdate
 * message bridge) and adds the two things the Angular worker does NOT do for our payload shape:
 *
 *   1. A `push` handler that DISPLAYS a notification from the backend's FLAT payload `{ title, body, url }`.
 *      Angular's worker only auto-shows a notification when the payload has a nested `notification` key;
 *      our WebPushSender serializes the minimal flat shape, so we render it ourselves here.
 *   2. A `notificationclick` handler that focuses an existing app tab (or opens one) at the payload `url`.
 *
 * importScripts pulls in the Angular worker first so all of its own listeners (install/activate/fetch/
 * message/push for the in-tab bridge) remain wired; our listeners are additive.
 */
importScripts('ngsw-worker.js');

self.addEventListener('push', (event) => {
  if (!event.data) return;

  let payload;
  try {
    payload = event.data.json();
  } catch {
    payload = { title: 'Usage IQ', body: event.data.text() };
  }

  // Support both our flat shape { title, body, url } and a nested { notification: {...} } just in case.
  const n = payload && payload.notification ? payload.notification : payload || {};
  const title = n.title || 'Usage IQ';
  const url = n.url || (n.data && n.data.url) || '/';

  const options = {
    body: n.body || '',
    icon: 'favicon.svg',
    badge: 'favicon.svg',
    tag: n.tag || undefined,
    renotify: !!n.tag,
    data: { url, actionUrls: payload.actionUrls || (n.data && n.data.actionUrls) || {} },
  };

  // Optional rich notification actions (array of { action, title }).
  if (Array.isArray(payload.actions)) {
    options.actions = payload.actions;
  }

  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const action = event.action;
  const map = event.notification.data && event.notification.data.actionUrls;
  let target = (event.notification.data && event.notification.data.url) || '/';
  if (action && map && map[action]) {
    target = map[action];
  }

  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clients) => {
      // Focus an existing app window if one is open (navigate it to the target), else open a new one.
      for (const client of clients) {
        if ('focus' in client) {
          if ('navigate' in client && target) {
            return client.focus().then(() => client.navigate(target).catch(() => undefined));
          }
          return client.focus();
        }
      }
      if (self.clients.openWindow) return self.clients.openWindow(target);
      return undefined;
    }),
  );
});

/*
 * Web Share Target handler.
 *
 * The manifest registers POST /share as a share target (enctype multipart/form-data). When the OS
 * shares a photo + text into the app, the browser POSTs the form here. We CANNOT render the SPA from a
 * POST, so we stash the shared blob + text into a dedicated Cache and 303-redirect to /share?shared=1,
 * which the app loads (a GET) and then drains the cache. This is the ONLY request we intercept; every
 * other request is left untouched so ngsw-worker handles it normally.
 */
self.addEventListener('fetch', (event) => {
  const request = event.request;
  let url;
  try {
    url = new URL(request.url);
  } catch {
    return; // Malformed URL — let ngsw handle it.
  }

  if (request.method !== 'POST' || url.pathname !== '/share') {
    return; // Not our case: do NOT call respondWith.
  }

  event.respondWith(
    (async () => {
      try {
        const form = await request.formData();

        // Pull the photo file: prefer the named "photo" field, else the first File value present.
        let file = form.get('photo');
        if (!(file instanceof File)) {
          file = null;
          for (const value of form.values()) {
            if (value instanceof File) {
              file = value;
              break;
            }
          }
        }

        // Join the text fields (title + text + url), newline-separated, dropping blanks.
        const text = ['title', 'text', 'url']
          .map((key) => form.get(key))
          .filter((value) => typeof value === 'string' && value.length > 0)
          .join('\n');

        const cache = await caches.open('shared-media');
        if (file) {
          const type = file.type || 'application/octet-stream';
          await cache.put(
            '/__shared/photo',
            new Response(file, { headers: { 'Content-Type': type } }),
          );
        }
        await cache.put(
          '/__shared/text',
          new Response(text, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } }),
        );
      } catch {
        // Swallow — never surface a broken response; the app handles missing data gracefully.
      }
      return Response.redirect('/share?shared=1', 303);
    })(),
  );
});

/*
 * Background Sync: when the browser fires a "flush-queue" sync (queued while offline), tell all window
 * clients to drain their IndexedDB outbox. The SW itself never performs authenticated fetches.
 */
self.addEventListener('sync', (event) => {
  if (event.tag === 'flush-queue') {
    event.waitUntil(messageAllClients({ type: 'usageiq-flush-queue' }));
  }
});

/*
 * Periodic Background Sync: a "refresh" tag nudges open clients to do a light data refresh.
 */
self.addEventListener('periodicsync', (event) => {
  if (event.tag === 'refresh') {
    event.waitUntil(messageAllClients({ type: 'usageiq-periodic-refresh' }));
  }
});

// Post a message to every window client (controlled or not), guarded per-client.
function messageAllClients(msg) {
  return self.clients
    .matchAll({ type: 'window', includeUncontrolled: true })
    .then((clients) => {
      for (const client of clients) {
        try {
          client.postMessage(msg);
        } catch {
          // Ignore a single dead client.
        }
      }
    })
    .catch(() => undefined);
}
