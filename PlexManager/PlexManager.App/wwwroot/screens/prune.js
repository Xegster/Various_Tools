import { el, card, step, reviewTable, summaryList, when, confirmDialog, toast } from '../ui.js';

/**
 * Prune low-rated movies: list the Plex movies rated below the threshold on Trakt, mark the keepers, then delete
 * the rest from Plex (file included) and from Radarr with an import exclusion.
 */
export async function prune(app) {
  const runs = await app.api.runs('prune');
  const latest = runs[0] ? await app.api.run(runs[0].id) : null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Prune low-rated movies' }),
    el('p', { class: 'lede', text: 'Movies you rated low on Trakt but are still taking up space in Plex. Nothing is deleted until the list has been reviewed here and the deletion typed out.' }),
  );

  let table = null;
  let dirty = false;

  const threshold = el('input', {
    type: 'number',
    min: '1',
    max: '10',
    value: String(app.status?.ratingThreshold ?? 7),
    style: 'width:70px',
  });

  const saveButton = el('button', {
    text: 'Save the Keep marks',
    disabled: true,
    onclick: async () => {
      try {
        await app.api.saveRows(latest.id, table.rows);
        dirty = false;
        saveButton.disabled = true;
        toast('Saved. The delete step will skip everything marked Keep.', 'ok');
      } catch (error) {
        toast(error.message, 'bad');
      }
    },
  });

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'List the candidates', 'Reads the newest Trakt backup for your ratings and the Plex library for what you still have. Read-only.', [
    el('label', { class: 'inline' }, ['Rating below', threshold]),
    el('button', {
      class: 'primary',
      text: 'List them',
      onclick: () => app.runJob('prune-candidates', { threshold: Number(threshold.value) || undefined }).then(() => app.render()),
    }),
    latest ? el('span', { class: 'muted small', text: `Last run ${when(latest.finishedAt)}` }) : null,
  ]));

  steps.append(step(2, 'Mark what to keep', 'Tick Keep on anything you want to hold on to, then save. Everything left unticked is what step 3 deletes.', [saveButton]));

  steps.append(step(3, 'Delete the rest', 'Deletes each one from Plex with its file, then removes it from Radarr with an import exclusion so a list sync does not fetch it again. Each row is checked against Plex first and skipped if the file has changed.', [
    el('button', { text: 'Dry run', disabled: !latest, onclick: () => app.runJob('prune-apply', { apply: false }) }),
    el('button', {
      class: 'danger',
      text: 'Delete them',
      disabled: !latest,
      onclick: async () => {
        if (dirty && !await confirmDialog({
          title: 'Unsaved Keep marks',
          body: 'The Keep marks on screen have not been saved, so the delete step would use the old ones. Carry on anyway?',
          confirmLabel: 'Carry on',
        })) {
          return;
        }

        const doomed = table ? table.rows.filter((r) => (r.Keep || '').toLowerCase() !== 'yes') : [];
        const gb = doomed.reduce((total, r) => total + (Number(r.SizeMB) || 0), 0) / 1024;
        if (await confirmDialog({
          title: 'Delete these movies for good?',
          body: `<strong>${doomed.length} movies</strong> and their files (about ${gb.toFixed(1)} GB) are deleted from Plex and removed from Radarr. This cannot be undone.<br><br>Type <strong>delete</strong> to confirm.`,
          confirmLabel: 'Delete them',
          typed: 'delete',
          danger: true,
        })) {
          app.runJob('prune-apply', { apply: true });
        }
      },
    }),
  ]));

  if (latest) {
    table = reviewTable(latest, {
      editable: ['Keep'],
      hide: ['File', 'RatingKey'],
      onChange: () => { dirty = true; saveButton.disabled = false; },
      link: { IMDb: 'IMDb' },
    });

    const totalGb = latest.rows.reduce((total, r) => total + (Number(r.SizeMB) || 0), 0) / 1024;
    page.append(card(`Candidates (${latest.rows.length}, ${totalGb.toFixed(1)} GB)`, [
      summaryList(latest),
      table.node,
      el('div', { class: 'row', style: 'margin-top:10px' }, [
        latest.csvPath ? el('a', { href: app.api.fileUrl(latest.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the CSV' })]) : null,
      ]),
    ]));
  } else {
    page.append(card('Nothing listed yet', el('p', { class: 'muted', text: 'Run step 1. It needs a Trakt backup to read your ratings from.' })));
  }

  return page;
}
