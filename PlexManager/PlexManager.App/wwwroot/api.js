// Everything the screens know about the local API. Nothing here talks to Plex, Trakt, Radarr or Sonarr
// directly: the exe does that, so no token ever reaches this page.

async function send(method, path, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }
  const response = await fetch('/api' + path, options);
  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = { error: text };
    }
  }
  if (!response.ok) {
    throw new Error((payload && (payload.error || payload.message)) || `${response.status} ${response.statusText}`);
  }
  return payload;
}

export const api = {
  status: () => send('GET', '/status'),

  getSettings: () => send('GET', '/settings'),
  saveSettings: (settings) => send('PUT', '/settings', settings),
  completeSetup: () => send('POST', '/settings/complete'),

  test: (service) => send('POST', `/test/${service}`),
  discoverKey: (service) => send('POST', `/discover-key/${service}`),

  plexSignInStart: () => send('POST', '/signin/plex/start'),
  plexSignInPoll: (handle) => send('POST', `/signin/plex/poll?handle=${encodeURIComponent(handle)}`),
  traktSignInStart: () => send('POST', '/signin/trakt/start'),
  traktSignInPoll: (handle) => send('POST', `/signin/trakt/poll?handle=${encodeURIComponent(handle)}`),
  traktSignOut: () => send('POST', '/signin/trakt/out'),
  tmdbRegions: () => send('GET', '/tmdb/regions'),

  traktLists: () => send('GET', '/trakt/lists'),

  runs: (kind) => send('GET', '/runs' + (kind ? `?kind=${encodeURIComponent(kind)}` : '')),
  run: (id) => send('GET', `/runs/${encodeURIComponent(id)}`),
  saveRows: (id, rows) => send('PUT', `/runs/${encodeURIComponent(id)}/rows`, rows),

  startJob: (operation, options = {}) => send('POST', '/jobs', { operation, ...options }),
  job: (id, from = 0) => send('GET', `/jobs/${encodeURIComponent(id)}?from=${from}`),
  cancelJob: (id) => send('POST', `/jobs/${encodeURIComponent(id)}/cancel`),

  reveal: (path) => send('POST', '/reveal', { path }),
  fileUrl: (path) => `/api/file?path=${encodeURIComponent(path)}`,

  importScan: (folder) => send('GET', '/import/scan' + (folder ? `?folder=${encodeURIComponent(folder)}` : '')),
  runImport: (request) => send('POST', '/import', request),
};
