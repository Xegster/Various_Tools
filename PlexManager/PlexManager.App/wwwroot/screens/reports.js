import { el, card, reviewTable, summaryList, when, toast } from '../ui.js';

/** Every run, newest first, with its rows and its CSV. This is the history the old Reports folder was. */
export async function reports(app) {
  const all = await app.api.runs();
  const page = el('div');

  page.append(
    el('h1', { text: 'Reports' }),
    el('p', { class: 'lede', text: 'Every run this app has made, newest first. The CSVs are still written to the Reports folder in the same shape as before, so anything that reads them keeps working.' }),
  );

  const kinds = [...new Set(all.map((r) => r.kind))].sort();
  let filter = '';
  const list = el('div');

  function draw() {
    const runs = all.filter((r) => !filter || r.kind === filter);
    list.replaceChildren(runs.length
      ? el('div', { class: 'tablewrap' }, [el('table', {}, [
          el('thead', {}, [el('tr', {}, [
            el('th', { text: 'When' }),
            el('th', { text: 'What' }),
            el('th', { text: 'Rows' }),
            el('th', { text: 'Result' }),
            el('th', { text: '' }),
          ])]),
          el('tbody', {}, runs.map((run) => el('tr', {}, [
            el('td', { class: 'nowrap', text: when(run.finishedAt) }),
            el('td', {}, [
              el('div', { text: run.title }),
              el('div', { class: 'muted small', text: run.applied ? 'changed something' : 'read-only or dry run' }),
            ]),
            el('td', { class: 'num', text: String(run.rows) }),
            el('td', { class: 'muted small', text: (run.summary || []).slice(1).join(' | ') }),
            el('td', { class: 'nowrap' }, [
              el('button', { class: 'small', text: 'Open', onclick: () => show(app, run.id) }),
              run.csvPath ? el('a', { href: app.api.fileUrl(run.csvPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'CSV' })]) : null,
              run.htmlPath ? el('a', { href: app.api.fileUrl(run.htmlPath), target: '_blank', rel: 'noreferrer' }, [el('button', { class: 'small', text: 'HTML' })]) : null,
            ]),
          ]))),
        ])])
      : el('p', { class: 'muted', text: 'Nothing has been run yet.' }));
  }

  const chips = el('div', { class: 'chips' }, [
    chip('everything', '', () => { filter = ''; draw(); }),
    ...kinds.map((kind) => chip(kind, kind, () => { filter = kind; draw(); })),
  ]);

  page.append(card(null, [
    el('div', { class: 'row' }, [
      chips,
      el('span', { class: 'spacer' }),
      el('button', { class: 'small', text: 'Open the Reports folder', onclick: () => app.api.reveal(app.status?.reportsFolder) }),
    ]),
  ]));

  page.append(list);
  draw();
  return page;
}

function chip(label, value, onclick) {
  return el('button', { class: 'small', text: label, onclick, 'data-kind': value });
}

async function show(app, id) {
  try {
    const run = await app.api.run(id);
    const dialog = el('dialog', { style: 'min-width:min(1100px, 92vw)' }, [
      el('h2', { text: run.title }),
      summaryList(run),
      el('div', { style: 'margin-top:12px' }, [reviewTable(run, { editable: [] }).node]),
      el('div', { class: 'row', style: 'margin-top:14px;justify-content:flex-end' }, [
        el('button', { text: 'Close', onclick: () => dialog.close() }),
      ]),
    ]);
    document.body.append(dialog);
    dialog.addEventListener('close', () => dialog.remove());
    dialog.showModal();
  } catch (error) {
    toast(error.message, 'bad');
  }
}
