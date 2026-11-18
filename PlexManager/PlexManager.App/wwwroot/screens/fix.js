import { el, card, step, reviewTable, summaryList, when, confirmDialog, toast } from '../ui.js';

/**
 * Fix unmatched Plex movies: ask Plex for candidates, approve one per movie, then apply.
 * Approving is done right here; the edit is written back to the same CSV the apply step reads.
 */
export async function fix(app) {
  const runs = await app.api.runs('fix');
  const latest = runs[0] ? await app.api.run(runs[0].id) : null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Fix unmatched movies' }),
    el('p', { class: 'lede', text: 'A Plex movie with no metadata match has no imdb, tmdb or tvdb id, so it cannot be compared with Trakt or handed to Radarr. This asks Plex for its match candidates, you approve one per movie, and Plex is then told to use it.' }),
  );

  let table = null;
  let dirty = false;

  const saveButton = el('button', {
    text: 'Save the approvals',
    disabled: true,
    onclick: async () => {
      try {
        await app.api.saveRows(latest.id, table.rows);
        dirty = false;
        saveButton.disabled = true;
        toast('Approvals saved to the candidates CSV.', 'ok');
      } catch (error) {
        toast(error.message, 'bad');
      }
    },
  });

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'Find the candidates', 'Reads the Plex movie library and asks Plex to search for each unmatched title. Read-only.', [
    el('button', { class: 'primary', text: 'Find candidates', onclick: () => app.runJob('fix-candidates').then(() => app.render()) }),
    latest ? el('span', { class: 'muted small', text: `Last run ${when(latest.finishedAt)}` }) : null,
  ]));

  steps.append(step(2, 'Approve one per movie', 'Tick the right candidate in the table below, then save. More than one approved for the same movie is refused.', [saveButton]));

  steps.append(step(3, 'Tell Plex', 'Matches each approved movie in Plex. Plex then refreshes its metadata in the background, so run a scan again a minute later.', [
    el('button', { text: 'Dry run', disabled: !latest, onclick: () => app.runJob('fix-apply', { apply: false }) }),
    el('button', {
      class: 'primary',
      text: 'Match in Plex',
      disabled: !latest,
      onclick: async () => {
        if (dirty && !await confirmDialog({
          title: 'Unsaved approvals',
          body: 'The approvals on screen have not been saved, so the apply step would use the old ones. Carry on anyway?',
          confirmLabel: 'Carry on',
        })) {
          return;
        }

        if (await confirmDialog({
          title: 'Match these movies in Plex?',
          body: 'This changes the metadata match on each approved movie. It does not touch any file.',
          confirmLabel: 'Match them',
        })) {
          app.runJob('fix-apply', { apply: true });
        }
      },
    }),
  ]));

  if (latest) {
    table = reviewTable(latest, {
      editable: ['Approve'],
      hide: ['File'],
      onChange: () => { dirty = true; saveButton.disabled = false; },
    });

    const movies = new Set(latest.rows.map((r) => r.RatingKey)).size;
    page.append(card(`Candidates (${latest.rows.length} rows for ${movies} movies)`, [
      summaryList(latest),
      table.node,
      el('div', { class: 'row', style: 'margin-top:10px' }, [
        latest.csvPath ? el('a', { href: app.api.fileUrl(latest.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the CSV' })]) : null,
      ]),
    ]));
  } else {
    page.append(card('Nothing found yet', el('p', { class: 'muted', text: 'Run step 1 to look for candidates.' })));
  }

  page.append(deleteCard(app));
  return page;
}

/** The one-off "this file is junk, delete it" tool PlexFix had. Deliberately awkward: it needs a typed confirmation. */
function deleteCard(app) {
  const fragment = el('input', { placeholder: 'part of the file path', style: 'min-width:320px' });

  return card('Delete one movie by its file path', [
    el('p', { class: 'muted small', style: 'margin:0 0 10px', text: 'For a movie Plex will never match because the file itself is wrong. It only works when exactly one movie in the library has that text in its path, and it deletes the file too, so Plex must have "Allow media deletion" turned on.' }),
    el('div', { class: 'row' }, [
      fragment,
      el('button', {
        text: 'Dry run',
        onclick: () => {
          if (!fragment.value.trim()) return toast('Type part of the file path first.', 'bad');
          return app.runJob('fix-delete', { apply: false, input: fragment.value.trim() });
        },
      }),
      el('button', {
        class: 'danger',
        text: 'Delete it',
        onclick: async () => {
          const value = fragment.value.trim();
          if (!value) return toast('Type part of the file path first.', 'bad');
          if (await confirmDialog({
            title: 'Delete the movie and its file?',
            body: `Every Plex movie whose path contains <span class="mono">${escapeHtml(value)}</span> is looked up; if there is exactly one, it and its file are deleted. This cannot be undone.<br><br>Type <strong>delete</strong> to confirm.`,
            confirmLabel: 'Delete it',
            typed: 'delete',
            danger: true,
          })) {
            app.runJob('fix-delete', { apply: true, input: value });
          }
          return undefined;
        },
      }),
    ]),
  ]);
}

function escapeHtml(value) {
  return value.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
