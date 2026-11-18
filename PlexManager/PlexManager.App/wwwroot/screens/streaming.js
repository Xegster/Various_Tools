import { el, card, step, reviewTable, when, confirmDialog } from '../ui.js';

/**
 * Streaming lists: the titles the plan set aside because they are already on a subscription service, moved onto
 * their own Trakt list. The destination lists are chosen on the Settings screen.
 */
export async function streaming(app) {
  const [planRuns, moveRuns] = await Promise.all([app.api.runs('migrate'), app.api.runs('move-streaming')]);
  const plan = planRuns[0] ? await app.api.run(planRuns[0].id) : null;
  const lastMove = moveRuns[0] || null;
  const page = el('div');

  page.append(
    el('h1', { text: 'Streaming lists' }),
    el('p', { class: 'lede', text: 'When the plan finds a title already streaming on one of your services, there is no point downloading it. This moves those titles onto the list you picked for that service, checks they arrived, and only then takes them off the source lists.' }),
  );

  const targets = [...(app.status?.lists?.streaming || [])];
  page.append(card('Where they go', [
    el('div', { class: 'chips' }, targets.length
      ? targets.map((line) => el('span', { class: 'pill', text: line }))
      : [el('span', { class: 'muted small', text: 'no streaming services set up' })]),
    el('p', { class: 'muted small', style: 'margin:10px 0 0' }, [
      'Add or change a service and its list on the ',
      el('a', { href: '#/settings', text: 'Settings' }),
      ' screen. A list that does not exist on Trakt yet is created as a private list the first time it is used.',
    ]),
  ]));

  const byProvider = {};
  if (plan) {
    for (const row of plan.rows) {
      const match = /^skip: on (.+)$/.exec(row.Action || '');
      if (match) {
        (byProvider[match[1]] ||= []).push(row);
      }
    }
  }

  const total = Object.values(byProvider).reduce((sum, rows) => sum + rows.length, 0);

  const steps = el('div', { class: 'steps' });
  page.append(card('Steps', steps));

  steps.append(step(1, 'Build a plan first', 'The titles come from the newest migrate plan, so build one if it is out of date.', [
    el('a', { href: '#/migrate' }, [el('button', { text: 'Go to Migrate' })]),
    plan ? el('span', { class: 'muted small', text: `Plan built ${when(plan.finishedAt)}; ${total} titles are already streaming` }) : el('span', { class: 'muted small', text: 'no plan yet' }),
  ]));

  steps.append(step(2, 'Move them', 'Each title is added to its service’s list and checked; anything that cannot be confirmed is left on the source lists untouched. A fresh Trakt backup is taken first.', [
    el('button', { text: 'Dry run', disabled: !plan || !total, onclick: () => app.runJob('move-streaming', { apply: false }) }),
    el('button', {
      class: 'primary',
      text: 'Move them',
      disabled: !plan || !total,
      onclick: async () => {
        if (await confirmDialog({
          title: 'Move these titles?',
          body: `${total} titles are added to their service lists and then removed from: <strong>${[...(app.status?.lists?.removeFrom || ['the watchlist'])].join(', ')}</strong>.<br><br>Nothing is removed until it is confirmed on the new list.`,
          confirmLabel: 'Move them',
        })) {
          app.runJob('move-streaming', { apply: true });
        }
      },
    }),
    lastMove ? el('span', { class: 'muted small', text: `Last moved ${when(lastMove.finishedAt)}` }) : null,
  ]));

  if (!plan) {
    page.append(card('No plan yet', el('p', { class: 'muted', text: 'Build a migrate plan first.' })));
    return page;
  }

  for (const [provider, rows] of Object.entries(byProvider)) {
    page.append(card(`${provider} (${rows.length})`, reviewTable(
      { columns: ['Type', 'Title', 'Year', 'Streaming', 'IMDb'], rows, editableColumns: [] },
      { editable: [], link: { IMDb: 'IMDb' } },
    ).node));
  }

  if (!total) {
    page.append(card('Nothing to move', el('p', { class: 'muted', text: 'The newest plan found nothing already streaming. That can also mean the TMDB key is missing, or no streaming services are switched on in Settings.' })));
  }

  return page;
}
