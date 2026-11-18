import { el, card, step, reviewTable, summaryList, when, confirmDialog, toast } from '../ui.js';

/**
 * Plex vs Trakt: run a scan, look at the gaps, then push what should be on Trakt. The scan is read-only and the
 * push is a dry run until it is confirmed.
 */
export async function scan(app) {
  const runs = await app.api.runs('scan');
  const latest = runs[0] ? await app.api.run(runs[0].id) : null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Plex vs Trakt' }),
    el('p', { class: 'lede', text: 'Compares what Plex says you have watched with the newest Trakt backup, then pushes the difference to your Trakt history. Episodes and movies are pushed separately, and anything on the exclusion list is left out of both.' }),
  );

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'Scan', 'Reads Plex and the newest Trakt backup. Changes nothing.', [
    el('button', { class: 'primary', text: 'Run the scan', onclick: () => app.runJob('scan').then(() => app.render()) }),
    latest ? el('span', { class: 'muted small', text: `Last run ${when(latest.finishedAt)}` }) : null,
  ]));

  steps.append(step(2, 'Push the movies', 'Adds one play per movie, dated with its last Plex view. Movies already in your Trakt watched list are skipped, so running it twice does not duplicate plays.', [
    el('button', { text: 'Dry run', disabled: !latest, onclick: () => app.runJob('push-movies', { apply: false }) }),
    el('button', {
      class: 'primary',
      text: 'Push for real',
      disabled: !latest,
      onclick: async () => {
        if (await confirmDialog({
          title: 'Add these plays to Trakt?',
          body: 'A fresh Trakt backup is taken first. Movies already marked watched on Trakt are skipped.',
          confirmLabel: 'Push the movies',
        })) {
          app.runJob('push-movies', { apply: true });
        }
      },
    }),
  ]));

  steps.append(step(3, 'Push the episodes', 'The same, by show, season and episode. Shows are matched by tvdb id first, then tmdb, then imdb.', [
    el('button', { text: 'Dry run', disabled: !latest, onclick: () => app.runJob('push-episodes', { apply: false }) }),
    el('button', {
      class: 'primary',
      text: 'Push for real',
      disabled: !latest,
      onclick: async () => {
        if (await confirmDialog({
          title: 'Add these episode plays to Trakt?',
          body: 'A fresh Trakt backup is taken first. Episodes already in your Trakt history are skipped.',
          confirmLabel: 'Push the episodes',
        })) {
          app.runJob('push-episodes', { apply: true });
        }
      },
    }),
  ]));

  if (!latest) {
    page.append(card('No scan yet', el('p', { class: 'muted', text: 'Run the scan to see the gaps. It needs a Trakt backup to compare against, so take one on the Backup screen first if there is none.' })));
    return page;
  }

  page.append(card(`Movies Plex has watched and Trakt has not (${latest.rows.length})`, [
    summaryList(latest),
    reviewTable(latest, { editable: [], hide: ['Ids'] }).node,
    el('div', { class: 'row', style: 'margin-top:10px' }, [
      latest.csvPath ? el('a', { href: app.api.fileUrl(latest.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the CSV' })]) : null,
      el('button', { class: 'small', text: 'Open the report folder', onclick: () => reveal(app, latest) }),
    ]),
  ]));

  page.append(card('The other three reports', el('div', { class: 'row' }, [
    el('span', { class: 'muted small', text: 'The scan also writes the missing episodes, the shows Trakt has never heard of, and the Plex movies with no usable id.' }),
    el('span', { class: 'spacer' }),
    el('button', { class: 'small', text: 'Open the folder', onclick: () => reveal(app, latest) }),
  ])));

  return page;
}

function reveal(app, run) {
  const folder = run.csvPath ? run.csvPath.replace(/\\[^\\]+$/, '') : app.status?.reportsFolder;
  app.api.reveal(folder).catch((error) => toast(error.message, 'bad'));
}
