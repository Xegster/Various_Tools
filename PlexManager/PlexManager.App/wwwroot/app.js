import { api } from './api.js';
import { el, toast } from './ui.js';
import { dashboard } from './screens/dashboard.js';
import { backup } from './screens/backup.js';
import { scan } from './screens/scan.js';
import { fix } from './screens/fix.js';
import { prune } from './screens/prune.js';
import { migrate } from './screens/migrate.js';
import { streaming } from './screens/streaming.js';
import { clean } from './screens/clean.js';
import { reports } from './screens/reports.js';
import { settings } from './screens/settings.js';

const screens = { dashboard, backup, scan, fix, prune, migrate, streaming, clean, reports, settings };

const host = document.getElementById('screen');
const statusbar = document.getElementById('statusbar');
const pane = document.getElementById('jobpane');
const logNode = document.getElementById('joblog');

let currentStatus = null;
let watching = null;

/** Shared context handed to every screen: the API, the status, and the job helpers. */
export const app = {
  api,
  get status() { return currentStatus; },
  refreshStatus,
  render,
  runJob,
  watchJob,
};

async function refreshStatus() {
  try {
    currentStatus = await api.status();
  } catch (error) {
    statusbar.replaceChildren(el('span', { class: 'pill bad', text: `Cannot reach the app: ${error.message}` }));
    return null;
  }

  statusbar.replaceChildren(...currentStatus.services.map((service) => el('span', {
    class: `pill ${service.connected ? 'ok' : 'bad'}`,
    title: service.address + (service.detail ? ` - ${service.detail}` : ''),
  }, [el('span', { class: 'dot' }), `${service.name}${service.detail && service.connected ? `: ${service.detail}` : ''}`])));

  statusbar.append(
    el('span', { class: 'spacer' }),
    el('span', { class: 'muted mono small', text: currentStatus.dataFolder }),
    el('button', { class: 'small', text: 'Open folder', onclick: () => api.reveal(currentStatus.dataFolder) }),
  );

  return currentStatus;
}

/**
 * Starts an operation, shows its log, and resolves when it has finished, so a screen can redraw itself with
 * what the run produced. Destructive runs are expected to have been confirmed by the screen already.
 */
async function runJob(operation, options = {}) {
  let job;
  try {
    job = await api.startJob(operation, options);
  } catch (error) {
    toast(error.message, 'bad');
    return null;
  }

  return watchJob(job.id, job.description);
}

function watchJob(id, description) {
  if (watching) clearTimeout(watching.timer);
  watching = { id, from: 0, timer: null, onDone: null };

  document.getElementById('app').classList.add('with-job');
  pane.hidden = false;
  document.getElementById('jobtitle').textContent = description || id;
  logNode.replaceChildren();
  setState('running');

  const promise = new Promise((resolve) => { watching.onDone = resolve; });
  poll();
  return promise;
}

async function poll() {
  if (!watching) return;
  let job;
  try {
    job = await api.job(watching.id, watching.from);
  } catch (error) {
    setState('failed', error.message);
    return;
  }

  for (const line of job.lines) {
    logNode.append(el('div', { class: line.level, text: line.message || ' ' }));
    watching.from = line.index + 1;
  }
  logNode.scrollTop = logNode.scrollHeight;

  if (job.state === 'running') {
    watching.timer = setTimeout(poll, 700);
    return;
  }

  setState(job.state, job.error);
  const done = watching.onDone;
  const runId = job.runId;
  watching.onDone = null;
  await refreshStatus();
  done?.({ state: job.state, runId, error: job.error });
}

function setState(state, error) {
  const node = document.getElementById('jobstate');
  node.textContent = state;
  node.className = `pill ${state === 'succeeded' ? 'ok' : state === 'running' ? 'warn' : 'bad'}`;
  document.getElementById('jobcancel').disabled = state !== 'running';
  if (error && state !== 'cancelled') toast(error, 'bad');
}

document.getElementById('jobcancel').addEventListener('click', async () => {
  if (watching) await api.cancelJob(watching.id);
});

document.getElementById('jobclose').addEventListener('click', () => {
  if (watching) {
    clearTimeout(watching.timer);

    // Closing the pane while a job is still going must not leave whoever started it waiting for ever.
    watching.onDone?.({ state: 'detached', runId: null, error: null });
  }

  watching = null;
  pane.hidden = true;
  document.getElementById('app').classList.remove('with-job');
});

async function render() {
  const name = (location.hash.replace(/^#\//, '') || 'dashboard').split('/')[0];
  const screen = screens[name] ? name : 'dashboard';

  for (const link of document.querySelectorAll('#sidebar a')) {
    link.classList.toggle('active', link.dataset.screen === screen);
  }

  host.replaceChildren(el('p', { class: 'muted', text: 'Loading...' }));
  try {
    const node = await screens[screen](app);
    host.replaceChildren(node);
  } catch (error) {
    host.replaceChildren(el('div', { class: 'card' }, [
      el('h2', { text: 'That screen could not be built' }),
      el('p', { class: 'muted', text: error.message }),
    ]));
  }
}

window.addEventListener('hashchange', render);

await refreshStatus();

// A fresh install goes straight to Settings, which doubles as the first-run wizard.
if (currentStatus && !currentStatus.setupComplete && !location.hash) {
  location.hash = '#/settings';
}

await render();
