/* Vertex AutoTrade — minimal PWA service worker (Blazor Server)
 * Caches static assets only. Live trading still needs network + SignalR.
 */
const CACHE = 'vertex-static-v1';
const PRECACHE = [
  '/',
  '/favicon.png',
  '/images/vertexailogo.png',
  '/css/site.css',
  '/manifest.webmanifest'
];

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(CACHE).then((c) => c.addAll(PRECACHE).catch(() => {})).then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((keys) =>
      Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  if (req.method !== 'GET') return;

  const url = new URL(req.url);
  // Never cache Blazor Server / hubs / API
  if (url.pathname.startsWith('/_blazor') ||
      url.pathname.startsWith('/hubs') ||
      url.pathname.startsWith('/api') ||
      url.pathname.includes('blazor.server.js')) {
    return;
  }

  // Static: cache-first
  if (url.pathname.startsWith('/css') ||
      url.pathname.startsWith('/js') ||
      url.pathname.startsWith('/lib') ||
      url.pathname.startsWith('/images') ||
      url.pathname.endsWith('.png') ||
      url.pathname.endsWith('.webmanifest')) {
    event.respondWith(
      caches.match(req).then((hit) => hit || fetch(req).then((res) => {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(req, copy)).catch(() => {});
        return res;
      }).catch(() => hit))
    );
    return;
  }

  // Navigation: network-first, fallback cache
  if (req.mode === 'navigate') {
    event.respondWith(
      fetch(req).catch(() => caches.match('/') || caches.match(req))
    );
  }
});
