// Small helpers the screens share: building elements, review tables, toasts, confirmations and the list picker.

export function el(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined || value === false) continue;
    if (name === 'class') node.className = value;
    else if (name === 'text') node.textContent = value;
    else if (name === 'html') node.innerHTML = value;
    else if (name.startsWith('on')) node.addEventListener(name.slice(2), value);
    else if (value === true) node.setAttribute(name, '');
    else node.setAttribute(name, value);
  }
  for (const child of [].concat(children)) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function card(title, children, lede) {
  return el('div', { class: 'card' }, [
    title ? el('h2', { text: title, style: 'margin-top:0' }) : null,
    lede ? el('p', { class: 'muted small', text: lede, style: 'margin:0 0 10px' }) : null,
    ...[].concat(children).filter(Boolean),
  ]);
}

export function toast(message, kind = '') {
  const node = el('div', { class: `toast ${kind}`, text: message });
  document.body.append(node);
  setTimeout(() => node.remove(), kind === 'bad' ? 9000 : 4500);
}

/** A yes/no dialog. Destructive steps pass `typed` so the word has to be typed out first. */
export function confirmDialog({ title, body, confirmLabel = 'Yes', typed = null, danger = false }) {
  return new Promise((resolve) => {
    const input = typed ? el('input', { placeholder: typed, style: 'margin-top:10px;width:100%' }) : null;
    const confirm = el('button', { class: danger ? 'danger' : 'primary', text: confirmLabel, disabled: !!typed });
    if (input) {
      input.addEventListener('input', () => { confirm.disabled = input.value.trim() !== typed; });
    }
    const dialog = el('dialog', {}, [
      el('h2', { text: title }),
      el('div', { class: 'muted', html: body }),
      input,
      el('div', { class: 'row', style: 'margin-top:16px;justify-content:flex-end' }, [
        el('button', { text: 'Cancel', onclick: () => { dialog.close(); resolve(false); } }),
        confirm,
      ]),
    ]);
    confirm.addEventListener('click', () => { dialog.close(); resolve(true); });
    document.body.append(dialog);
    dialog.addEventListener('close', () => dialog.remove());
    dialog.showModal();
    if (input) input.focus();
  });
}

/**
 * A table of run rows. `editable` names the columns the user may change; a checkbox column is used for the
 * yes/no ones (Approve, Keep, Monitor, SearchOnAdd) and a text box for the rest (Action).
 * `onChange` receives the full edited row set.
 */
export function reviewTable(run, { editable = run.editableColumns || [], onChange = null, hide = [], link = {} } = {}) {
  const columns = (run.columns || []).filter((c) => !hide.includes(c));
  const rows = (run.rows || []).map((r) => ({ ...r }));
  const checkboxColumns = ['Approve', 'Keep', 'Monitor', 'SearchOnAdd'];

  const body = el('tbody');
  for (const row of rows) {
    const tr = el('tr');
    for (const column of columns) {
      const value = row[column] ?? '';
      if (editable.includes(column) && checkboxColumns.includes(column)) {
        const box = el('input', {
          type: 'checkbox',
          onchange: () => { row[column] = box.checked ? 'yes' : ''; onChange?.(rows); },
        });
        box.checked = value.toLowerCase() === 'yes';
        tr.append(el('td', {}, [box]));
      } else if (editable.includes(column)) {
        const field = el('input', {
          value,
          style: 'width:100%;min-width:140px',
          oninput: () => { row[column] = field.value; onChange?.(rows); },
        });
        tr.append(el('td', {}, [field]));
      } else if (link[column] && value) {
        tr.append(el('td', {}, [el('a', { href: value, target: '_blank', rel: 'noreferrer', text: link[column] })]));
      } else if (/^https?:\/\//.test(value)) {
        tr.append(el('td', {}, [el('a', { href: value, target: '_blank', rel: 'noreferrer', text: 'open' })]));
      } else {
        tr.append(el('td', { class: /^\d+$/.test(value) ? 'num' : '', text: value }));
      }
    }
    body.append(tr);
  }

  if (!rows.length) {
    body.append(el('tr', {}, [el('td', { colspan: columns.length, class: 'muted', text: 'Nothing here.' })]));
  }

  return {
    rows,
    node: el('div', { class: 'tablewrap' }, [
      el('table', {}, [el('thead', {}, [el('tr', {}, columns.map((c) => el('th', { text: c })))]), body]),
    ]),
  };
}

export function summaryList(run) {
  return el('div', { class: 'summary' }, (run.summary || []).map((line) => el('span', { text: line })));
}

/** A step in a numbered workflow: a title, an explanation and its buttons. */
export function step(number, title, description, controls) {
  return el('div', { class: 'step' }, [
    el('div', { class: 'n', text: String(number) }),
    el('div', { class: 'body' }, [
      el('h3', { text: title }),
      el('div', { class: 'muted small', html: description }),
      el('div', { class: 'row', style: 'margin-top:8px' }, [].concat(controls).filter(Boolean)),
    ]),
  ]);
}

/**
 * The reusable list picker (phase 3): a drop-down of the account's personal lists with the default watchlist
 * pinned at the top, a refresh button, and multi-select when several lists may be chosen.
 * Lists are held by id, so renaming one on Trakt does not break the setting.
 */
export function listPicker({ lists, selected, multiple, onChange, allowCreate = false, refresh = null }) {
  const chosen = [].concat(selected || []).filter(Boolean);
  const container = el('div', {});

  function render() {
    container.replaceChildren();

    const select = el('select', { style: 'min-width:240px' });
    select.append(el('option', { value: '', text: multiple ? 'Add a list...' : 'Choose a list...' }));
    for (const list of lists) {
      if (multiple && chosen.some((c) => sameList(c, list))) continue;
      select.append(el('option', {
        value: `${list.id}|${list.slug}`,
        text: list.isWatchlist ? 'Watchlist (default)' : `${list.name} (${list.itemCount})`,
      }));
    }

    select.addEventListener('change', () => {
      if (!select.value) return;
      const [id, slug] = select.value.split('|');
      const list = lists.find((l) => String(l.id) === id && l.slug === slug);
      const reference = { id: list.id, slug: list.slug, name: list.name };
      if (multiple) chosen.push(reference);
      else chosen.splice(0, chosen.length, reference);
      onChange(multiple ? chosen : chosen[0]);
      render();
    });

    const controls = el('div', { class: 'row tight' }, [select]);
    if (refresh) {
      controls.append(el('button', { class: 'small', text: 'Refresh', onclick: refresh }));
    }
    if (allowCreate) {
      const name = el('input', { placeholder: 'or a new list name', style: 'width:180px' });
      controls.append(name, el('button', {
        class: 'small',
        text: 'Use this name',
        onclick: () => {
          if (!name.value.trim()) return;
          const reference = { id: 0, slug: '', name: name.value.trim() };
          if (multiple) chosen.push(reference);
          else chosen.splice(0, chosen.length, reference);
          onChange(multiple ? chosen : chosen[0]);
          render();
        },
      }));
    }

    container.append(controls);

    if (chosen.length) {
      container.append(el('div', { class: 'chips', style: 'margin-top:8px' }, chosen.map((list) => el('span', { class: 'chip' }, [
        label(list),
        multiple || chosen.length > 1
          ? el('button', {
              text: 'x',
              title: 'Remove',
              onclick: () => {
                chosen.splice(chosen.indexOf(list), 1);
                onChange(multiple ? chosen : chosen[0] || null);
                render();
              },
            })
          : null,
      ]))));
    }
  }

  render();
  return container;
}

export function label(list) {
  if (!list) return 'none';
  if (list.slug === 'watchlist' || (list.id === 0 && !list.name)) return 'Watchlist (default)';
  return list.name || `list ${list.id}`;
}

function sameList(a, b) {
  if (a.slug === 'watchlist' || b.isWatchlist) return a.slug === 'watchlist' && b.isWatchlist;
  return String(a.id) === String(b.id) || (a.name && a.name === b.name);
}

export function when(value) {
  if (!value) return 'never';
  const date = new Date(value);
  const days = (Date.now() - date.getTime()) / 86400000;
  if (days < 1) return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  if (days < 7) return `${Math.floor(days)} days ago`;
  return date.toLocaleDateString();
}
