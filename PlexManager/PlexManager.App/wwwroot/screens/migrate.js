import { el, card, step, reviewTable, summaryList, when, confirmDialog, toast } from '../ui.js';

/**
 * The migrate workflow as numbered steps: plan, review, add, verify, then clear Trakt.
 * The Action cell is editable here, which is how a title is taken out of the process or sent for removal.
 */
export async function migrate(app) {
  const [planRuns, verifyRuns] = await Promise.all([app.api.runs('migrate'), app.api.runs('migrate-verify')]);
  const latest = planRuns[0] ? await app.api.run(planRuns[0].id) : null;
  const lastVerify = verifyRuns[0] || null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Migrate to Radarr and Sonarr' }),
    el('p', { class: 'lede', text: 'Works through the chosen Trakt lists and decides what to hand to Radarr or Sonarr, what you already have, and what should simply leave the lists. The plan is read-only: nothing is added or removed until the later steps.' }),
  );

  const lists = (app.status?.lists?.source || []);
  page.append(card('Reading from', [
    el('div', { class: 'chips' }, lists.length
      ? [...lists].map((name) => el('span', { class: 'pill', text: name }))
      : [el('span', { class: 'muted small', text: 'no lists chosen yet' })]),
    el('p', { class: 'muted small', style: 'margin:10px 0 0' }, [
      'Titles that are dealt with are removed from: ',
      [...(app.status?.lists?.removeFrom || [])].join(', ') || 'the watchlist',
      '. Change both on the ',
      el('a', { href: '#/settings', text: 'Settings' }),
      ' screen.',
    ]),
  ]));

  let table = null;
  let dirty = false;

  const saveButton = el('button', {
    text: 'Save the plan',
    disabled: true,
    onclick: async () => {
      try {
        await app.api.saveRows(latest.id, table.rows);
        dirty = false;
        saveButton.disabled = true;
        toast('Plan saved. The later steps read it back from the same file.', 'ok');
      } catch (error) {
        toast(error.message, 'bad');
      }
    },
  });

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'Work out the plan', 'Reads the newest Trakt backup, Plex, Radarr and Sonarr, then applies the rules from Settings: low ratings are skipped, anything already owned is skipped, series in another original language are dropped, titles already streaming are set aside, and the rest are added (monitored when they are recent enough or scored well enough). Read-only.', [
    el('button', { class: 'primary', text: 'Build the plan', onclick: () => app.runJob('migrate-plan').then(() => app.render()) }),
    latest ? el('span', { class: 'muted small', text: `Last built ${when(latest.finishedAt)}` }) : null,
  ]));

  steps.append(step(2, 'Review it', 'The Action cell is yours to change: leave it as it is, set it to <strong>add</strong> to force a title through, or <strong>blank it</strong> to have the title removed from the Trakt lists in step 5. Monitor and SearchOnAdd can be ticked or unticked per row.', [saveButton]));

  steps.append(step(3, 'Add to Radarr and Sonarr', 'Looks each "add" row up by its tmdb or tvdb id and posts it with the quality profile and root folder from Settings. Trakt is not touched.', [
    el('button', { text: 'Dry run', disabled: !latest, onclick: () => app.runJob('migrate-apply', { apply: false }) }),
    el('button', {
      class: 'primary',
      text: 'Add them',
      disabled: !latest,
      onclick: async () => {
        if (dirty && !await confirmDialog({
          title: 'Unsaved plan',
          body: 'The plan on screen has not been saved, so this step would use the old one. Carry on anyway?',
          confirmLabel: 'Carry on',
        })) {
          return;
        }

        const adds = table ? table.rows.filter((r) => r.Action === 'add') : [];
        if (await confirmDialog({
          title: 'Add these to Radarr and Sonarr?',
          body: `<strong>${adds.filter((r) => r.Type === 'movie').length} movies</strong> to Radarr and <strong>${adds.filter((r) => r.Type === 'show').length} series</strong> to Sonarr. Nothing on Trakt changes.`,
          confirmLabel: 'Add them',
        })) {
          app.runJob('migrate-apply', { apply: true });
        }
      },
    }),
  ]));

  steps.append(step(4, 'Check they arrived', 'Re-reads Radarr and Sonarr and confirms every title the plan said should be there is. Writes a verified list and a missing list. Read-only, and the removal steps lean on it.', [
    el('button', { text: 'Verify', disabled: !latest, onclick: () => app.runJob('migrate-verify').then(() => app.render()) }),
    lastVerify ? el('span', { class: 'muted small', text: `Last verified ${when(lastVerify.finishedAt)}: ${(lastVerify.summary || []).find((s) => s.startsWith('Missing:')) || ''}` }) : null,
  ]));

  steps.append(step(5, 'Clear the Trakt lists', 'Three separate removals, each a dry run first. Verified: the titles step 4 confirmed are in Radarr or Sonarr. Reviewed: the rows whose Action you blanked, plus the ones the plan marked "remove:". Leftovers: the ones never meant for Radarr or Sonarr (already in Plex, already watched, no usable id) plus whatever step 4 found missing.', [
    removal(app, 'trakt-remove-verified', 'Verified', !lastVerify),
    removal(app, 'trakt-remove-lists', 'Reviewed', !latest),
    removal(app, 'trakt-remove-leftovers', 'Leftovers', !latest),
  ]));

  if (latest) {
    table = reviewTable(latest, {
      editable: ['Action', 'Monitor', 'SearchOnAdd'],
      hide: ['PartlyWatchedOnTrakt', 'TraktId'],
      onChange: () => { dirty = true; saveButton.disabled = false; },
      link: { IMDb: 'IMDb' },
    });

    const counts = {};
    for (const row of latest.rows) {
      const key = (row.Action || '(blank)').split(':')[0];
      counts[key] = (counts[key] || 0) + 1;
    }

    page.append(card(`The plan (${latest.rows.length} titles)`, [
      summaryList(latest),
      el('div', { class: 'chips', style: 'margin:8px 0 12px' }, Object.entries(counts)
        .sort((a, b) => b[1] - a[1])
        .map(([action, count]) => el('span', { class: 'pill', text: `${action}: ${count}` }))),
      table.node,
      el('div', { class: 'row', style: 'margin-top:10px' }, [
        latest.csvPath ? el('a', { href: app.api.fileUrl(latest.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the CSV' })]) : null,
        latest.htmlPath ? el('a', { href: app.api.fileUrl(latest.htmlPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'Open the series page' })]) : null,
      ]),
    ]));
  } else {
    page.append(card('No plan yet', el('p', { class: 'muted', text: 'Build the plan in step 1. It needs a Trakt backup, and Radarr and Sonarr must be reachable with a quality profile and root folder chosen in Settings.' })));
  }

  return page;
}

function removal(app, operation, label, disabled) {
  const lists = [...(app.status?.lists?.removeFrom || ['the watchlist'])].join(', ');
  return el('span', { class: 'row tight', style: 'border:1px solid var(--line);border-radius:6px;padding:4px 6px' }, [
    el('span', { class: 'small muted', text: label }),
    el('button', { class: 'small', text: 'Dry run', disabled, onclick: () => app.runJob(operation, { apply: false }) }),
    el('button', {
      class: 'small danger',
      text: 'Remove',
      disabled,
      onclick: async () => {
        if (await confirmDialog({
          title: `Remove the ${label.toLowerCase()} titles from Trakt?`,
          body: `They are removed from: <strong>${lists}</strong>.<br><br>A fresh Trakt backup is taken first. Nothing is added anywhere.`,
          confirmLabel: 'Remove them',
          danger: true,
        })) {
          app.runJob(operation, { apply: true });
        }
      },
    }),
  ]);
}
