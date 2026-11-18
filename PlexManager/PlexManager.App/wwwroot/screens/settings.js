import { el, card, listPicker, toast, confirmDialog } from '../ui.js';

/**
 * Settings, which is also the first-run wizard: one section per service with a Test connection button, the
 * sign-in buttons for Plex and Trakt, the list pickers, the rules, and the one-click import of the old files.
 */
export async function settings(app) {
  const current = await app.api.getSettings();
  let traktLists = null;

  const page = el('div');
  page.append(
    el('h1', { text: 'Settings' }),
    el('p', { class: 'lede', text: 'Every service in one place, each with a button that makes one read-only call so you can see it works. Tokens and API keys are encrypted for your Windows account, and nothing here is ever sent to the browser once it is saved.' }),
  );

  const saveBar = el('div', { class: 'row', style: 'position:sticky;bottom:0;background:var(--bg);padding:12px 0;z-index:4' });
  const status = el('span', { class: 'muted small' });

  async function save(quiet) {
    try {
      Object.assign(current, await app.api.saveSettings(current));
      await app.refreshStatus();
      if (!quiet) toast('Saved.', 'ok');
      return true;
    } catch (error) {
      toast(error.message, 'bad');
      return false;
    }
  }

  async function refreshLists(force) {
    if (traktLists && !force) return traktLists;
    const result = await app.api.traktLists();
    if (!result.ok) {
      toast(`Trakt lists could not be read: ${result.message}`, 'bad');
      traktLists = [];
    } else {
      traktLists = result.lists;
    }
    return traktLists;
  }

  page.append(await importCard(app, current));
  page.append(plexCard(app, current, save));
  page.append(arrCard(app, current, 'radarr', 'Radarr', 'Movies. 7878 by default.', save));
  page.append(arrCard(app, current, 'sonarr', 'Sonarr', 'TV series. 8989 by default.', save));
  page.append(traktCard(app, current, save));
  page.append(metadataCard(app, current, save));
  page.append(await listsCard(current, refreshLists));
  page.append(rulesCard(current));
  page.append(exclusionsCard(current));
  page.append(knownTitlesCard(current));
  page.append(dataCard(app, current));

  saveBar.append(
    el('button', {
      class: 'primary',
      text: 'Save settings',
      onclick: async () => {
        if (await save()) {
          await app.api.completeSetup();
          await app.refreshStatus();
        }
      },
    }),
    el('button', { text: 'Reload', onclick: () => app.render() }),
    status,
  );
  page.append(saveBar);

  return page;
}

// ------------------------------------------------------------------ import (phase 6)

async function importCard(app, current) {
  let scan;
  try {
    scan = await app.api.importScan();
  } catch {
    scan = { found: false };
  }

  if (!scan.found) {
    return el('div');
  }

  const found = [
    scan.hasConfig && 'plexmanager.config.json',
    scan.hasTraktConfig && 'trakt.config.json',
    scan.hasTraktTokens && 'trakt.tokens.json',
    scan.hasExclusions && 'plexmanager.exclusions.json',
    scan.backupFolders > 0 && `${scan.backupFolders} backup folders`,
    scan.reportEntries > 0 && `${scan.reportEntries} report entries`,
  ].filter(Boolean);

  const copyData = el('input', { type: 'checkbox' });
  copyData.checked = true;

  return card('Bring the old files over', [
    el('p', { class: 'muted small', style: 'margin:0 0 8px' }, [
      'An existing PlexManager folder was found at ',
      el('span', { class: 'mono', text: scan.folder }),
      '. Importing it fills in every setting below, including the Trakt sign-in, so nothing has to be typed again.',
    ]),
    el('div', { class: 'chips', style: 'margin-bottom:10px' }, found.map((name) => el('span', { class: 'pill', text: name }))),
    el('div', { class: 'row' }, [
      el('label', { class: 'inline' }, [copyData, 'also copy the caches, backups and reports']),
      el('button', {
        class: 'primary',
        text: 'Import',
        onclick: async () => {
          try {
            const result = await app.api.runImport({ folder: scan.folder, copyData: copyData.checked });
            toast(`Imported: ${result.imported.join('; ')}`, 'ok');
            await app.refreshStatus();
            app.render();
          } catch (error) {
            toast(error.message, 'bad');
          }
        },
      }),
    ]),
  ]);
}

// ------------------------------------------------------------------ Plex

function plexCard(app, current, save) {
  const plex = current.plex;
  const result = el('span', { class: 'muted small' });
  const sections = el('div', { class: 'row', style: 'margin-top:10px' });

  function drawSections(list) {
    sections.replaceChildren();
    if (!list?.length) return;

    for (const [key, label] of [['movieSectionId', 'Movie library'], ['showSectionId', 'TV library']]) {
      const select = el('select');
      for (const section of list) {
        select.append(el('option', { value: String(section.id), text: `${section.title} (${section.type})` }));
      }
      select.value = String(plex[key]);
      select.addEventListener('change', () => {
        plex[key] = Number(select.value);
        const chosen = list.find((s) => String(s.id) === select.value);
        plex[key === 'movieSectionId' ? 'movieSectionName' : 'showSectionName'] = chosen?.title || '';
      });
      sections.append(el('label', { class: 'field' }, [label, select]));
    }
  }

  return card('Plex', [
    el('div', { class: 'grid2' }, [
      field('Host or IP', plex, 'host', 'the Plex server address'),
      numberField('Port', plex, 'port'),
      checkField('Use HTTPS', plex, 'useHttps'),
      secretField('Token', plex, 'token', 'paste one, or use Sign in with Plex'),
    ]),
    el('div', { class: 'row', style: 'margin-top:12px' }, [
      el('button', {
        text: 'Sign in with Plex',
        onclick: () => plexSignIn(app, current, save, result),
      }),
      el('button', {
        text: 'Test connection',
        onclick: async () => {
          if (!await save(true)) return;
          result.textContent = 'testing...';
          const test = await app.api.test('plex');
          result.textContent = test.message;
          result.className = `small ${test.ok ? 'pill ok' : 'pill bad'}`;
          if (test.ok) drawSections(test.sections);
        },
      }),
      result,
    ]),
    el('p', { class: 'muted small', style: 'margin:10px 0 0', text: 'The two libraries below are found by Test connection; the old tools had their section numbers compiled in.' }),
    sections,
  ], 'Where your library lives. Everything else reads from it; only the Fix and Prune screens ever write to it.');
}

async function plexSignIn(app, current, save, result) {
  try {
    const start = await app.api.plexSignInStart();
    const dialog = signInDialog({
      title: 'Sign in with Plex',
      code: start.code,
      url: start.url,
      note: 'Approve the code on plex.tv. The token comes back here and is saved for you.',
    });

    let stop = false;
    dialog.addEventListener('close', () => { stop = true; });

    while (!stop) {
      await new Promise((resolve) => setTimeout(resolve, 2000));
      if (stop) break;
      const poll = await app.api.plexSignInPoll(start.handle);
      if (poll.state === 'signedin') {
        dialog.close();
        toast(poll.message, 'ok');
        result.textContent = poll.message;
        await app.refreshStatus();
        app.render();
        return;
      }
      if (poll.state === 'failed') {
        dialog.close();
        toast(poll.message, 'bad');
        return;
      }
    }
  } catch (error) {
    toast(error.message, 'bad');
  }
}

// ------------------------------------------------------------------ Radarr and Sonarr

function arrCard(app, current, key, title, lede, save) {
  const arr = current[key];
  const result = el('span', { class: 'muted small' });
  const options = el('div', { class: 'grid2', style: 'margin-top:10px' });

  function drawOptions(profiles, roots) {
    options.replaceChildren();
    if (profiles?.length) {
      const select = el('select');
      for (const profile of profiles) {
        select.append(el('option', { value: String(profile.id), text: profile.name }));
      }
      select.value = String(arr.qualityProfileId || profiles[0].id);
      select.addEventListener('change', () => {
        arr.qualityProfileId = Number(select.value);
        arr.qualityProfileName = profiles.find((p) => String(p.id) === select.value)?.name || '';
      });
      options.append(el('label', { class: 'field' }, ['Quality profile', select]));
    }

    if (roots?.length) {
      const select = el('select');
      for (const root of roots) {
        select.append(el('option', { value: root, text: root }));
      }
      select.value = arr.rootFolder || roots[0];
      select.addEventListener('change', () => { arr.rootFolder = select.value; });
      options.append(el('label', { class: 'field' }, ['Root folder', select]));
    }
  }

  if (arr.qualityProfileName || arr.rootFolder) {
    options.append(el('span', { class: 'muted small', text: `Currently: ${arr.qualityProfileName || 'no profile'}, ${arr.rootFolder || 'no root folder'}. Test connection to change them.` }));
  }

  return card(title, [
    el('div', { class: 'grid2' }, [
      field('Host or IP', arr, 'host', 'the server address'),
      numberField('Port', arr, 'port'),
      checkField('Use HTTPS', arr, 'useHttps'),
      field('URL base', arr, 'urlBase', 'only when it sits behind a path'),
      secretField('API key', arr, 'apiKey', 'Settings, General in the app'),
    ]),
    el('div', { class: 'row', style: 'margin-top:12px' }, [
      el('button', {
        text: 'Read the key from the server',
        onclick: async () => {
          if (!await save(true)) return;
          const found = await app.api.discoverKey(key);
          result.textContent = found.message;
          result.className = `small ${found.ok ? 'pill ok' : 'pill warn'}`;
          if (found.ok) app.render();
        },
      }),
      el('button', {
        text: 'Test connection',
        onclick: async () => {
          if (!await save(true)) return;
          result.textContent = 'testing...';
          const test = await app.api.test(key);
          result.textContent = test.message;
          result.className = `small ${test.ok ? 'pill ok' : 'pill bad'}`;
          if (test.ok) drawOptions(test.profiles, test.rootFolders);
        },
      }),
      result,
    ]),
    options,
  ], lede);
}

// ------------------------------------------------------------------ Trakt

function traktCard(app, current, save) {
  const trakt = current.trakt;
  const result = el('span', { class: 'muted small' });
  const signedIn = (app.status?.services || []).find((s) => s.name === 'Trakt')?.connected;

  return card('Trakt', [
    el('p', { class: 'muted small', style: 'margin:0 0 10px' }, [
      'Trakt has no way to create an API app for you. Make one at ',
      el('a', { href: 'https://trakt.tv/oauth/applications', target: '_blank', rel: 'noreferrer', text: 'trakt.tv/oauth/applications' }),
      ', set its redirect URI to ',
      el('span', { class: 'mono', text: trakt.redirectUri || 'http://localhost' }),
      ', and paste the Client ID here. Then Connect, which shows a code to approve on trakt.tv.',
    ]),
    el('div', { class: 'grid2' }, [
      field('Client ID', trakt, 'clientId', 'from your Trakt API app'),
      field('Redirect URI', trakt, 'redirectUri', 'must match the app'),
    ]),
    el('div', { class: 'row', style: 'margin-top:12px' }, [
      el('button', { class: signedIn ? '' : 'primary', text: signedIn ? 'Connect again' : 'Connect Trakt', onclick: () => traktSignIn(app, current, save, result) }),
      el('button', {
        text: 'Test connection',
        onclick: async () => {
          if (!await save(true)) return;
          result.textContent = 'testing...';
          const test = await app.api.test('trakt');
          result.textContent = test.message;
          result.className = `small ${test.ok ? 'pill ok' : 'pill bad'}`;
        },
      }),
      signedIn ? el('button', {
        class: 'danger',
        text: 'Disconnect',
        onclick: async () => {
          if (await confirmDialog({
            title: 'Forget the Trakt sign-in?',
            body: 'The tokens are deleted from this machine. Your Trakt account is untouched, and you can connect again at any time.',
            confirmLabel: 'Disconnect',
            danger: true,
          })) {
            await app.api.traktSignOut();
            await app.refreshStatus();
            app.render();
          }
        },
      }) : null,
      signedIn ? el('span', { class: 'pill ok', text: `signed in as ${trakt.username}` }) : null,
      result,
    ]),
  ], 'The watchlist, the personal lists, your ratings and your watch history all come from here.');
}

async function traktSignIn(app, current, save, result) {
  if (!await save(true)) return;
  try {
    const start = await app.api.traktSignInStart();
    if (start.error) {
      toast(start.error, 'bad');
      return;
    }

    const dialog = signInDialog({
      title: 'Connect Trakt',
      code: start.code,
      url: start.url,
      note: 'Open the link, sign in to Trakt and enter the code. This window closes itself when it is done.',
    });

    let stop = false;
    dialog.addEventListener('close', () => { stop = true; });
    let interval = (start.interval || 5) * 1000;

    while (!stop) {
      await new Promise((resolve) => setTimeout(resolve, interval));
      if (stop) break;
      const poll = await app.api.traktSignInPoll(start.handle);
      if (poll.state === 'signedin') {
        dialog.close();
        toast(poll.message, 'ok');
        result.textContent = poll.message;
        await app.refreshStatus();
        app.render();
        return;
      }
      if (poll.state === 'failed') {
        dialog.close();
        toast(poll.message, 'bad');
        return;
      }
      if (poll.state === 'slowdown') {
        interval += 1000;
      }
    }
  } catch (error) {
    toast(error.message, 'bad');
  }
}

function signInDialog({ title, code, url, note }) {
  const dialog = el('dialog', {}, [
    el('h2', { text: title }),
    el('p', { class: 'muted', text: note }),
    el('div', { class: 'code', text: code }),
    el('p', { style: 'margin:14px 0 0' }, [
      el('a', { href: url, target: '_blank', rel: 'noreferrer', text: 'Open the sign-in page' }),
    ]),
    el('p', { class: 'mono small muted', text: url, style: 'word-break:break-all' }),
    el('div', { class: 'row', style: 'justify-content:flex-end' }, [
      el('button', { text: 'Cancel', onclick: () => dialog.close() }),
    ]),
  ]);
  document.body.append(dialog);
  dialog.addEventListener('close', () => dialog.remove());
  dialog.showModal();
  return dialog;
}

// ------------------------------------------------------------------ TMDB and OMDb

function metadataCard(app, current, save) {
  const tmdbResult = el('span', { class: 'muted small' });
  const omdbResult = el('span', { class: 'muted small' });
  const region = el('input', { value: current.tmdb.region, style: 'width:90px' });
  region.addEventListener('input', () => { current.tmdb.region = region.value.trim().toUpperCase(); });

  const regionSelect = el('span', {});

  return card('TMDB and OMDb (optional)', [
    el('p', { class: 'muted small', style: 'margin:0 0 10px', text: 'TMDB unlocks the streaming check, which sets aside titles you could already watch on a service you pay for. OMDb unlocks the Rotten Tomatoes score, which decides whether an older movie is worth monitoring. Without them the migrate plan still works: nothing is treated as streaming, and older movies are added unmonitored.' }),
    el('div', { class: 'grid2' }, [
      secretField('TMDB API key', current.tmdb, 'apiKey', 'themoviedb.org, Settings, API'),
      el('label', { class: 'field' }, ['TMDB region (two letters)', region]),
      secretField('OMDb API key', current.omdb, 'apiKey', 'omdbapi.com/apikey.aspx'),
    ]),
    el('div', { class: 'row', style: 'margin-top:12px' }, [
      el('button', {
        text: 'Test TMDB',
        onclick: async () => {
          if (!await save(true)) return;
          const test = await app.api.test('tmdb');
          tmdbResult.textContent = test.message;
          tmdbResult.className = `small ${test.ok ? 'pill ok' : 'pill bad'}`;
        },
      }),
      el('button', {
        text: 'Load the region list',
        onclick: async () => {
          if (!await save(true)) return;
          const result = await app.api.tmdbRegions();
          if (!result.ok) {
            tmdbResult.textContent = result.message;
            tmdbResult.className = 'small pill bad';
            return;
          }

          const select = el('select');
          for (const name of result.regions) {
            select.append(el('option', { value: name.slice(0, 2), text: name }));
          }
          select.value = current.tmdb.region;
          select.addEventListener('change', () => {
            current.tmdb.region = select.value;
            region.value = select.value;
          });
          regionSelect.replaceChildren(el('label', { class: 'field' }, ['Pick a region', select]));
        },
      }),
      tmdbResult,
    ]),
    regionSelect,
    el('div', { class: 'row', style: 'margin-top:10px' }, [
      el('button', {
        text: 'Test OMDb',
        onclick: async () => {
          if (!await save(true)) return;
          const test = await app.api.test('omdb');
          omdbResult.textContent = test.message;
          omdbResult.className = `small ${test.ok ? 'pill ok' : 'pill bad'}`;
        },
      }),
      omdbResult,
    ]),
  ]);
}

// ------------------------------------------------------------------ lists (phase 3)

async function listsCard(current, refreshLists) {
  const migration = current.migration;
  const body = el('div');

  async function draw(force) {
    body.replaceChildren(el('p', { class: 'muted small', text: 'Reading your Trakt lists...' }));
    const lists = await refreshLists(force);

    if (!lists.length) {
      body.replaceChildren(el('p', { class: 'muted' }, [
        'Your Trakt lists could not be read, so the pickers are empty. Connect Trakt above, then ',
        el('a', { href: '#', text: 'try again', onclick: (e) => { e.preventDefault(); draw(true); } }),
        '.',
      ]));
      return;
    }

    const refresh = () => draw(true);

    body.replaceChildren(
      el('h2', { text: 'Read as the source' }),
      el('p', { class: 'muted small', style: 'margin:0 0 8px', text: 'The lists the migrate plan and the tidy-up work through. The watchlist is in the picker as a fixed first entry; the last list chosen acts as the overflow list.' }),
      listPicker({
        lists,
        selected: migration.sourceLists,
        multiple: true,
        refresh,
        onChange: (value) => { migration.sourceLists = value; },
      }),

      el('h2', { text: 'Titles are removed from' }),
      el('p', { class: 'muted small', style: 'margin:0 0 8px', text: 'Once a title has been dealt with it comes off these. The watchlist is always included; add the personal lists you keep in step with it. A list you do not choose here is never touched.' }),
      listPicker({
        lists,
        selected: migration.removeFromLists,
        multiple: true,
        refresh,
        onChange: (value) => { migration.removeFromLists = value; },
      }),

      el('h2', { text: 'Already streaming goes to' }),
      el('p', { class: 'muted small', style: 'margin:0 0 8px', text: 'A service you pay for, and the list its titles are moved to. A name typed in that does not exist on Trakt yet is created as a private list the first time it is used.' }),
      streamingTargets(migration, lists, refresh),
    );
  }

  await draw(false);
  return card('Trakt lists', body, 'Picked from your account, held by list id, so renaming one on Trakt does not break anything here.');
}

function streamingTargets(migration, lists, refresh) {
  const container = el('div');

  function draw() {
    container.replaceChildren();
    for (const target of migration.streamingTargets) {
      const enabled = el('input', { type: 'checkbox', onchange: () => { target.enabled = enabled.checked; } });
      enabled.checked = target.enabled;

      const provider = el('input', {
        value: target.provider,
        placeholder: 'Netflix',
        style: 'width:150px',
        oninput: () => { target.provider = provider.value; },
      });

      container.append(el('div', { class: 'row', style: 'margin-bottom:10px;align-items:flex-start' }, [
        el('label', { class: 'inline' }, [enabled, 'on']),
        provider,
        el('span', { class: 'muted', text: '->' }),
        listPicker({
          lists,
          selected: target.list && (target.list.id || target.list.name) ? [target.list] : [],
          multiple: false,
          allowCreate: true,
          onChange: (value) => { target.list = value || { id: 0, slug: '', name: '' }; },
        }),
        el('button', {
          class: 'small danger',
          text: 'Remove',
          onclick: () => {
            migration.streamingTargets.splice(migration.streamingTargets.indexOf(target), 1);
            draw();
          },
        }),
      ]));
    }

    container.append(el('div', { class: 'row' }, [
      el('button', {
        class: 'small',
        text: 'Add a service',
        onclick: () => {
          migration.streamingTargets.push({ provider: '', list: { id: 0, slug: '', name: '' }, enabled: true });
          draw();
        },
      }),
      refresh ? el('button', { class: 'small', text: 'Refresh the lists', onclick: refresh }) : null,
    ]));
  }

  draw();
  return container;
}

// ------------------------------------------------------------------ rules

function rulesCard(current) {
  const rules = current.migration;
  const languages = el('input', {
    value: (rules.keepOriginalLanguages || []).join(', '),
    placeholder: 'en',
    oninput: () => {
      rules.keepOriginalLanguages = languages.value.split(',').map((s) => s.trim()).filter(Boolean);
    },
  });

  return card('Rules', [
    el('div', { class: 'grid2' }, [
      numberField('Skip anything rated below', rules, 'ratingThreshold', 'also the prune threshold'),
      numberField('Monitor and search from this year on', rules, 'releaseYearCutoff'),
      numberField('Older movies need this Rotten Tomatoes score', rules, 'rottenTomatoesCutoff', 'percent'),
      el('label', { class: 'field' }, ['Keep series in these original languages', languages, el('span', { class: 'muted small', text: 'comma separated, two-letter codes; empty keeps every language' })]),
    ]),
  ], 'What the migrate plan does with each title. These were constants in the old PlexMigrate.');
}

// ------------------------------------------------------------------ exclusions

function exclusionsCard(current) {
  const exclusions = current.exclusions;
  const shows = editableList(exclusions.shows, 'Show title as Plex shows it', (value) => { exclusions.shows = value; });
  const episodes = editableList(exclusions.episodes, 'Show|S01E02', (value) => { exclusions.episodes = value; });

  return card('Never log these', [
    el('h2', { text: 'Shows', style: 'margin-top:0' }),
    el('p', { class: 'muted small', style: 'margin:0 0 8px', text: 'Dropped from every episode report, so they are never pushed to Trakt. Matched on the Plex show title, ignoring case.' }),
    shows,
    el('h2', { text: 'Single episodes' }),
    el('p', { class: 'muted small', style: 'margin:0 0 8px', text: 'Episodes Trakt does not recognise: merged finales, specials, different numbering. One per line as Show|S01E02.' }),
    episodes,
  ], 'This was plexmanager.exclusions.json.');
}

function knownTitlesCard(current) {
  const titles = current.fix.knownTitles;
  const body = el('div');

  function draw() {
    const rows = titles.map((entry) => el('tr', {}, [
      el('td', {}, [el('input', { value: entry.plexTitle, style: 'width:100%', oninput: (e) => { entry.plexTitle = e.target.value; } })]),
      el('td', {}, [el('input', { value: entry.name, style: 'width:100%', oninput: (e) => { entry.name = e.target.value; } })]),
      el('td', {}, [el('input', { value: entry.year, style: 'width:70px', oninput: (e) => { entry.year = e.target.value; } })]),
      el('td', {}, [el('button', {
        class: 'small danger',
        text: 'Remove',
        onclick: () => { titles.splice(titles.indexOf(entry), 1); draw(); },
      })]),
    ]));

    body.replaceChildren(
      el('div', { class: 'tablewrap' }, [el('table', {}, [
        el('thead', {}, [el('tr', {}, [
          el('th', { text: 'Plex title' }),
          el('th', { text: 'What it really is' }),
          el('th', { text: 'Year' }),
          el('th', { text: '' }),
        ])]),
        el('tbody', {}, rows.length ? rows : [el('tr', {}, [el('td', { colspan: 4, class: 'muted', text: 'Nothing here.' })])]),
      ])]),
      el('div', { class: 'row', style: 'margin-top:10px' }, [el('button', {
        class: 'small',
        text: 'Add a title',
        onclick: () => { titles.push({ plexTitle: '', name: '', year: '' }); draw(); },
      })]),
    );
  }

  draw();
  return card('Known title corrections', body, 'For a Plex movie whose own title is unusable. A candidate matching one of these exactly is pre-approved on the Fix screen, whatever its rank. This was a dictionary compiled into PlexFix.');
}

function editableList(values, placeholder, onChange) {
  const area = el('textarea', {
    rows: String(Math.min(12, Math.max(3, values.length + 1))),
    placeholder,
    style: 'width:100%;font-family:Consolas,monospace;font-size:12px',
    oninput: () => onChange(area.value.split('\n').map((s) => s.trim()).filter(Boolean)),
  });
  area.value = values.join('\n');
  return area;
}

// ------------------------------------------------------------------ data folder

function dataCard(app, current) {
  const ui = current.ui;
  return card('This app', [
    el('div', { class: 'grid2' }, [
      el('label', { class: 'field' }, ['Data folder', el('input', { value: app.status?.dataFolder || '', readonly: true, class: 'mono' })]),
      numberField('Loopback port (0 picks a free one)', ui, 'port'),
      checkField('Use the built-in window when WebView2 is available', ui, 'preferWebView'),
    ]),
    el('p', { class: 'muted small', style: 'margin:10px 0 0', text: 'Everything lives in the data folder, outside the exe, so replacing the exe keeps your settings. Put a file called portable.flag next to the exe, or start it with --data <folder>, to keep everything beside it instead.' }),
    el('div', { class: 'row', style: 'margin-top:10px' }, [
      el('button', { class: 'small', text: 'Open the data folder', onclick: () => app.api.reveal(app.status?.dataFolder) }),
      el('button', { class: 'small', text: 'Open the Reports folder', onclick: () => app.api.reveal(app.status?.reportsFolder) }),
      el('button', { class: 'small', text: 'Open the Backups folder', onclick: () => app.api.reveal(app.status?.backupsFolder) }),
    ]),
  ]);
}

// ------------------------------------------------------------------ small field helpers

function field(label, target, key, hint) {
  const input = el('input', { value: target[key] ?? '', oninput: () => { target[key] = input.value.trim(); } });
  return el('label', { class: 'field' }, [label, input, hint ? el('span', { class: 'muted small', text: hint }) : null]);
}

function numberField(label, target, key, hint) {
  const input = el('input', {
    type: 'number',
    value: String(target[key] ?? 0),
    oninput: () => { target[key] = Number(input.value) || 0; },
  });
  return el('label', { class: 'field' }, [label, input, hint ? el('span', { class: 'muted small', text: hint }) : null]);
}

function checkField(label, target, key) {
  const input = el('input', { type: 'checkbox', onchange: () => { target[key] = input.checked; } });
  input.checked = !!target[key];
  return el('label', { class: 'inline', style: 'align-self:end;padding-bottom:6px' }, [input, label]);
}

/** A secret is shown as a hint such as ****1234; leaving it alone keeps the stored value. */
function secretField(label, target, key, hint) {
  const input = el('input', {
    type: 'password',
    value: target[key] ?? '',
    placeholder: 'not set',
    oninput: () => { target[key] = input.value.trim(); },
  });
  return el('label', { class: 'field' }, [
    label,
    input,
    el('span', { class: 'muted small', text: hint ? `${hint}. Leave it as it is to keep the saved one.` : 'Leave it as it is to keep the saved one.' }),
  ]);
}
