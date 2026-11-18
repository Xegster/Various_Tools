import { el, card, when, summaryList } from '../ui.js';

export async function backup(app) {
  const runs = await app.api.runs('backup');
  const page = el('div');

  page.append(
    el('h1', { text: 'Backup' }),
    el('p', { class: 'lede', text: 'Pulls the whole Trakt account into a timestamped folder: watched state, history, ratings, the watchlist, every personal list, collections and favourites, plus a flat CSV of ids. Read-only as far as Trakt is concerned, and every operation that writes to Trakt runs one of these first.' }),
  );

  page.append(card(null, el('div', { class: 'row' }, [
    el('button', { class: 'primary', text: 'Back up now', onclick: () => app.runJob('backup').then(() => app.render()) }),
    el('button', {
      text: 'Open the Backups folder',
      onclick: () => app.api.reveal(app.status?.backupsFolder),
    }),
  ])));

  page.append(card(`Previous backups (${runs.length})`, runs.length
    ? el('div', { class: 'tablewrap' }, [el('table', {}, [
        el('thead', {}, [el('tr', {}, [
          el('th', { text: 'When' }),
          el('th', { text: 'What it held' }),
          el('th', { text: '' }),
        ])]),
        el('tbody', {}, runs.map((run) => el('tr', {}, [
          el('td', { class: 'nowrap', text: when(run.finishedAt) }),
          el('td', {}, [summaryList(run)]),
          el('td', {}, [el('button', {
            class: 'small',
            text: 'Open folder',
            onclick: () => {
              const folder = (run.summary || []).find((s) => s.startsWith('Folder: '));
              if (folder) app.api.reveal(folder.slice('Folder: '.length));
            },
          })]),
        ]))),
      ])])
    : el('p', { class: 'muted', text: 'No backup has been taken through this app yet. Backups taken by the old TraktBackup tool are imported by Settings and still readable; they just do not appear in this list.' })));

  return page;
}
