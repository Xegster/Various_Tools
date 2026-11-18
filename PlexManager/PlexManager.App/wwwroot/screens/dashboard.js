import { el, card, when } from '../ui.js';

export async function dashboard(app) {
  const status = app.status || await app.refreshStatus();
  const page = el('div');

  page.append(
    el('h1', { text: 'Dashboard' }),
    el('p', { class: 'lede', text: 'Where everything stands, and the usual next step. Nothing on this screen changes anything by itself.' }),
  );

  if (!status) {
    return page;
  }

  if (!status.setupComplete) {
    page.append(card('Set up first', [
      el('p', { class: 'muted', text: 'The services have not been confirmed yet. The Settings screen walks through each one with a Test connection button, and can import the old console tools’ files in one click.' }),
      el('a', { href: '#/settings' }, [el('button', { class: 'primary', text: 'Open Settings' })]),
    ]));
  }

  page.append(card('Services', el('div', { class: 'chips' }, status.services.map((service) => el('span', {
    class: `pill ${service.connected ? 'ok' : 'bad'}`,
  }, [
    el('span', { class: 'dot' }),
    `${service.name}: ${service.connected ? (service.detail || service.address) : 'not connected'}`,
  ])))));

  page.append(card('Lists in use', [
    row('Read as the source', status.lists.source),
    row('Titles are removed from', status.lists.removeFrom),
    row('Already streaming goes to', status.lists.streaming),
    el('p', { class: 'muted small', style: 'margin:10px 0 0' },
      ['Change these on the Settings screen; they are held by Trakt list id, so renaming a list on Trakt is safe.']),
  ]));

  page.append(card('Trakt backup', [
    el('div', { class: 'row' }, [
      el('span', { class: 'muted', text: status.lastBackup ? `Last backup ${when(status.lastBackup.at)}` : 'No backup yet.' }),
      el('span', { class: 'spacer' }),
      el('button', { text: 'Back up now', onclick: () => app.runJob('backup') }),
      el('a', { href: '#/backup' }, [el('button', { text: 'Backup screen' })]),
    ]),
    status.lastBackup ? el('div', { class: 'mono muted small', text: status.lastBackup.folder }) : null,
  ]));

  const runs = status.latestRuns || [];
  page.append(card('Most recent of each kind', runs.length
    ? el('div', { class: 'tablewrap' }, [el('table', {}, [
        el('thead', {}, [el('tr', {}, [el('th', { text: 'What' }), el('th', { text: 'When' }), el('th', { text: 'Result' })])]),
        el('tbody', {}, runs.map((run) => el('tr', {}, [
          el('td', { text: run.title }),
          el('td', { class: 'nowrap', text: when(run.finishedAt) }),
          el('td', { class: 'muted small', text: (run.summary || []).slice(1, 4).join(' | ') }),
        ]))),
      ])])
    : el('p', { class: 'muted', text: 'Nothing has been run yet.' })));

  page.append(card('The usual order', el('div', { class: 'steps' }, [
    line(1, 'Back up Trakt', 'Everything that writes to Trakt takes a fresh backup first, but one by hand costs nothing.', '#/backup'),
    line(2, 'Compare Plex with Trakt', 'Find what Plex has watched that Trakt has not, and push it.', '#/scan'),
    line(3, 'Fix unmatched movies', 'Plex items with no metadata match cannot be compared at all.', '#/fix'),
    line(4, 'Migrate', 'Work out what to hand to Radarr and Sonarr, add it, check it arrived, then clear the Trakt lists.', '#/migrate'),
    line(5, 'Prune and tidy', 'Delete low-rated movies and clear watched titles off the lists.', '#/prune'),
  ])));

  return page;
}

function row(title, values) {
  return el('div', { class: 'row', style: 'margin-bottom:6px' }, [
    el('span', { class: 'muted small', style: 'min-width:170px', text: title }),
    el('span', { class: 'chips' }, ([...(values || [])].length
      ? [...values].map((value) => el('span', { class: 'pill', text: value }))
      : [el('span', { class: 'muted small', text: 'none chosen' })])),
  ]);
}

function line(number, title, description, href) {
  return el('div', { class: 'step' }, [
    el('div', { class: 'n', text: String(number) }),
    el('div', { class: 'body' }, [
      el('h3', {}, [el('a', { href, style: 'color:inherit;text-decoration:none', text: title })]),
      el('div', { class: 'muted small', text: description }),
    ]),
  ]);
}
