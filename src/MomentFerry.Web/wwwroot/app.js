/* MomentFerry Console ---------------------------------------------------
   Vanilla ES2019+, no build step. app.js owns navigation, shared state and
   every renderer; settings.js owns the runtime-settings form, automation
   status, storage and image updates, and pushes its results back here.
--------------------------------------------------------------------- */

const $ = (id) => document.getElementById(id);

let appInfo = { dryRun: true };
let presets = [];
let shares = [];
let groups = [];
let events = [];
let operations = [];
let quarantinedOperations = [];

/* Filled in by settings.js */
let automationInfo = null;
let storageInfo = null;
let updateInfo = null;

let currentView = 'overview';
const backgroundTasks = new Map();
let taskClock = null;
let scanRequestedAt = null;
let scanScheduleError = '';
let manualScanResult = null;

const TITLES = {
  overview: ['Overview', 'Every watched folder, the running event and anything waiting on you.'],
  events: ['Events', 'A capture-time window. Anything shot inside it lands in one folder.'],
  shares: ['Shares', 'The folders MomentFerry can see. Your sync tool keeps them filled.'],
  groups: ['Source groups', 'Which phones or cameras feed an event.'],
  renaming: ['File naming', 'Templates that rename files on their way to the destination.'],
  preview: ['Routing preview', 'See where every file would go before a single byte moves.'],
  ops: ['Operations', 'Every copy, checksum, commit and deletion, in order.'],
  settings: ['Automation & safety', 'How often MomentFerry looks, and what it is allowed to do.'],
  updates: ['Image updates', 'Stable releases from GHCR, applied by an isolated companion.'],
  setup: ['Finish setup', 'One last check before anything moves.']
};

/* Helpers ------------------------------------------------------------- */

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[c]);
}

/** Operations only carry a source path, so match it back to the owning share. */
function shareForPath(path) {
  if (!path) return null;
  const value = String(path).replace(/\\/g, '/');
  return shares
    .filter(share => value.startsWith(String(share.path).replace(/\\/g, '/')))
    .sort((a, b) => b.path.length - a.path.length)[0] || null;
}

function baseName(path) {
  const parts = String(path ?? '').split(/[\\/]/);
  return parts[parts.length - 1] || String(path ?? '');
}

function safeSegment(value) {
  const cleaned = String(value ?? '').trim().replace(/[\/:*?"<>|]/g, '_');
  return cleaned || 'unnamed';
}

// Mirrors DestinationPathResolver: {source} and {owner} stay literal because they
// depend on the source share of each individual file.
function destinationFolder(event) {
  const captured = new Date(event.startAt);
  const valid = !Number.isNaN(captured.getTime());
  const pad = (number, size) => String(number).padStart(size, '0');
  return String(event.destinationFolderTemplate ?? '')
    .replace(/\{event\.name\}/gi, () => safeSegment(event.name))
    .replace(/\{event\.type\}/gi, () => safeSegment(event.type || 'Event'))
    .replace(/\{year\}/gi, valid ? pad(captured.getFullYear(), 4) : '{year}')
    .replace(/\{month\}/gi, valid ? pad(captured.getMonth() + 1, 2) : '{month}')
    .replace(/\{day\}/gi, valid ? pad(captured.getDate(), 2) : '{day}');
}

function destinationPathFor(event, destination) {
  return `${destination ? destination.path : 'destination'}/${destinationFolder(event)}`;
}

function formatDate(value) {
  if (!value) return 'unknown';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? escapeHtml(value) : date.toLocaleString();
}

function formatBytes(value) {
  if (value == null || Number.isNaN(Number(value))) return 'unknown';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let number = Number(value);
  let index = 0;
  while (number >= 1024 && index < units.length - 1) { number /= 1024; index++; }
  return `${number.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number.toLocaleString() : '0';
}

function toLocalInput(value) {
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function fromLocalInput(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) throw new Error('Invalid date/time.');
  return date.toISOString();
}

function operationModeValue() {
  const picked = document.querySelector('input[name="eventOperation"]:checked');
  return picked ? picked.value : 'SafeMove';
}

function setOperationMode(mode) {
  document.querySelectorAll('input[name="eventOperation"]').forEach(input => {
    input.checked = input.value === mode;
  });
}

async function request(url, options = {}) {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      message = body.error || body.title || body.detail || message;
    } catch {}
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

/* Background tasks --------------------------------------------------- */

function taskDuration(task) {
  const seconds = Math.max(0, Math.floor(((task.finishedAt || Date.now()) - task.startedAt) / 1000));
  if (seconds < 60) return `${seconds}s`;
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
}

function renderBackgroundTasks() {
  const tasks = [...backgroundTasks.values()].sort((a, b) => b.startedAt - a.startedAt);
  const running = tasks.filter(task => task.state === 'running').length;
  $('taskCenter').classList.toggle('hidden', !tasks.length);
  if (!tasks.length) return;

  $('taskCenterSummary').textContent = running
    ? `${running} running · you can change views`
    : `${tasks.length} finished`;
  $('clearFinishedTasks').classList.toggle('hidden', tasks.every(task => task.state === 'running'));
  $('backgroundTaskList').innerHTML = tasks.slice(0, 6).map(task => {
    const state = task.state === 'running' ? 'Running' : task.state === 'success' ? 'Completed' : 'Failed';
    const dot = task.state === 'running' ? 'dot-amb' : task.state === 'success' ? 'dot-acc' : 'dot-red';
    const content = `
      <span class="dot ${dot}"></span>
      <span class="task-row-main">
        <span class="task-row-label">${escapeHtml(task.label)}</span>
        <span class="task-row-detail">${escapeHtml(task.detail || state)}</span>
      </span>
      <span class="task-row-time">${taskDuration(task)}</span>`;
    return task.view
      ? `<button class="task-row task-${task.state}" type="button" data-view="${escapeHtml(task.view)}">${content}</button>`
      : `<div class="task-row task-${task.state}">${content}</div>`;
  }).join('');
}

function updateTaskClock() {
  const running = [...backgroundTasks.values()].some(task => task.state === 'running');
  if (running && !taskClock) taskClock = setInterval(renderBackgroundTasks, 1000);
  if (!running && taskClock) {
    clearInterval(taskClock);
    taskClock = null;
  }
}

window.runBackgroundTask = function (key, label, view, action) {
  const existing = backgroundTasks.get(key);
  if (existing?.state === 'running') return existing.promise;

  const task = { key, label, view, state: 'running', detail: 'Running', startedAt: Date.now() };
  backgroundTasks.set(key, task);
  renderBackgroundTasks();
  updateTaskClock();

  task.promise = Promise.resolve().then(action).then(result => {
    task.state = 'success';
    task.detail = 'Completed';
    task.finishedAt = Date.now();
    return result;
  }).catch(error => {
    task.state = 'error';
    task.detail = error.message || String(error);
    task.finishedAt = Date.now();
    throw error;
  }).finally(() => {
    renderBackgroundTasks();
    updateTaskClock();
  });

  return task.promise;
};

$('clearFinishedTasks').addEventListener('click', () => {
  backgroundTasks.forEach((task, key) => {
    if (task.state !== 'running') backgroundTasks.delete(key);
  });
  renderBackgroundTasks();
});

/* Theme --------------------------------------------------------------- */

function applyTheme(theme) {
  document.documentElement.dataset.theme = theme;
  $('themeLabel').textContent = theme === 'light' ? 'Light' : 'Dark';
  try { localStorage.setItem('momentferry.theme', theme); } catch {}
}

$('themeToggle').addEventListener('click', () => {
  applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light');
});

/* Navigation ---------------------------------------------------------- */

function setView(view) {
  if (!TITLES[view]) view = 'overview';
  currentView = view;

  document.querySelectorAll('.view').forEach(section => {
    section.classList.toggle('hidden', section.id !== `view-${view}`);
  });
  document.querySelectorAll('#nav .nav-item').forEach(button => {
    if (button.dataset.view === view) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  });

  const [title, subtitle] = TITLES[view];
  $('pageTitle').textContent = title;
  $('pageSubtitle').textContent = subtitle;

  if (view === 'setup') renderSetup();
  if (location.hash.slice(1) !== view) history.replaceState(null, '', `#${view}`);
  window.scrollTo({ top: 0, behavior: 'auto' });
}

document.addEventListener('click', event => {
  const target = event.target.closest('[data-view]');
  if (!target) return;
  event.preventDefault();
  setView(target.dataset.view);
});

window.addEventListener('hashchange', () => setView(location.hash.slice(1) || 'overview'));

/* Mode chip ----------------------------------------------------------- */

function renderMode() {
  const dry = appInfo.dryRun !== false;
  const chip = $('modeChip');
  const dot = $('modeDot');
  chip.classList.toggle('is-dry', dry);
  chip.classList.toggle('is-live', !dry);
  dot.className = `dot ${dry ? 'dot-amb' : 'dot-acc'}`;
  $('status').textContent = dry ? 'Dry Run — nothing is moved' : 'Live — files are moved for real';
  $('modeAction').textContent = dry ? 'Go Live…' : 'Back to Dry Run';
}

/* Data ---------------------------------------------------------------- */

async function load() {
  try {
    const [info, presetData, shareData, groupData, eventData, operationData, quarantineData] = await Promise.all([
      request('/api/v1/info'),
      request('/api/v1/share-presets'),
      request('/api/v1/shares'),
      request('/api/v1/source-groups/'),
      request('/api/v1/events/'),
      request('/api/v1/operations?limit=50'),
      request('/api/v1/quarantine?limit=200')
    ]);
    appInfo = info;
    presets = presetData;
    shares = shareData;
    groups = groupData;
    events = eventData;
    operations = operationData;
    quarantinedOperations = quarantineData;
    renderAll();
    await reloadRenaming();
  } catch (error) {
    $('status').textContent = 'Offline';
    $('pageSubtitle').textContent = error.message;
  }
}

function renderAll() {
  renderMode();
  renderBadges();
  renderPresets();
  renderShares();
  renderGroupChoices();
  renderGroups();
  renderEventSelectors();
  renderEvents();
  renderRoutingSources();
  renderOperations();
  renderQuarantine();
  renderOnboarding();
  renderOverview();
}

function renderBadges() {
  $('badgeEvents').textContent = events.length ? String(events.length) : '';
  $('badgeShares').textContent = shares.length ? String(shares.length) : '';
  $('badgeGroups').textContent = groups.length ? String(groups.length) : '';
}

/* Overview ------------------------------------------------------------ */

function renderOverview() {
  renderRunningEvent();
  renderStorage();
  renderSources();
  renderRecentOps();
}

// Everything currently collecting. Falls back to planned, then to any event, so the card still
// says something useful before the first event is started.
function activeEvents() {
  const active = events.filter(x => x.status === 'Active');
  if (active.length) return active;
  const planned = events.filter(x => x.status === 'Planned');
  if (planned.length) return planned;
  return events.length ? [events[0]] : [];
}

function activeEvent() {
  return activeEvents()[0] || null;
}

// Cycle counters are automation-wide, not per event, so they are rendered once below the event
// list instead of per row, where they would read as per-event totals.
function automationBlock() {
  const dry = appInfo.dryRun !== false;
  const cycleRunning = automationInfo?.cycleRunning === true;
  const matched = automationInfo ? (cycleRunning ? automationInfo.currentMatched : automationInfo.lastMatched) : 0;
  const moved = automationInfo
    ? (dry
      ? (cycleRunning ? automationInfo.currentWouldMove : automationInfo.lastWouldMove)
      : automationInfo.lastExecuted)
    : 0;
  const held = quarantinedOperations.length;
  const processed = automationInfo?.currentProcessed || 0;
  const total = automationInfo?.currentTotal || 0;
  const percent = total ? Math.min(100, Math.round(processed / total * 100)) : 0;
  const progress = cycleRunning ? `
    <div class="event-progress">
      <div class="event-progress-head">
        <span>${escapeHtml(automationInfo.currentPhase || 'Preparing')} · ${escapeHtml(automationInfo.currentShareName || 'sources')}</span>
        <span>${total ? `${formatNumber(processed)} / ${formatNumber(total)} · ${percent}%` : 'starting…'}</span>
      </div>
      <div class="progress-track" role="progressbar" aria-label="Current automation cycle" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${percent}"><span style="width:${percent}%"></span></div>
    </div>` : '';
  const scanDisabled = cycleRunning || scanRequestedAt || appInfo.automationEnabled === false;

  return `
    ${progress}
    <div class="stat-grid stat-grid-3">
      <div class="stat">
        <div class="stat-value">${formatNumber(matched)}</div>
        <div class="stat-label">${cycleRunning ? 'matched so far' : 'matched last cycle'}</div>
      </div>
      <div class="stat">
        <div class="stat-value acc">${formatNumber(moved)}</div>
        <div class="stat-label">${dry ? (cycleRunning ? 'would move so far' : 'would move') : 'moved last cycle'}</div>
      </div>
      <button class="stat" type="button" data-view="ops">
        <div class="stat-value amb">${formatNumber(held)}</div>
        <div class="stat-label">held</div>
      </button>
    </div>
    <div class="event-scan-row">
      <div>
        <div class="kicker">Automation</div>
        <div class="event-scan-time" id="nextScanCountdown"></div>
      </div>
      <button class="btn" type="button" data-scan-now ${scanDisabled ? 'disabled' : ''}>
        ${cycleRunning ? 'Scanning…' : scanRequestedAt ? 'Queued…' : 'Scan now'}
      </button>
    </div>`;
}

function eventSummary(event) {
  return {
    groupName: groups.find(x => x.id === event.sourceGroupId)?.name || 'unknown group',
    destination: shares.find(x => x.id === event.destinationShareId),
    window: `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : 'open'}`,
    mode: event.operationMode === 'Copy' ? 'Copy' : 'Safe Move'
  };
}

// Compact rows keep many events scannable; the full destination path stays in the Events view.
function eventRowList(list) {
  const VISIBLE = 5;
  const shown = list.slice(0, VISIBLE);
  const rest = list.length - shown.length;

  const rows = shown.map(event => {
    const info = eventSummary(event);
    return `
      <div class="list-row" style="padding:11px 14px">
        <div class="list-main">
          <div class="list-heading" style="margin-bottom:3px">
            <span class="list-title">${escapeHtml(event.name)}</span>
            ${eventStatusPill(event.status)}
          </div>
          <div class="list-meta">${escapeHtml(info.window)} · ${escapeHtml(info.groupName)} → ${escapeHtml(info.destination?.name || 'missing destination')} · ${info.mode}</div>
        </div>
        <div class="card-actions">
          <button class="btn btn-sm btn-ghost" type="button" data-event-toggle="${escapeHtml(event.id)}">
            ${event.status === 'Active' ? 'Stop' : 'Start'}
          </button>
        </div>
      </div>`;
  }).join('');

  const more = rest > 0
    ? `<button class="btn btn-sm btn-ghost" type="button" data-view="events">Show ${formatNumber(rest)} more →</button>`
    : '';

  return `<div class="stack" style="gap:8px;margin-bottom:14px">${rows}${more}</div>`;
}

function eventHeadline(event) {
  const info = eventSummary(event);
  return `
    <div class="row" style="align-items:baseline;gap:10px;margin-bottom:3px">
      <div style="font-size:24px;font-weight:600;letter-spacing:-.02em">${escapeHtml(event.name)}</div>
      <div style="font-size:12.5px;color:var(--mut)">${escapeHtml(event.type || 'Event')} · ${info.mode}</div>
    </div>
    <div class="mono" style="font-size:12px;color:var(--mut);margin-bottom:14px">
      ${escapeHtml(info.window)} · ${escapeHtml(info.groupName)} → ${escapeHtml(destinationPathFor(event, info.destination))}
    </div>`;
}

function renderRunningEvent() {
  const body = $('ovEventBody');
  const state = $('ovEventState');
  const kicker = $('ovEventKicker');
  const list = activeEvents();

  if (!list.length) {
    kicker.textContent = 'Running event';
    state.className = 'pill';
    state.textContent = 'None';
    body.innerHTML = `
      <div style="font-size:13px;color:var(--mut);line-height:1.6">
        No event yet. An event is a capture-time window — everything shot inside it lands in one folder.
      </div>
      <div class="actions" style="margin-top:14px">
        <button class="btn btn-acc" type="button" data-view="events">Create an event</button>
      </div>`;
    return;
  }

  const multiple = list.length > 1;
  const collecting = list[0].status === 'Active';
  kicker.textContent = multiple ? 'Running events' : 'Running event';
  state.className = collecting ? 'pill pill-acc' : 'pill';
  state.textContent = multiple
    ? `${formatNumber(list.length)} ${collecting ? 'active' : list[0].status.toLowerCase()}`
    : (collecting ? 'Active' : list[0].status);

  const toggle = multiple
    ? ''
    : `<button class="btn btn-acc" type="button" data-event-toggle="${escapeHtml(list[0].id)}">
         ${collecting ? 'Stop event' : 'Start event'}
       </button>`;

  body.innerHTML = `
    ${multiple ? eventRowList(list) : eventHeadline(list[0])}
    ${automationBlock()}
    <div class="actions" style="margin-top:14px">
      ${toggle}
      <button class="btn btn-ghost" type="button" data-view="preview">Preview routing</button>
    </div>`;
  renderNextScanCountdown();
}

function renderNextScanCountdown() {
  const target = $('nextScanCountdown');
  if (!target) return;
  if (scanScheduleError) {
    target.textContent = scanScheduleError;
    target.className = 'event-scan-time error';
    return;
  }
  target.className = 'event-scan-time';
  if (automationInfo?.cycleRunning) {
    target.textContent = 'Scan in progress';
    return;
  }
  if (scanRequestedAt) {
    target.textContent = 'Manual scan queued';
    return;
  }
  if (appInfo.automationEnabled === false) {
    target.textContent = 'Automation is off';
    return;
  }
  if (!automationInfo?.lastCycleCompletedAt) {
    target.textContent = 'First scan pending';
    return;
  }

  const next = new Date(automationInfo.lastCycleCompletedAt).getTime()
    + Number(appInfo.reconciliationIntervalSeconds || 300) * 1000;
  const seconds = Math.max(0, Math.ceil((next - Date.now()) / 1000));
  const result = manualScanResult
    ? `Manual scan completed ${new Date(manualScanResult.completedAt).toLocaleTimeString()} · ${formatNumber(manualScanResult.matched)} matched · ${formatNumber(manualScanResult.wouldMove)} would move${manualScanResult.errors ? ` · ${formatNumber(manualScanResult.errors)} errors` : ''} · `
    : '';
  if (!seconds) {
    target.textContent = `${result}Next scan due now`;
    return;
  }
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds % 3600 / 60);
  const remainder = seconds % 60;
  target.textContent = `${result}Next scan in ${hours ? `${hours}:` : ''}${String(minutes).padStart(hours ? 2 : 1, '0')}:${String(remainder).padStart(2, '0')}`;
  target.title = `Scheduled for ${new Date(next).toLocaleString()}`;
}

async function triggerScanNow() {
  if (automationInfo?.cycleRunning || scanRequestedAt || appInfo.automationEnabled === false) return;
  scanScheduleError = '';
  manualScanResult = null;
  scanRequestedAt = new Date().toISOString();
  renderRunningEvent();
  try {
    await runBackgroundTask('manual-scan', 'Manual scan', 'overview', async () => {
      const result = await request('/api/v1/automation/run', { method: 'POST' });
      scanRequestedAt = result.requestedAt;
      await monitorManualScan(result.requestedAt);
    });
  } catch (error) {
    scanRequestedAt = null;
    scanScheduleError = `Could not start scan · ${error.message}`;
  }
  renderRunningEvent();
}

async function monitorAutomationCycle(requestedAt, taskKey, phaseFallback) {
  const requested = new Date(requestedAt).getTime();
  const deadline = Date.now() + 30 * 60 * 1000;

  while (Date.now() < deadline) {
    const status = await request('/api/v1/status', { cache: 'no-store' });
    automationInfo = status.automation;
    appInfo.automationEnabled = status.automationEnabled;
    appInfo.reconciliationIntervalSeconds = status.reconciliationIntervalSeconds;

    const task = backgroundTasks.get(taskKey);
    if (task) {
      const total = automationInfo.currentTotal || 0;
      const processed = automationInfo.currentProcessed || 0;
      const percent = total ? Math.min(100, Math.round(processed / total * 100)) : 0;
      task.detail = automationInfo.cycleRunning
        ? `${automationInfo.currentPhase || phaseFallback} · ${total ? `${formatNumber(processed)} / ${formatNumber(total)} · ${percent}%` : 'starting…'}`
        : 'Queued';
      renderBackgroundTasks();
    }

    const completed = new Date(automationInfo.lastCycleCompletedAt || 0).getTime();
    if (!automationInfo.cycleRunning && completed >= requested) {
      renderOverview();
      return {
        completedAt: automationInfo.lastCycleCompletedAt,
        matched: automationInfo.lastMatched || 0,
        wouldMove: automationInfo.lastWouldMove || 0,
        executed: automationInfo.lastExecuted || 0,
        errors: automationInfo.lastErrors || 0
      };
    }

    renderOverview();
    await new Promise(resolve => setTimeout(resolve, 250));
  }

  throw new Error('Timed out waiting for the cycle to finish');
}

async function monitorManualScan(requestedAt) {
  manualScanResult = await monitorAutomationCycle(requestedAt, 'manual-scan', 'Scanning');
  scanRequestedAt = null;
  scanScheduleError = '';
  renderOverview();
}

function renderStorage() {
  const target = $('ovStorage');
  if (!storageInfo) {
    target.innerHTML = '<div class="message">Loading storage…</div>';
    return;
  }

  const items = storageInfo.items || [];
  if (!items.length) {
    target.innerHTML = `
      <div style="font-size:13px;color:var(--mut);line-height:1.6">
        No destination share configured yet, so there is nowhere for MomentFerry to put anything.
      </div>
      <div class="actions" style="margin-top:14px">
        <button class="btn btn-acc" type="button" data-view="shares">Add a destination</button>
      </div>`;
    return;
  }

  const primary = items.find(x => x.exists && x.availableFreeSpaceBytes != null) || items[0];
  const reserve = storageInfo.minimumFreeSpaceReserveBytes || 0;

  if (!primary.exists) {
    target.innerHTML = `
      <div style="font-size:15px;font-weight:600;color:var(--red);margin-bottom:6px">Path missing</div>
      <div style="font-size:12.5px;color:var(--mut)">${escapeHtml(primary.error || 'MomentFerry cannot see this folder inside the container.')}</div>
      <div class="mono" style="margin-top:auto;padding-top:14px;border-top:1px solid var(--line);font-size:11.5px;color:var(--dim)">${escapeHtml(primary.path)}</div>`;
    return;
  }

  const free = Number(primary.availableFreeSpaceBytes) || 0;
  const others = items.filter(x => x !== primary);
  const belowReserve = primary.belowReserve;

  // The bar splits the free space into what MomentFerry may use and the reserve
  // it always holds back. A tiny reserve stays visible at a 1.5% floor.
  const reservePercent = belowReserve
    ? 100
    : Math.min(100, Math.max(1.5, (reserve / (free + reserve)) * 100));

  target.innerHTML = `
    <div class="row" style="align-items:baseline;gap:7px">
      <span style="font-size:28px;font-weight:600;letter-spacing:-.02em">${escapeHtml(formatBytes(free))}</span>
      <span style="font-size:14px;color:var(--mut)">free on ${escapeHtml(primary.name)}</span>
    </div>
    <div class="meter" title="Usable free space versus the reserve MomentFerry holds back">
      <div class="meter-used" style="width:${(100 - reservePercent).toFixed(2)}%"></div>
      <div class="meter-reserve" style="width:${reservePercent.toFixed(2)}%"></div>
    </div>
    <div style="font-size:11.5px;color:var(--mut);line-height:1.5">
      ${escapeHtml(formatBytes(reserve))} is always held back on top of each file.
      ${belowReserve
        ? '<span style="color:var(--amb)">Free space is below that reserve — transfers will hold.</span>'
        : 'There is room for the next transfers.'}
    </div>
    ${others.length ? `<div style="font-size:11.5px;color:var(--mut);margin-top:10px">${others.map(x =>
        `${escapeHtml(x.name)}: ${x.exists ? escapeHtml(formatBytes(x.availableFreeSpaceBytes)) + ' free' : 'path missing'}`
      ).join(' · ')}</div>` : ''}
    <div class="mono" style="margin-top:auto;padding-top:14px;border-top:1px solid var(--line);font-size:11.5px;color:var(--dim)">${escapeHtml(primary.path)}</div>`;
}

function renderSources() {
  const target = $('ovSources');
  if (!shares.length) {
    target.innerHTML = '<div class="empty" style="grid-column:1/-1"><strong>No folders watched yet</strong>Add the folders your sync tool fills, plus one destination.</div>';
    return;
  }

  target.innerHTML = shares.map(share => {
    const isDestination = share.role !== 'Source';
    const storage = (storageInfo?.items || []).find(x => x.shareId === share.id);
    const detail = isDestination
      ? (storage
        ? (storage.exists ? `${formatBytes(storage.availableFreeSpaceBytes)} free${storage.belowReserve ? ' · below reserve' : ''}` : 'path missing')
        : 'destination')
      : `${share.recursive ? 'subfolders' : 'top-level'} · ${share.stabilitySeconds}s stability`;
    const healthy = share.enabled && (!storage || storage.exists);

    return `
      <div class="tile">
        <div class="tile-head">
          <div class="tile-name">${escapeHtml(share.name)}</div>
          ${isDestination
            ? '<span class="pill" style="font-size:10.5px;padding:2px 7px">Destination</span>'
            : `<span class="dot dot-sm ${healthy ? 'dot-acc' : 'dot-amb'}"></span>`}
        </div>
        <div class="tile-path">${escapeHtml(share.path)}</div>
        <div class="tile-status">${share.enabled ? escapeHtml(detail) : 'disabled'}</div>
      </div>`;
  }).join('');
}

function renderRecentOps() {
  const target = $('ovRecentOps');
  if (!operations.length) {
    target.innerHTML = '<div style="font-size:12.5px;color:var(--mut)">Nothing has run yet.</div>';
    return;
  }
  target.innerHTML = operations.slice(0, 5).map(operation => `
    <div class="ledger-row">
      <span title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</span>
      <span>${escapeHtml(operation.state)}</span>
    </div>`).join('');
}

/* Onboarding + setup wizard -------------------------------------------- */

const SETUP_STEPS = () => [
  { label: 'Safety reviewed', done: true, view: 'settings' },
  { label: shares.length ? `${shares.length} share${shares.length === 1 ? '' : 's'} added` : 'Add folders', done: shares.length > 0, view: 'shares' },
  { label: groups.length ? `${groups.length} group${groups.length === 1 ? '' : 's'}` : 'Group the phones', done: groups.length > 0, view: 'groups' },
  { label: appInfo.dryRun === false ? 'Live mode enabled' : 'Verify, then go Live →', done: appInfo.dryRun === false, view: 'setup' }
];

function renderOnboarding() {
  const panel = $('onboardingPanel');
  const steps = SETUP_STEPS();
  const remaining = steps.filter(x => !x.done).length;
  const dismissed = localStorage.getItem('momentferry.onboarding.dismissed') === 'true';

  panel.classList.toggle('hidden', remaining === 0 || dismissed);
  if (remaining === 0 || dismissed) return;

  $('onboardingSummary').textContent =
    `${steps.length - remaining} of ${steps.length} steps done. MomentFerry stays in Dry Run until you say otherwise.`;

  $('onboardingSteps').innerHTML = steps.map((step, index) => `
    <button class="guide-step ${step.done ? '' : 'is-todo'}" type="button" data-view="${step.view}">
      <div class="guide-state">${step.done ? 'Done' : `Step ${index + 1}`}</div>
      <div class="guide-label"${step.done ? '' : ' style="font-weight:500"'}>${escapeHtml(step.label)}</div>
    </button>`).join('');
}

function renderSetup() {
  const steps = [
    { label: 'Review safety', done: true },
    { label: 'Add folders', done: shares.length > 0 },
    { label: 'Group the phones', done: groups.length > 0 },
    { label: 'Verify and go Live', done: appInfo.dryRun === false }
  ];
  const currentIndex = steps.findIndex(x => !x.done);

  $('setupSteps').innerHTML = steps.map((step, index) => {
    const isCurrent = index === currentIndex;
    return `
      <div class="wizard-step ${isCurrent ? 'is-current' : ''}">
        <span class="wizard-marker ${step.done ? 'is-done' : (isCurrent ? 'is-current' : '')}">${step.done ? '✓' : index + 1}</span>
        <span>${escapeHtml(step.label)}</span>
      </div>`;
  }).join('');

  const destination = (storageInfo?.items || [])[0];
  const sampleEvent = activeEvent();
  const checks = [
    {
      name: 'Capture times look right',
      detail: 'Run a routing preview on a source share to confirm the capture times MomentFerry reads.',
      ok: operations.length > 0 || events.length > 0,
      mono: false
    },
    {
      name: 'Destination path looks right',
      detail: sampleEvent && destination
        ? destinationPathFor(sampleEvent, destination)
        : 'No event and destination pair configured yet.',
      ok: Boolean(sampleEvent && destination),
      mono: true
    },
    {
      name: 'There is room',
      detail: destination && destination.exists && destination.availableFreeSpaceBytes != null
        ? `${formatBytes(destination.availableFreeSpaceBytes)} free · ${formatBytes(storageInfo.minimumFreeSpaceReserveBytes)} reserve untouched`
        : 'Destination free space is unknown.',
      ok: Boolean(destination && destination.exists && !destination.belowReserve),
      mono: false
    }
  ];

  $('setupChecks').innerHTML = checks.map(check => `
    <div class="check-row">
      <div>
        <div class="setting-name">${escapeHtml(check.name)}</div>
        <div class="setting-desc${check.mono ? ' mono' : ''}">${escapeHtml(check.detail)}</div>
      </div>
      <span class="pill ${check.ok ? 'pill-acc' : 'pill-amb'}">${check.ok ? 'Checked' : 'Review'}</span>
    </div>`).join('');

  $('setupEnableLive').classList.toggle('hidden', appInfo.dryRun === false);
}

/* Shares --------------------------------------------------------------- */

function renderPresets() {
  $('preset').innerHTML = '<option value="">Plain folder / custom</option>' + presets
    .map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.displayName)}</option>`)
    .join('');
}

function renderShares() {
  const list = $('shareList');
  if (!shares.length) {
    list.innerHTML = '<div class="empty"><strong>No shares yet</strong>Add the folders your sync tool fills, plus one destination folder.</div>';
    return;
  }

  list.innerHTML = shares.map(share => `
    <article class="list-row">
      <div class="list-main">
        <div class="list-heading">
          <span class="list-title">${escapeHtml(share.name)}</span>
          <span class="pill">${escapeHtml(share.role)}</span>
          ${share.preset ? `<span style="font-size:11px;color:var(--dim)">${escapeHtml(share.preset)}</span>` : ''}
          ${share.enabled ? '' : '<span class="pill pill-amb">Disabled</span>'}
        </div>
        <div class="list-path">${escapeHtml(share.path)}</div>
        <div class="list-meta">
          ${share.owner ? `${escapeHtml(share.owner)} · ` : ''}
          ${(share.allowedMediaTypes || []).join(' and ') || 'no media types'} ·
          ${share.recursive ? 'subfolders' : 'top-level'} ·
          ${share.stabilitySeconds}s stability
        </div>
        <div class="list-meta" id="state-${escapeHtml(share.id)}"></div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-ghost" type="button" onclick="probeShare('${share.id}')">Test</button>
        ${share.role !== 'Destination' ? `<button class="btn btn-sm btn-ghost" type="button" onclick="scanShare('${share.id}')">Scan</button>` : ''}
        ${share.role !== 'Destination' ? `<button class="btn btn-sm btn-ghost" type="button" onclick="metadataPreview('${share.id}')">Metadata</button>` : ''}
        <button class="btn btn-sm" type="button" onclick="editShare('${share.id}')">Edit</button>
        <button class="btn btn-sm btn-danger" type="button" onclick="deleteShare('${share.id}')">Remove</button>
      </div>
    </article>`).join('');
}

/* Groups --------------------------------------------------------------- */

function renderGroupChoices(selected = []) {
  const sourceShares = shares.filter(x => x.enabled && x.role !== 'Destination');
  $('groupShareChoices').innerHTML = sourceShares.length
    ? sourceShares.map(share => `
        <label><input type="checkbox" name="groupShare" value="${share.id}" ${selected.includes(share.id) ? 'checked' : ''} />${escapeHtml(share.name)}</label>
      `).join('')
    : '<span class="subtle">Create at least one source share first.</span>';
}

function renderGroups() {
  const list = $('groupList');
  if (!groups.length) {
    list.innerHTML = '<div class="empty"><strong>No source groups yet</strong>A group is just a set of phones or cameras that feed one event. Make one per family, per project, or per trip.</div>';
    return;
  }

  list.innerHTML = groups.map(group => {
    const members = group.shareIds.map(id => shares.find(x => x.id === id)).filter(Boolean);
    const usedBy = events.filter(x => x.sourceGroupId === group.id).length;
    return `
      <article class="card">
        <div class="card-head" style="align-items:flex-start;margin-bottom:14px">
          <div>
            <div class="list-title list-title-lg" style="margin-bottom:4px">${escapeHtml(group.name)}</div>
            <div class="card-sub">Used by ${usedBy} event${usedBy === 1 ? '' : 's'} · ${members.length} source share${members.length === 1 ? '' : 's'}</div>
          </div>
          <div class="card-actions">
            <button class="btn btn-sm btn-ghost" type="button" onclick="editGroup('${group.id}')">Edit</button>
            <button class="btn btn-sm btn-danger" type="button" onclick="deleteGroup('${group.id}')">Remove</button>
          </div>
        </div>
        <div class="grid-3" style="gap:10px">
          ${members.length
            ? members.map(share => `
                <div class="tile">
                  <div class="tile-name" style="margin-bottom:3px">${escapeHtml(share.name)}</div>
                  <div class="tile-path" style="margin-bottom:0">${escapeHtml(share.path)}</div>
                </div>`).join('')
            : '<div class="list-meta">No shares in this group yet.</div>'}
        </div>
      </article>`;
  }).join('');
}

/* Events --------------------------------------------------------------- */

function renderEventSelectors() {
  $('eventSourceGroup').innerHTML = groups.length
    ? groups.map(group => `<option value="${group.id}">${escapeHtml(group.name)}</option>`).join('')
    : '<option value="">Create a source group first</option>';

  const destinations = shares.filter(x => x.enabled && x.role !== 'Source');
  $('eventDestination').innerHTML = destinations.length
    ? destinations.map(share => `<option value="${share.id}">${escapeHtml(share.name)} · ${escapeHtml(share.path)}</option>`).join('')
    : '<option value="">Create a destination share first</option>';
}

function eventStatusPill(status) {
  if (status === 'Active') return '<span class="pill pill-acc">Collecting</span>';
  if (status === 'Planned') return '<span class="pill pill-amb">Planned</span>';
  return `<span class="pill">${escapeHtml(status)}</span>`;
}

function renderEvents() {
  const list = $('eventList');
  if (!events.length) {
    list.innerHTML = '<div class="empty"><strong>No events yet</strong>An event is a capture-time window. Anything shot inside it lands in one folder.</div>';
    return;
  }

  list.innerHTML = events.map(event => {
    const groupName = groups.find(x => x.id === event.sourceGroupId)?.name || event.sourceGroupId;
    const destination = shares.find(x => x.id === event.destinationShareId);
    const mode = event.operationMode === 'Copy' ? 'Copy' : 'Safe Move';
    const range = `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : 'still open'}`;
    const routed = operations.filter(o => o.eventId === event.id).length;
    const canStart = event.status !== 'Archived' && event.status !== 'Cancelled';
    const startLabel = event.status === 'Closed' ? 'Reopen' : 'Start';

    return `
      <article class="list-row">
        <div class="list-main">
          <div class="list-heading">
            <span class="list-title list-title-lg">${escapeHtml(event.name)}</span>
            ${eventStatusPill(event.status)}
          </div>
          <div class="list-meta" style="margin-bottom:5px">${escapeHtml(range)} · ${escapeHtml(groupName)} · ${mode}</div>
          <div class="list-path">${escapeHtml(destinationPathFor(event, destination))}</div>
        </div>
        <div class="list-side">
          ${routed ? `<div class="list-count">${formatNumber(routed)}</div><div class="list-meta" style="margin-bottom:10px">files routed</div>` : ''}
          <div class="card-actions">
            <button class="btn btn-sm btn-ghost" type="button" onclick="editEvent('${event.id}')">Edit</button>
            <button class="btn btn-sm btn-ghost" type="button" onclick="backfillEvent('${event.id}')" title="Scan the source shares and route media already captured in this event's window">Sort existing media</button>
            ${event.status === 'Active'
              ? `<button class="btn btn-sm" type="button" onclick="stopEvent('${event.id}')">Stop</button>`
              : (canStart ? `<button class="btn btn-sm" type="button" onclick="startEvent('${event.id}')">${startLabel}</button>` : '')}
            <button class="btn btn-sm btn-danger" type="button" onclick="deleteEvent('${event.id}')">Remove</button>
          </div>
        </div>
      </article>`;
  }).join('');
}

/* Operations ----------------------------------------------------------- */

function renderOperations() {
  const target = $('operationList');
  const header = `
    <div class="th">File</div>
    <div class="th">From</div>
    <div class="th">Stage</div>
    <div class="th right">State</div>`;

  const rows = operations.map(operation => {
    const share = shareForPath(operation.sourcePath);
    const stage = operation.lastError
      ? operation.lastError
      : (operation.destinationPath ? `→ ${operation.destinationPath}` : `started ${formatDate(operation.startedAt)}`);
    return `
      <div class="td strong" title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</div>
      <div class="td">${escapeHtml(share ? share.name : '—')}</div>
      <div class="td">${escapeHtml(stage)}</div>
      <div class="td right">${escapeHtml(operation.state)}</div>`;
  }).join('');

  target.innerHTML = `
    <div class="card card-flush">
      <div class="table-scroll">
        <div class="table table-ops">${header}${rows}</div>
      </div>
      ${operations.length ? '' : '<div class="table-empty">No operations recorded yet.</div>'}
    </div>`;
}

/* Quarantine ------------------------------------------------------------ */

function renderQuarantine() {
  const list = $('quarantineList');
  $('quarantineCount').textContent = quarantinedOperations.length
    ? `${quarantinedOperations.length} item${quarantinedOperations.length === 1 ? '' : 's'}`
    : '';

  if (!quarantinedOperations.length) {
    list.innerHTML = '<div class="divided-row" style="display:block;font-size:12.5px;color:var(--mut)">Nothing is waiting. Every file so far routed cleanly.</div>';
    return;
  }

  list.innerHTML = quarantinedOperations.map(operation => `
    <div class="divided-row">
      <div style="min-width:0">
        <div class="mono" style="font-size:12px;color:var(--txt)" title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</div>
        <div style="font-size:11.5px;color:var(--mut);margin-top:2px">${escapeHtml(operation.lastError || 'No reason recorded.')}</div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-ghost" type="button" ${appInfo.dryRun ? 'disabled title="Dry Run is enabled"' : ''} onclick="retryQuarantine('${operation.id}')">Retry</button>
        <button class="btn btn-sm btn-ghost" type="button" onclick="dismissQuarantine('${operation.id}')">Dismiss safely</button>
      </div>
    </div>`).join('');
}

/* Routing preview -------------------------------------------------------- */

function renderRoutingSources() {
  const sourceShares = shares.filter(x => x.enabled && x.role !== 'Destination');
  $('routingSource').innerHTML = sourceShares.length
    ? sourceShares.map(share => `<option value="${share.id}">${escapeHtml(share.name)} · ${escapeHtml(share.path)}</option>`).join('')
    : '<option value="">No source shares</option>';
}

async function previewRouting() {
  const id = $('routingSource').value;
  if (!id) return;
  const share = shares.find(item => item.id === id);
  $('routingSummary').textContent = '';
  $('routingList').innerHTML = '<div class="empty">Scanning stable files and evaluating events…</div>';

  try {
    const result = await runBackgroundTask(
      `routing-preview:${id}`,
      `Routing preview · ${share?.name || 'source'}`,
      'preview',
      () => request(`/api/v1/shares/${id}/routing-preview?limit=2000`));
    const dry = appInfo.dryRun !== false;

    const rows = result.items.map(item => {
      const event = item.event;
      const canExecute = item.state === 'Matched' && event && !dry;
      const destination = item.destinationPath
        ? escapeHtml(item.destinationPath)
        : (item.message ? escapeHtml(item.message) : 'stays where it is');
      return `
        <div class="td strong" title="${escapeHtml(item.mediaFile.originalName)}">${escapeHtml(item.mediaFile.originalName)}</div>
        <div class="td">${escapeHtml(formatDate(item.mediaFile.capturedAt))}</div>
        <div class="td">${escapeHtml(event ? event.name : item.state)}</div>
        <div class="td mono">
          ${destination}
          ${canExecute ? `<div style="margin-top:6px"><button class="btn btn-sm" type="button" onclick="executeTransfer('${item.mediaFile.id}','${event.id}')">Execute</button></div>` : ''}
        </div>`;
    }).join('');

    $('routingList').innerHTML = `
      <div class="card card-flush">
        <div class="table-summary">
          <span><b>${formatNumber(result.total)}</b> scanned</span>
          <span><b class="acc">${formatNumber(result.matched)}</b> matched an event</span>
          <span><b>${formatNumber(result.unmatched)}</b> outside any event</span>
          <span><b class="amb">${formatNumber(result.ambiguous)}</b> need a decision</span>
        </div>
        <div class="table-scroll">
          <div class="table table-preview">
            <div class="th">File</div>
            <div class="th">Captured</div>
            <div class="th">Match</div>
            <div class="th">Destination</div>
            ${rows}
          </div>
        </div>
        ${result.items.length ? '' : '<div class="table-empty">No stable media files found yet. Scan again after the stability interval.</div>'}
      </div>`;

    $('routingSummary').textContent = dry ? 'Dry Run: nothing here can be moved or deleted.' : '';
  } catch (error) {
    $('routingList').innerHTML = '<div class="empty"><strong>Preview failed</strong>See the message below.</div>';
    $('routingSummary').textContent = error.message;
    $('routingSummary').className = 'message error';
  }
}

/* Share / group / event actions ------------------------------------------ */

window.probeShare = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = 'Testing path…';
  try {
    const result = await runBackgroundTask(
      `share-probe:${id}`,
      `Test path · ${share?.name || 'share'}`,
      'shares',
      () => request(`/api/v1/shares/${id}/probe`));
    state.textContent = result.exists && result.readable
      ? 'Path OK · readable'
      : `Path problem · ${result.error || (result.exists ? 'not readable' : 'not found')}`;
  } catch (error) {
    state.textContent = `Test failed · ${error.message}`;
  }
};

window.scanShare = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = 'Scanning…';
  try {
    const result = await runBackgroundTask(
      `share-scan:${id}`,
      `Scan · ${share?.name || 'source'}`,
      'shares',
      () => request(`/api/v1/shares/${id}/scan?limit=1`));
    state.textContent = `${result.total} media files · ${result.stable} stable · ${result.waitingStable} waiting`;
  } catch (error) {
    state.textContent = `Scan failed · ${error.message}`;
  }
};

window.metadataPreview = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = 'Reading metadata…';
  try {
    const result = await runBackgroundTask(
      `metadata-preview:${id}`,
      `Metadata · ${share?.name || 'source'}`,
      'shares',
      () => request(`/api/v1/shares/${id}/metadata-preview?limit=5`));
    if (!result.items.length) {
      state.textContent = 'No stable media yet. Scan again after the stability interval.';
      return;
    }
    const first = result.items[0];
    const captured = first.metadata.capturedAt || 'no capture time';
    const camera = [first.metadata.cameraMake, first.metadata.cameraModel].filter(Boolean).join(' ');
    const error = first.metadata.error ? ` · ${first.metadata.error}` : '';
    state.textContent = `${result.total} metadata samples · ${captured}${camera ? ` · ${camera}` : ''}${error}`;
  } catch (error) {
    state.textContent = `Metadata failed · ${error.message}`;
  }
};

window.editShare = function (id) {
  const share = shares.find(x => x.id === id);
  if (!share) return;
  $('shareId').value = share.id;
  $('name').value = share.name;
  $('path').value = share.path;
  $('role').value = share.role;
  $('preset').value = share.preset || '';
  $('owner').value = share.owner || '';
  $('timezone').value = share.defaultTimeZone || '';
  $('stability').value = share.stabilitySeconds;
  $('ignore').value = (share.ignorePatterns || []).join('\n');
  $('enabled').checked = share.enabled;
  $('recursive').checked = share.recursive;
  $('images').checked = (share.allowedMediaTypes || []).includes('Image');
  $('videos').checked = (share.allowedMediaTypes || []).includes('Video');
  $('imageExtensions').value = ((share.imageExtensions || []).length ? share.imageExtensions : DEFAULT_IMAGE_EXTENSIONS).join('\n');
  $('videoExtensions').value = ((share.videoExtensions || []).length ? share.videoExtensions : DEFAULT_VIDEO_EXTENSIONS).join('\n');
  $('sharePreset').value = share.renamePresetId || '';
  $('imageSubfolder').value = share.imageSubfolder || '';
  $('videoSubfolder').value = share.videoSubfolder || '';
  syncShareRoleFields();
  $('formTitle').textContent = `Edit ${share.name}`;
  openForm('shares', 'shareFormPanel');
};

window.deleteShare = async function (id) {
  const share = shares.find(x => x.id === id);
  if (!share || !confirm(`Remove share “${share.name}”? No media files are deleted.`)) return;
  try {
    await request(`/api/v1/shares/${id}`, { method: 'DELETE' });
    await reloadConfiguration();
  } catch (error) {
    alert(error.message);
  }
};

window.editGroup = function (id) {
  const group = groups.find(x => x.id === id);
  if (!group) return;
  $('groupId').value = group.id;
  $('groupName').value = group.name;
  $('groupFormTitle').textContent = `Edit ${group.name}`;
  renderGroupChoices(group.shareIds);
  openForm('groups', 'groupFormPanel');
};

window.deleteGroup = async function (id) {
  const group = groups.find(x => x.id === id);
  if (!group || !confirm(`Remove source group “${group.name}”?`)) return;
  try {
    await request(`/api/v1/source-groups/${id}`, { method: 'DELETE' });
    await reloadConfiguration();
  } catch (error) {
    alert(error.message);
  }
};

window.editEvent = function (id) {
  const event = events.find(x => x.id === id);
  if (!event) return;
  $('eventId').value = event.id;
  $('eventName').value = event.name;
  $('eventType').value = event.type || '';
  $('eventSourceGroup').value = event.sourceGroupId;
  $('eventDestination').value = event.destinationShareId;
  $('eventStart').value = toLocalInput(event.startAt);
  $('eventEnd').value = event.endAt ? toLocalInput(event.endAt) : '';
  $('eventStatus').value = event.status;
  setOperationMode(event.operationMode);
  $('eventConflict').value = event.conflictStrategy;
  $('eventDuplicate').value = event.duplicateStrategy;
  $('eventTemplate').value = event.destinationFolderTemplate;
  $('eventFormTitle').textContent = `Edit ${event.name}`;
  openForm('events', 'eventFormPanel');
};

window.startEvent = async function (id) {
  try {
    await request(`/api/v1/events/${id}/start`, { method: 'POST' });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.stopEvent = async function (id) {
  try {
    await request(`/api/v1/events/${id}/stop`, { method: 'POST' });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.backfillEvent = async function (id) {
  const event = events.find(x => x.id === id);
  if (!event) return;

  const range = `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : 'still open'}`;
  const mode = event.operationMode === 'Copy' ? 'copy' : 'safe-move';
  if (!confirm(
    `Sort existing media into “${event.name}”?\n\n` +
    `MomentFerry will scan every source share of this event, read capture metadata for files it has ` +
    `not indexed yet, and ${mode} everything captured in ${range}.\n\n` +
    `Media matching other events is left alone. On a large share the metadata pass can take a while.`)) {
    return;
  }

  const key = `backfill-${id}`;
  try {
    await runBackgroundTask(key, `Backfill: ${event.name}`, 'events', async () => {
      const started = await request(`/api/v1/events/${id}/backfill`, { method: 'POST' });
      const summary = await monitorAutomationCycle(started.requestedAt, key, 'Backfill');
      const routed = appInfo.dryRun !== false
        ? `${formatNumber(summary.wouldMove)} would be routed (Dry Run)`
        : `${formatNumber(summary.executed)} routed`;
      alert(
        `Backfill finished for “${event.name}”.\n\n` +
        `${formatNumber(summary.matched)} matched · ${routed}` +
        (summary.errors ? ` · ${formatNumber(summary.errors)} errors` : ''));
    });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.deleteEvent = async function (id) {
  const event = events.find(x => x.id === id);
  if (!event || !confirm(`Remove event “${event.name}”?`)) return;
  try {
    await request(`/api/v1/events/${id}`, { method: 'DELETE' });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.executeTransfer = async function (mediaFileId, eventId) {
  const event = events.find(x => x.id === eventId);
  const action = event?.operationMode === 'Copy'
    ? 'copy this media file to the verified destination'
    : 'safe-move this media file; the source is only deleted after destination SHA-256 verification';
  if (!confirm(`MomentFerry will ${action}. Continue?`)) return;

  try {
    const result = await runBackgroundTask(
      `transfer:${mediaFileId}`,
      `Transfer · ${event?.name || 'event'}`,
      'ops',
      () => request('/api/v1/transfers', {
        method: 'POST',
        body: JSON.stringify({ mediaFileId, eventId })
      }));
    alert(result.message || `Transfer finished: ${result.operation.state}`);
    await refreshOperations();
    await previewRouting();
  } catch (error) {
    alert(error.message);
  }
};

window.dismissQuarantine = async function (id) {
  const resolutionNote = prompt('Describe how this held operation was resolved. The source file will not be deleted.');
  if (resolutionNote === null) return;
  try {
    await request(`/api/v1/quarantine/${id}/dismiss`, {
      method: 'POST',
      body: JSON.stringify({ resolutionNote })
    });
    $('quarantineMessage').textContent = 'Item dismissed. Source preserved.';
    await Promise.all([refreshQuarantine(), refreshOperations()]);
  } catch (error) {
    $('quarantineMessage').textContent = error.message;
  }
};

window.retryQuarantine = async function (id) {
  if (!confirm('Retry this held transfer from the preserved source file?')) return;
  try {
    await runBackgroundTask(
      `quarantine-retry:${id}`,
      'Retry held transfer',
      'ops',
      () => request(`/api/v1/operations/${id}/retry`, { method: 'POST' }));
    $('quarantineMessage').textContent = 'Held transfer retried.';
    await Promise.all([refreshQuarantine(), refreshOperations()]);
  } catch (error) {
    $('quarantineMessage').textContent = error.message;
  }
};

/* Refresh helpers -------------------------------------------------------- */

async function refreshOperations() {
  operations = await request('/api/v1/operations?limit=50');
  renderOperations();
  renderRecentOps();
  renderEvents();
}

async function refreshQuarantine() {
  quarantinedOperations = await request('/api/v1/quarantine?limit=200');
  renderQuarantine();
  renderRunningEvent();
}

async function reloadConfiguration() {
  [shares, groups, events] = await Promise.all([
    request('/api/v1/shares'),
    request('/api/v1/source-groups/'),
    request('/api/v1/events/')
  ]);
  renderBadges();
  renderShares();
  renderGroupChoices();
  renderGroups();
  renderEventSelectors();
  renderEvents();
  renderRoutingSources();
  renderOnboarding();
  renderOverview();
  await reloadRenaming();
}

async function reloadEvents() {
  events = await request('/api/v1/events/');
  renderBadges();
  renderEvents();
  renderOnboarding();
  renderOverview();
}

/* Forms ------------------------------------------------------------------ */

function openForm(view, panelId) {
  setView(view);
  $(panelId).classList.remove('hidden');
  $(panelId).scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function closeForm(panelId) { $(panelId).classList.add('hidden'); }

function applyPreset() {
  const preset = presets.find(p => p.id === $('preset').value);
  if (!preset) return;
  $('stability').value = preset.stabilitySeconds;
  $('ignore').value = (preset.ignorePatterns || []).join('\n');
}

async function loadFolderTree() {
  const tree = $('folderTree');
  tree.innerHTML = '<div class="subtle">Loading mounted folders…</div>';
  try {
    const result = await request(`/api/v1/folders?role=${encodeURIComponent($('role').value)}`);
    tree.innerHTML = result.roots.length
      ? result.roots.map(root => `
          <div class="folder-root">
            <div class="folder-root-label">${escapeHtml(root.path)}</div>
            <div class="folder-children">${renderFolderNodes(root.folders, 0)}</div>
          </div>`).join('')
      : '<div class="message">No mounted folders found for this role.</div>';
  } catch (error) {
    tree.innerHTML = `<div class="message error">${escapeHtml(error.message)}</div>`;
  }
}

function renderFolderNodes(folders, depth) {
  if (!folders.length) return '<div class="subtle" style="font-size:12px">No subfolders.</div>';
  return folders.map(folder => `
    <div class="folder-node" data-path="${escapeHtml(folder.path)}">
      <div class="folder-row" style="--depth:${depth}">
        <button type="button" class="btn folder-toggle${folder.hasChildren ? '' : ' placeholder'}" ${folder.hasChildren ? 'aria-expanded="false" aria-label="Expand folder"' : 'disabled aria-hidden="true"'}>${folder.hasChildren ? '›' : '·'}</button>
        <button type="button" class="folder-select${$('path').value === folder.path ? ' selected' : ''}" data-folder-select="${escapeHtml(folder.path)}">
          <span class="folder-name">${escapeHtml(folder.name)}</span>
          <span class="folder-path">${escapeHtml(folder.path)}</span>
        </button>
      </div>
      <div class="folder-children"></div>
    </div>`).join('');
}

async function toggleFolder(button) {
  const node = button.closest('.folder-node');
  const children = node.querySelector(':scope > .folder-children');
  const expanded = button.getAttribute('aria-expanded') === 'true';
  if (expanded) {
    children.innerHTML = '';
    button.setAttribute('aria-expanded', 'false');
    button.setAttribute('aria-label', 'Expand folder');
    button.textContent = '›';
    return;
  }

  button.disabled = true;
  try {
    const result = await request(`/api/v1/folders?role=${encodeURIComponent($('role').value)}&path=${encodeURIComponent(node.dataset.path)}`);
    children.innerHTML = renderFolderNodes(result.folders, Number(node.querySelector('.folder-row').style.getPropertyValue('--depth')) + 1);
    button.setAttribute('aria-expanded', 'true');
    button.setAttribute('aria-label', 'Collapse folder');
    button.textContent = '⌄';
  } catch (error) {
    children.innerHTML = `<div class="message error">${escapeHtml(error.message)}</div>`;
  } finally {
    button.disabled = false;
  }
}

const DEFAULT_IMAGE_EXTENSIONS = [
  '.jpg', '.jpeg', '.png', '.heic', '.heif', '.webp', '.gif', '.tif', '.tiff',
  '.dng', '.arw', '.cr2', '.cr3', '.nef', '.raf'
];

const DEFAULT_VIDEO_EXTENSIONS = [
  '.mp4', '.mov', '.m4v', '.avi', '.mkv', '.3gp', '.webm', '.mts', '.m2ts'
];

// The share form serves every role; only show the fields the selected role actually uses.
function syncShareRoleFields() {
  const role = $('role').value;
  const isSource = role === 'Source' || role === 'Both';
  const isDestination = role === 'Destination' || role === 'Both';
  $('sourceExtensionFields').classList.toggle('hidden', !isSource);
  $('destinationSubfolderFields').classList.toggle('hidden', !isDestination);
  $('subfolderHint').classList.toggle('hidden', !isDestination);
}

function linesToList(value) {
  return value.split('\n').map(x => x.trim()).filter(Boolean);
}

function resetShareForm() {
  $('shareForm').reset();
  $('shareId').value = '';
  $('formTitle').textContent = 'Add share';
  $('stability').value = 30;
  $('enabled').checked = true;
  $('recursive').checked = true;
  $('images').checked = true;
  $('videos').checked = true;
  $('imageExtensions').value = DEFAULT_IMAGE_EXTENSIONS.join('\n');
  $('videoExtensions').value = DEFAULT_VIDEO_EXTENSIONS.join('\n');
  $('imageSubfolder').value = '';
  $('videoSubfolder').value = '';
  $('sharePreset').value = '';
  syncShareRoleFields();
  $('formMessage').textContent = '';
  $('folderBrowser').classList.add('hidden');
  $('browseFolders').setAttribute('aria-expanded', 'false');
}

function resetGroupForm() {
  $('groupForm').reset();
  $('groupId').value = '';
  $('groupFormTitle').textContent = 'Add source group';
  $('groupMessage').textContent = '';
  renderGroupChoices();
}

function resetEventForm() {
  $('eventForm').reset();
  $('eventId').value = '';
  $('eventFormTitle').textContent = 'New event';
  $('eventType').value = 'Vacation';
  $('eventStart').value = toLocalInput(new Date().toISOString());
  $('eventStatus').value = 'Planned';
  setOperationMode('SafeMove');
  $('eventConflict').value = 'AppendSourceName';
  $('eventDuplicate').value = 'SafeMoveToExisting';
  $('eventTemplate').value = '{event.name}';
  $('eventMessage').textContent = '';
  renderEventSelectors();
}

$('shareForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const mediaTypes = [];
  if ($('images').checked) mediaTypes.push('Image');
  if ($('videos').checked) mediaTypes.push('Video');

  const body = {
    name: $('name').value,
    path: $('path').value,
    role: $('role').value,
    enabled: $('enabled').checked,
    owner: $('owner').value || null,
    group: null,
    preset: $('preset').value || null,
    stabilitySeconds: Number($('stability').value),
    recursive: $('recursive').checked,
    defaultTimeZone: $('timezone').value || null,
    ignorePatterns: $('ignore').value.split('\n').map(x => x.trim()).filter(Boolean),
    allowedMediaTypes: mediaTypes
  };

  const id = $('shareId').value;
  try {
    await request(id ? `/api/v1/shares/${id}` : '/api/v1/shares', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(body)
    });
    resetShareForm();
    closeForm('shareFormPanel');
    await reloadConfiguration();
  } catch (error) {
    $('formMessage').textContent = error.message;
    $('formMessage').className = 'message error';
  }
});

$('groupForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const shareIds = [...document.querySelectorAll('input[name="groupShare"]:checked')].map(x => x.value);
  const body = { name: $('groupName').value, shareIds };
  const id = $('groupId').value;
  try {
    await request(id ? `/api/v1/source-groups/${id}` : '/api/v1/source-groups/', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(body)
    });
    resetGroupForm();
    closeForm('groupFormPanel');
    await reloadConfiguration();
  } catch (error) {
    $('groupMessage').textContent = error.message;
    $('groupMessage').className = 'message error';
  }
});

$('eventForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const body = {
    name: $('eventName').value,
    type: $('eventType').value || null,
    startAt: fromLocalInput($('eventStart').value),
    endAt: $('eventEnd').value ? fromLocalInput($('eventEnd').value) : null,
    status: $('eventStatus').value,
    sourceGroupId: $('eventSourceGroup').value,
    destinationShareId: $('eventDestination').value,
    destinationFolderTemplate: $('eventTemplate').value,
    operationMode: operationModeValue(),
    conflictStrategy: $('eventConflict').value,
    duplicateStrategy: $('eventDuplicate').value
  };
  const id = $('eventId').value;
  try {
    await request(id ? `/api/v1/events/${id}` : '/api/v1/events/', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(body)
    });
    resetEventForm();
    closeForm('eventFormPanel');
    await reloadEvents();
  } catch (error) {
    $('eventMessage').textContent = error.message;
    $('eventMessage').className = 'message error';
  }
});

/* File naming ---------------------------------------------------------- */

let renamePresets = [];
let cameraMappings = [];
let previewTimer = null;

async function reloadRenaming() {
  [renamePresets, cameraMappings] = await Promise.all([
    request('/api/v1/rename-presets'),
    request('/api/v1/camera-mappings')
  ]);
  renderPresets();
  renderMappings();
  renderPresetChoices();
  const badge = $('badgeRenaming');
  if (badge) badge.textContent = renamePresets.length ? String(renamePresets.length) : '';
}

function renderPresetChoices() {
  const select = $('sharePreset');
  if (!select) return;
  const current = select.value;
  select.innerHTML = `<option value="">No renaming</option>` +
    renamePresets.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('');
  select.value = current;
}

function renderPresets() {
  const list = $('presetList');
  if (!list) return;
  if (!renamePresets.length) {
    list.innerHTML = '<div class="empty"><strong>No presets yet</strong>A preset is a filename template you can attach to a source or a destination.</div>';
    return;
  }

  list.innerHTML = renamePresets.map(preset => {
    const usedBy = shares.filter(s => s.renamePresetId === preset.id).map(s => s.name);
    return `
      <article class="list-row">
        <div class="list-main">
          <div class="list-heading">
            <span class="list-title">${escapeHtml(preset.name)}</span>
          </div>
          <div class="list-path">${escapeHtml(preset.template)}</div>
          <div class="list-meta">${usedBy.length ? `Used by ${escapeHtml(usedBy.join(', '))}` : 'Not attached to a share yet'}</div>
        </div>
        <div class="card-actions">
          <button class="btn btn-sm btn-ghost" type="button" onclick="editPreset('${escapeHtml(preset.id)}')">Edit</button>
          <button class="btn btn-sm btn-ghost" type="button" onclick="tryPreset('${escapeHtml(preset.id)}')">Preview</button>
          <button class="btn btn-sm btn-danger" type="button" onclick="deletePreset('${escapeHtml(preset.id)}')">Remove</button>
        </div>
      </article>`;
  }).join('');
}

function renderMappings() {
  const list = $('mappingList');
  if (!list) return;
  if (!cameraMappings.length) {
    list.innerHTML = '<div class="list-meta">No mappings yet. The reported model is used as-is.</div>';
    return;
  }

  list.innerHTML = cameraMappings.map(mapping => `
    <article class="list-row" style="padding:10px 14px">
      <div class="list-main">
        <div class="mono" style="font-size:12.5px">${escapeHtml(mapping.from)} → <b>${escapeHtml(mapping.to)}</b></div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-danger" type="button" onclick="deleteMapping('${escapeHtml(mapping.id)}')">Remove</button>
      </div>
    </article>`).join('');
}

async function refreshRenamePreview() {
  const target = $('renamePreview');
  if (!target) return;
  const sourceTemplate = $('previewSourceTemplate').value.trim();
  const destinationTemplate = $('previewDestinationTemplate').value.trim();

  if (!sourceTemplate && !destinationTemplate) {
    target.innerHTML = '<div class="list-meta">Enter a template to see how files would be named.</div>';
    return;
  }

  try {
    const result = await request('/api/v1/rename-presets/preview', {
      method: 'POST',
      body: JSON.stringify({ sourceTemplate, destinationTemplate })
    });
    target.innerHTML = result.samples.map(sample => `
      <div class="list-row" style="padding:9px 13px">
        <div class="list-main">
          <div class="mono" style="font-size:12px;color:var(--mut)">${escapeHtml(sample.original)}</div>
          <div class="mono" style="font-size:13px"><b class="acc">${escapeHtml(sample.result)}</b></div>
        </div>
        <div class="list-meta">${sample.camera ? escapeHtml(sample.camera) : 'no camera'}</div>
      </div>`).join('');
  } catch (error) {
    target.innerHTML = `<div class="message error">${escapeHtml(error.message)}</div>`;
  }
}

function schedulePreview() {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(refreshRenamePreview, 250);
}

window.editPreset = function (id) {
  const preset = renamePresets.find(x => x.id === id);
  if (!preset) return;
  $('presetId').value = preset.id;
  $('presetName').value = preset.name;
  $('presetTemplate').value = preset.template;
  $('presetFormTitle').textContent = `Edit ${preset.name}`;
  openForm('renaming', 'presetFormPanel');
};

window.tryPreset = function (id) {
  const preset = renamePresets.find(x => x.id === id);
  if (!preset) return;
  $('previewDestinationTemplate').value = preset.template;
  refreshRenamePreview();
  $('renamePreview').scrollIntoView({ behavior: 'smooth', block: 'center' });
};

window.deletePreset = async function (id) {
  const preset = renamePresets.find(x => x.id === id);
  const usedBy = shares.filter(s => s.renamePresetId === id).map(s => s.name);
  const warning = usedBy.length
    ? `\n\n${usedBy.join(', ')} will stop renaming and keep original filenames.`
    : '';
  if (!preset || !confirm(`Remove preset “${preset.name}”?${warning}`)) return;
  try {
    await request(`/api/v1/rename-presets/${id}`, { method: 'DELETE' });
    await reloadConfiguration();
  } catch (error) {
    alert(error.message);
  }
};

window.deleteMapping = async function (id) {
  try {
    await request(`/api/v1/camera-mappings/${id}`, { method: 'DELETE' });
    await reloadRenaming();
    refreshRenamePreview();
  } catch (error) {
    alert(error.message);
  }
};

function resetPresetForm() {
  $('presetForm').reset();
  $('presetId').value = '';
  $('presetFormTitle').textContent = 'Add preset';
  $('presetMessage').textContent = '';
}

$('newPreset').addEventListener('click', () => {
  resetPresetForm();
  openForm('renaming', 'presetFormPanel');
});
$('cancelPreset').addEventListener('click', () => {
  resetPresetForm();
  closeForm('presetFormPanel');
});
$('previewSourceTemplate').addEventListener('input', schedulePreview);
$('previewDestinationTemplate').addEventListener('input', schedulePreview);

$('presetForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const id = $('presetId').value;
  const body = { name: $('presetName').value, template: $('presetTemplate').value };
  try {
    await request(id ? `/api/v1/rename-presets/${id}` : '/api/v1/rename-presets', {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(body)
    });
    resetPresetForm();
    closeForm('presetFormPanel');
    await reloadConfiguration();
  } catch (error) {
    $('presetMessage').textContent = error.message;
    $('presetMessage').className = 'message error';
  }
});

$('mappingForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const body = { from: $('mappingFrom').value, to: $('mappingTo').value };
  try {
    await request('/api/v1/camera-mappings', { method: 'POST', body: JSON.stringify(body) });
    $('mappingForm').reset();
    $('mappingMessage').textContent = '';
    await reloadRenaming();
    refreshRenamePreview();
  } catch (error) {
    $('mappingMessage').textContent = error.message;
    $('mappingMessage').className = 'message error';
  }
});

/* Wiring ----------------------------------------------------------------- */

$('preset').addEventListener('change', applyPreset);
$('role').addEventListener('change', () => {
  $('path').value = '';
  syncShareRoleFields();
  if (!$('folderBrowser').classList.contains('hidden')) loadFolderTree();
});
$('browseFolders').addEventListener('click', async () => {
  const browser = $('folderBrowser');
  const opening = browser.classList.contains('hidden');
  browser.classList.toggle('hidden');
  $('browseFolders').setAttribute('aria-expanded', String(opening));
  if (opening) await loadFolderTree();
});
$('folderTree').addEventListener('click', event => {
  const select = event.target.closest('[data-folder-select]');
  if (select) {
    $('path').value = select.dataset.folderSelect;
    if (!$('name').value.trim()) $('name').value = select.querySelector('.folder-name').textContent;
    $('folderTree').querySelectorAll('.folder-select').forEach(button => button.classList.toggle('selected', button === select));
    return;
  }
  const toggle = event.target.closest('.folder-toggle:not(.placeholder)');
  if (toggle) toggleFolder(toggle);
});

$('newShare').addEventListener('click', () => { resetShareForm(); openForm('shares', 'shareFormPanel'); });
$('cancelEdit').addEventListener('click', () => closeForm('shareFormPanel'));
$('newGroup').addEventListener('click', () => { resetGroupForm(); openForm('groups', 'groupFormPanel'); });
$('cancelGroup').addEventListener('click', () => closeForm('groupFormPanel'));
$('newEvent').addEventListener('click', () => { resetEventForm(); openForm('events', 'eventFormPanel'); });
$('cancelEvent').addEventListener('click', () => closeForm('eventFormPanel'));
$('previewRouting').addEventListener('click', previewRouting);
$('refreshOperations').addEventListener('click', refreshOperations);
$('refreshQuarantine').addEventListener('click', refreshQuarantine);
$('dismissOnboarding').addEventListener('click', () => {
  localStorage.setItem('momentferry.onboarding.dismissed', 'true');
  renderOnboarding();
});

document.addEventListener('click', event => {
  if (event.target.closest('[data-scan-now]')) {
    triggerScanNow();
    return;
  }
  const toggle = event.target.closest('[data-event-toggle]');
  if (!toggle) return;
  const id = toggle.dataset.eventToggle;
  const target = events.find(x => x.id === id);
  if (!target) return;
  if (target.status === 'Active') stopEvent(id); else startEvent(id);
});

/* Boot -------------------------------------------------------------------- */

applyTheme(document.documentElement.dataset.theme === 'light' ? 'light' : 'dark');
setView(location.hash.slice(1) || 'overview');
setInterval(renderNextScanCountdown, 1000);
load();
