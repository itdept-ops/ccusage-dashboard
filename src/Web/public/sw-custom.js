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
    data: { url },
  };

  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const target = (event.notification.data && event.notification.data.url) || '/';

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
