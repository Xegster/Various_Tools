import { el, card, step, reviewTable, summaryList, when, confirmDialog } from '../ui.js';

/**
 * Tidy the Trakt lists: drop what has been watched and clear duplicates. The last list in the selection acts as
 * the overflow list, so a title on two lists is kept there and removed from the others.
 */
export async function clean(app) {
  const runs = await app.api.runs('clean');
  const latest = runs.find((r) => !r.applied) ? await app.api.run(runs.find((r) => !r.applied).id) : runs[0] ? await app.api.run(runs[0].id) : null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Tidy the Trakt lists' }),
    el('p', { class: 'lede', text: 'A movie with any play comes off the lists. A show comes off only when Trakt says it has ended or been cancelled and every aired episode is watched, so a series you are still working through stays. A title sitting on two of the lists is kept on the overflow list and removed from the rest.' }),
  );

  const lists = [...(app.status?.lists?.source || [])];
  page.append(card('Lists it works on', [
    el('div', { class: 'chips' }, lists.length
      ? lists.map((name) => el('span', { class: 'pill', text: name }))
      : [el('span', { class: 'muted small', text: 'the watchlist only' })]),
    el('p', { class: 'muted small', style: 'margin:10px 0 0' }, [
      'These are the source lists from ',
      el('a', { href: '#/settings', text: 'Settings' }),
      '. The last one is treated as the overflow list.',
    ]),
  ]));

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'Work out what to remove', 'Reads the lists and your watched state from Trakt live, and checks each started show’s progress. Changes nothing.', [
    el('button', { class: 'primary', text: 'Dry run', onclick: () => app.runJob('clean', { apply: false }).then(() => app.render()) }),
    latest ? el('span', { class: 'muted small', text: `Last run ${when(latest.finishedAt)}` }) : null,
  ]));

  steps.append(step(2, 'Remove them', 'A fresh Trakt backup is taken first, then the removals are sent and the lists are read back into the Current folder.', [
    el('button', {
      class: 'danger',
      text: 'Remove them',
      disabled: !latest || !latest.rows.length,
      onclick: async () => {
        if (await confirmDialog({
          title: 'Remove these from Trakt?',
          body: `<strong>${latest.rows.length} titles</strong> are removed from: ${lists.join(', ') || 'the watchlist'}.<br><br>A fresh backup is taken first. Your watch history and ratings are not touched.`,
          confirmLabel: 'Remove them',
          danger: true,
        })) {
          app.runJob('clean', { apply: true }).then(() => app.render());
        }
      },
    }),
  ]));

  if (latest) {
    page.append(card(`What would go (${latest.rows.length})`, [
      summaryList(latest),
      reviewTable(latest, { editable: [], hide: ['TraktId'] }).node,
      latest.csvPath ? el('div', { class: 'row', style: 'margin-top:10px' }, [
        el('a', { href: app.api.fileUrl(latest.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the CSV' })]),
      ]) : null,
    ]));
  } else {
    page.append(card('Nothing worked out yet', el('p', { class: 'muted', text: 'Run the dry run to see what would be removed.' })));
  }

  return page;
}
