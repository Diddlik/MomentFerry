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
let logEntries = [];

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
let routingPreview = null;

const TITLES = () => ({
  overview: [t('Overview'), t('Every watched folder, the running event and anything waiting on you.')],
  events: [t('Events'), t('A capture-time window. Anything shot inside it lands in one folder.')],
  shares: [t('Shares'), t('The folders MomentFerry can see. Your sync tool keeps them filled.')],
  groups: [t('Source groups'), t('Which phones or cameras feed an event.')],
  renaming: [t('File naming'), t('Templates that rename files on their way to the destination.')],
  preview: [t('Routing preview'), t('See where every file would go before a single byte moves.')],
  ops: [t('Operations'), t('Every copy, checksum, commit and deletion, in order.')],
  settings: [t('Automation & safety'), t('How often MomentFerry looks, and what it is allowed to do.')],
  maintenance: [t('Maintenance'), t('Housekeeping for the index and the operation history.')],
  updates: [t('Image updates'), t('Stable releases from GHCR, applied by an isolated companion.')],
  setup: [t('Finish setup'), t('One last check before anything moves.')]
});

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
    .replace(/\{event\.type\}/gi, () => safeSegment(event.type || t('Event')))
    .replace(/\{year\}/gi, valid ? pad(captured.getFullYear(), 4) : '{year}')
    .replace(/\{month\}/gi, valid ? pad(captured.getMonth() + 1, 2) : '{month}')
    .replace(/\{day\}/gi, valid ? pad(captured.getDate(), 2) : '{day}');
}

function destinationPathFor(event, destination) {
  return `${destination ? destination.path : 'destination'}/${destinationFolder(event)}`;
}

/* Display preferences live in this browser, like the interface language. */
const CLOCK_STORAGE_KEY = 'momentferry.clock';
const WEEK_START_STORAGE_KEY = 'momentferry.weekStart';

function readDisplayPreference(key, allowed) {
  try {
    const stored = localStorage.getItem(key);
    if (allowed.includes(stored)) return stored;
  } catch {}
  return 'auto';
}

const CLOCK_PREFERENCE = readDisplayPreference(CLOCK_STORAGE_KEY, ['12', '24']);
const WEEK_START_PREFERENCE = readDisplayPreference(WEEK_START_STORAGE_KEY, ['0', '1']);
const TIME_OPTIONS = CLOCK_PREFERENCE === 'auto' ? {} : { hour12: CLOCK_PREFERENCE === '12' };
const PICKER_HOUR12 = TIME_OPTIONS.hour12
  ?? new Intl.DateTimeFormat(MF_LANG, { hour: 'numeric' }).resolvedOptions().hour12;

// The region decides the week, not the UI language: an English UI in Germany still starts on Monday.
function localeWeekStart() {
  try {
    const locale = new Intl.Locale(navigator.language || MF_LANG);
    const info = locale.getWeekInfo?.() ?? locale.weekInfo;
    if (info) return info.firstDay % 7;
  } catch {}
  return 1;
}

const WEEK_START = WEEK_START_PREFERENCE === 'auto' ? localeWeekStart() : Number(WEEK_START_PREFERENCE);

function formatDate(value) {
  if (!value) return t('unknown');
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? escapeHtml(value) : date.toLocaleString(MF_LANG, TIME_OPTIONS);
}

function formatBytes(value) {
  if (value == null || Number.isNaN(Number(value))) return t('unknown');
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let number = Number(value);
  let index = 0;
  while (number >= 1024 && index < units.length - 1) { number /= 1024; index++; }
  return `${number.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number.toLocaleString(MF_LANG) : '0';
}

function toLocalInput(value) {
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return local.toISOString().slice(0, 16);
}

function fromLocalInput(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) throw new Error(t('Invalid date/time.'));
  return date.toISOString();
}

/* Date/time picker -------------------------------------------------------
   Replaces datetime-local, whose native picker follows the OS locale and honours
   neither the clock nor the week-start preference. The input shows the formatted
   value; data-value holds the local "YYYY-MM-DDTHH:mm" the form submits. */

let pickerInput = null;
let pickerMonth = null;

function pad2(number) {
  return String(number).padStart(2, '0');
}

function setDateTime(id, value) {
  const input = $(id);
  input.dataset.value = value || '';
  input.value = value
    ? new Date(value).toLocaleString(MF_LANG, { dateStyle: 'medium', timeStyle: 'short', ...TIME_OPTIONS })
    : '';
}

function pickerValue() {
  return pickerInput.dataset.value ? new Date(pickerInput.dataset.value) : null;
}

function commitPicker(date) {
  setDateTime(pickerInput.id,
    `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}T${pad2(date.getHours())}:${pad2(date.getMinutes())}`);
}

function renderPicker() {
  const value = pickerValue();
  const year = pickerMonth.getFullYear();
  const month = pickerMonth.getMonth();
  const leading = (new Date(year, month, 1).getDay() - WEEK_START + 7) % 7;
  const today = new Date();
  const isDay = (date, day) => date && date.getFullYear() === year && date.getMonth() === month && date.getDate() === day;
  // 7 January 2024 was a Sunday, so counting on from it yields the weekday names in display order.
  const weekdays = Array.from({ length: 7 }, (_, i) =>
    new Date(2024, 0, 7 + WEEK_START + i).toLocaleDateString(MF_LANG, { weekday: 'short' }));
  const days = Array.from({ length: new Date(year, month + 1, 0).getDate() }, (_, i) => i + 1).map(day =>
    `<button type="button" class="dt-day${isDay(value, day) ? ' is-selected' : ''}${isDay(today, day) ? ' is-today' : ''}" data-day="${day}">${day}</button>`);
  const hours = Array.from({ length: 24 }, (_, hour) => `<option value="${hour}"${value?.getHours() === hour ? ' selected' : ''}>${
    PICKER_HOUR12 ? new Date(2024, 0, 1, hour).toLocaleTimeString(MF_LANG, { hour: 'numeric', hour12: true }) : pad2(hour)}</option>`);
  const minutes = Array.from({ length: 60 }, (_, minute) =>
    `<option value="${minute}"${value?.getMinutes() === minute ? ' selected' : ''}>${pad2(minute)}</option>`);

  $('dtPicker').innerHTML = `
    <div class="dt-head">
      <button type="button" class="btn btn-sm btn-ghost" data-month="-1" aria-label="${t('Previous month')}">‹</button>
      <span>${pickerMonth.toLocaleDateString(MF_LANG, { month: 'long', year: 'numeric' })}</span>
      <button type="button" class="btn btn-sm btn-ghost" data-month="1" aria-label="${t('Next month')}">›</button>
    </div>
    <div class="dt-grid">
      ${weekdays.map(name => `<span class="dt-weekday">${name}</span>`).join('')}
      ${'<span></span>'.repeat(leading)}${days.join('')}
    </div>
    <div class="dt-time">
      <select id="dtHour" aria-label="${t('Hour')}">${hours.join('')}</select>
      <span>:</span>
      <select id="dtMinute" aria-label="${t('Minute')}">${minutes.join('')}</select>
    </div>
    <div class="actions" style="justify-content:flex-end">
      ${'optional' in pickerInput.dataset ? `<button type="button" class="btn btn-sm btn-ghost" data-clear>${t('Clear')}</button>` : ''}
      <button type="button" class="btn btn-sm btn-acc" data-done>${t('Done')}</button>
    </div>`;
}

function openPicker(input) {
  pickerInput = input;
  const shown = pickerValue() || new Date();
  pickerMonth = new Date(shown.getFullYear(), shown.getMonth(), 1);
  renderPicker();
  const picker = $('dtPicker');
  const rect = input.getBoundingClientRect();
  picker.classList.remove('hidden');
  picker.style.top = `${rect.bottom + window.scrollY + 4}px`;
  picker.style.left = `${Math.max(0, Math.min(rect.left, document.documentElement.clientWidth - picker.offsetWidth - 8)) + window.scrollX}px`;
}

function closePicker() {
  $('dtPicker').classList.add('hidden');
  pickerInput?.focus();
  pickerInput = null;
}

document.addEventListener('click', event => {
  const input = event.target.closest?.('input[data-datetime]');
  if (input) openPicker(input);
});

// pointerdown rather than click: a click inside re-renders the picker and detaches its target.
document.addEventListener('pointerdown', event => {
  if (pickerInput && event.target !== pickerInput && !event.target.closest('#dtPicker')) closePicker();
});

document.addEventListener('keydown', event => {
  if (event.target.matches?.('input[data-datetime]') && ['Enter', ' ', 'ArrowDown'].includes(event.key)) {
    event.preventDefault();
    openPicker(event.target);
  } else if (event.key === 'Escape' && pickerInput) {
    closePicker();
  }
});

$('dtPicker').addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.dataset.month) {
    pickerMonth = new Date(pickerMonth.getFullYear(), pickerMonth.getMonth() + Number(button.dataset.month), 1);
    renderPicker();
  } else if (button.dataset.day) {
    commitPicker(new Date(pickerMonth.getFullYear(), pickerMonth.getMonth(), Number(button.dataset.day),
      Number($('dtHour').value), Number($('dtMinute').value)));
    renderPicker();
  } else if ('clear' in button.dataset) {
    setDateTime(pickerInput.id, '');
    closePicker();
  } else {
    closePicker();
  }
});

$('dtPicker').addEventListener('change', () => {
  const value = pickerValue();
  if (!value) return;
  value.setHours(Number($('dtHour').value), Number($('dtMinute').value));
  commitPicker(value);
});

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
    headers: {
      'Content-Type': 'application/json',
      'X-MomentFerry-Request': '1',
      ...(options.headers || {})
    },
    ...options
  });
  if (response.status === 401) {
    const returnUrl = `${location.pathname}${location.search}${location.hash}`;
    location.assign(`/login.html?returnUrl=${encodeURIComponent(returnUrl)}`);
  }
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
    ? t('{{count}} running · you can change views', { count: formatNumber(running) })
    : t('{{count}} finished', { count: formatNumber(tasks.length) });
  $('clearFinishedTasks').classList.toggle('hidden', tasks.every(task => task.state === 'running'));
  $('backgroundTaskList').innerHTML = tasks.slice(0, 6).map(task => {
    const state = task.state === 'running' ? t('Running') : task.state === 'success' ? t('Completed') : t('Failed');
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

  const task = { key, label, view, state: 'running', detail: t('Running'), startedAt: Date.now() };
  backgroundTasks.set(key, task);
  renderBackgroundTasks();
  updateTaskClock();

  task.promise = Promise.resolve().then(action).then(result => {
    task.state = 'success';
    task.detail = t('Completed');
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
  $('themeLabel').textContent = theme === 'light' ? t('Light') : t('Dark');
  try { localStorage.setItem('momentferry.theme', theme); } catch {}
}

$('themeToggle').addEventListener('click', () => {
  applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light');
});

/* Navigation ---------------------------------------------------------- */

function setView(view) {
  const titles = TITLES();
  if (!titles[view]) view = 'overview';
  currentView = view;

  document.querySelectorAll('.view').forEach(section => {
    section.classList.toggle('hidden', section.id !== `view-${view}`);
  });
  document.querySelectorAll('#nav .nav-item').forEach(button => {
    if (button.dataset.view === view) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  });

  const [title, subtitle] = titles[view];
  $('pageTitle').textContent = title;
  $('pageSubtitle').textContent = subtitle;

  if (view === 'setup') renderSetup();
  if (view === 'ops') refreshLogs().catch(() => { });
  if (view === 'maintenance') refreshMaintenance().catch(() => { });
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
  $('status').textContent = dry ? t('Dry Run — nothing is moved') : t('Live — files are moved for real');
  $('modeAction').textContent = dry ? t('Go Live…') : t('Back to Dry Run');
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
    $('status').textContent = t('Offline');
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
        <span>${escapeHtml(t(automationInfo.currentPhase || 'Preparing'))} · ${escapeHtml(automationInfo.currentShareName || t('sources'))}</span>
        <span>${total ? `${formatNumber(processed)} / ${formatNumber(total)} · ${percent}%` : t('starting…')}</span>
      </div>
      <div class="progress-track" role="progressbar" aria-label="${escapeHtml(t('Current automation cycle'))}" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${percent}"><span style="width:${percent}%"></span></div>
    </div>` : '';
  const scanDisabled = cycleRunning || scanRequestedAt || appInfo.automationEnabled === false;

  return `
    ${progress}
    <div class="stat-grid stat-grid-3">
      <div class="stat">
        <div class="stat-value">${formatNumber(matched)}</div>
        <div class="stat-label">${t(cycleRunning ? 'matched so far' : 'matched last cycle')}</div>
      </div>
      <div class="stat">
        <div class="stat-value acc">${formatNumber(moved)}</div>
        <div class="stat-label">${t(dry ? (cycleRunning ? 'would move so far' : 'would move') : 'moved last cycle')}</div>
      </div>
      <button class="stat" type="button" data-view="ops">
        <div class="stat-value amb">${formatNumber(held)}</div>
        <div class="stat-label">${t('held')}</div>
      </button>
    </div>
    <div class="event-scan-row">
      <div>
        <div class="kicker">${t('Automation')}</div>
        <div class="event-scan-time" id="nextScanCountdown"></div>
      </div>
      <button class="btn" type="button" data-scan-now ${scanDisabled ? 'disabled' : ''}>
        ${t(cycleRunning ? 'Scanning…' : scanRequestedAt ? 'Queued…' : 'Scan now')}
      </button>
    </div>`;
}

function eventSummary(event) {
  return {
    groupName: groups.find(x => x.id === event.sourceGroupId)?.name || t('unknown group'),
    destination: shares.find(x => x.id === event.destinationShareId),
    window: `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : t('open')}`,
    mode: event.operationMode === 'Copy' ? t('Copy') : t('Safe Move')
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
          <div class="list-meta">${escapeHtml(info.window)} · ${escapeHtml(info.groupName)} → ${escapeHtml(info.destination?.name || t('missing destination'))} · ${info.mode}</div>
        </div>
        <div class="card-actions">
          <button class="btn btn-sm btn-ghost" type="button" data-event-toggle="${escapeHtml(event.id)}">
            ${t(event.status === 'Active' ? 'Stop' : 'Start')}
          </button>
        </div>
      </div>`;
  }).join('');

  const more = rest > 0
    ? `<button class="btn btn-sm btn-ghost" type="button" data-view="events">${t('Show {{count}} more →', { count: formatNumber(rest) })}</button>`
    : '';

  return `<div class="stack" style="gap:8px;margin-bottom:14px">${rows}${more}</div>`;
}

function eventHeadline(event) {
  const info = eventSummary(event);
  return `
    <div class="row" style="align-items:baseline;gap:10px;margin-bottom:3px">
      <div style="font-size:24px;font-weight:600;letter-spacing:-.02em">${escapeHtml(event.name)}</div>
      <div style="font-size:12.5px;color:var(--mut)">${escapeHtml(event.type || t('Event'))} · ${info.mode}</div>
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
    kicker.textContent = t('Running event');
    state.className = 'pill';
    state.textContent = t('None');
    body.innerHTML = `
      <div style="font-size:13px;color:var(--mut);line-height:1.6">
        ${t('No event yet. An event is a capture-time window — everything shot inside it lands in one folder.')}
      </div>
      <div class="actions" style="margin-top:14px">
        <button class="btn btn-acc" type="button" data-view="events">${t('Create an event')}</button>
      </div>`;
    return;
  }

  const multiple = list.length > 1;
  const collecting = list[0].status === 'Active';
  kicker.textContent = t(multiple ? 'Running events' : 'Running event');
  state.className = collecting ? 'pill pill-acc' : 'pill';
  state.textContent = multiple
    ? `${formatNumber(list.length)} ${collecting ? t('active') : t(list[0].status).toLowerCase()}`
    : t(collecting ? 'Active' : list[0].status);

  const toggle = multiple
    ? ''
    : `<button class="btn btn-acc" type="button" data-event-toggle="${escapeHtml(list[0].id)}">
         ${t(collecting ? 'Stop event' : 'Start event')}
       </button>`;

  body.innerHTML = `
    ${multiple ? eventRowList(list) : eventHeadline(list[0])}
    ${automationBlock()}
    <div class="actions" style="margin-top:14px">
      ${toggle}
      <button class="btn btn-ghost" type="button" data-view="preview">${t('Preview routing')}</button>
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
    target.textContent = t('Scan in progress');
    return;
  }
  if (scanRequestedAt) {
    target.textContent = t('Manual scan queued');
    return;
  }
  if (appInfo.automationEnabled === false) {
    target.textContent = t('Automation is off');
    return;
  }
  if (!automationInfo?.lastCycleCompletedAt) {
    target.textContent = t('First scan pending');
    return;
  }

  const next = new Date(automationInfo.lastCycleCompletedAt).getTime()
    + Number(appInfo.reconciliationIntervalSeconds || 300) * 1000;
  const seconds = Math.max(0, Math.ceil((next - Date.now()) / 1000));
  const result = manualScanResult
    ? t('Manual scan completed {{time}} · {{matched}} matched · {{wouldMove}} would move', {
      time: new Date(manualScanResult.completedAt).toLocaleTimeString(MF_LANG, TIME_OPTIONS),
      matched: formatNumber(manualScanResult.matched),
      wouldMove: formatNumber(manualScanResult.wouldMove)
    }) + (manualScanResult.errors ? ` · ${t(manualScanResult.errors === 1 ? '{{count}} error' : '{{count}} errors', { count: formatNumber(manualScanResult.errors) })}` : '') + ' · '
    : '';
  if (!seconds) {
    target.textContent = `${result}${t('Next scan due now')}`;
    return;
  }
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor(seconds % 3600 / 60);
  const remainder = seconds % 60;
  const clock = `${hours ? `${hours}:` : ''}${String(minutes).padStart(hours ? 2 : 1, '0')}:${String(remainder).padStart(2, '0')}`;
  target.textContent = `${result}${t('Next scan in {{clock}}', { clock })}`;
  target.title = t('Scheduled for {{time}}', { time: new Date(next).toLocaleString(MF_LANG, TIME_OPTIONS) });
}

async function triggerScanNow() {
  if (automationInfo?.cycleRunning || scanRequestedAt || appInfo.automationEnabled === false) return;
  scanScheduleError = '';
  manualScanResult = null;
  scanRequestedAt = new Date().toISOString();
  renderRunningEvent();
  try {
    await runBackgroundTask('manual-scan', t('Manual scan'), 'overview', async () => {
      const result = await request('/api/v1/automation/run', { method: 'POST' });
      scanRequestedAt = result.requestedAt;
      await monitorManualScan(result.requestedAt);
    });
  } catch (error) {
    scanRequestedAt = null;
    scanScheduleError = t('Could not start scan · {{error}}', { error: error.message });
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
        ? `${t(automationInfo.currentPhase || phaseFallback)} · ${total ? `${formatNumber(processed)} / ${formatNumber(total)} · ${percent}%` : t('starting…')}`
        : t('Queued');
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

  throw new Error(t('Timed out waiting for the cycle to finish'));
}

async function monitorManualScan(requestedAt) {
  manualScanResult = await monitorAutomationCycle(requestedAt, 'manual-scan', t('Scanning'));
  scanRequestedAt = null;
  scanScheduleError = '';
  renderOverview();
}

function renderStorage() {
  const target = $('ovStorage');
  if (!storageInfo) {
    target.innerHTML = `<div class="message">${t('Loading storage…')}</div>`;
    return;
  }

  const items = storageInfo.items || [];
  if (!items.length) {
    target.innerHTML = `
      <div style="font-size:13px;color:var(--mut);line-height:1.6">
        ${t('No destination share configured yet, so there is nowhere for MomentFerry to put anything.')}
      </div>
      <div class="actions" style="margin-top:14px">
        <button class="btn btn-acc" type="button" data-view="shares">${t('Add a destination')}</button>
      </div>`;
    return;
  }

  const primary = items.find(x => x.exists && x.availableFreeSpaceBytes != null) || items[0];
  const reserve = storageInfo.minimumFreeSpaceReserveBytes || 0;

  if (!primary.exists) {
    target.innerHTML = `
      <div style="font-size:15px;font-weight:600;color:var(--red);margin-bottom:6px">${t('Path missing')}</div>
      <div style="font-size:12.5px;color:var(--mut)">${escapeHtml(primary.error || t('MomentFerry cannot see this folder inside the container.'))}</div>
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
      <span style="font-size:14px;color:var(--mut)">${t('free on {{name}}', { name: escapeHtml(primary.name) })}</span>
    </div>
    <div class="meter" title="${escapeHtml(t('Usable free space versus the reserve MomentFerry holds back'))}">
      <div class="meter-used" style="width:${(100 - reservePercent).toFixed(2)}%"></div>
      <div class="meter-reserve" style="width:${reservePercent.toFixed(2)}%"></div>
    </div>
    <div style="font-size:11.5px;color:var(--mut);line-height:1.5">
      ${t('{{size}} is always held back on top of each file.', { size: escapeHtml(formatBytes(reserve)) })}
      ${belowReserve
        ? `<span style="color:var(--amb)">${t('Free space is below that reserve — transfers will hold.')}</span>`
        : t('There is room for the next transfers.')}
    </div>
    ${others.length ? `<div style="font-size:11.5px;color:var(--mut);margin-top:10px">${others.map(x =>
        `${escapeHtml(x.name)}: ${x.exists ? t('{{size}} free', { size: escapeHtml(formatBytes(x.availableFreeSpaceBytes)) }) : t('path missing')}`
      ).join(' · ')}</div>` : ''}
    <div class="mono" style="margin-top:auto;padding-top:14px;border-top:1px solid var(--line);font-size:11.5px;color:var(--dim)">${escapeHtml(primary.path)}</div>`;
}

function renderSources() {
  const target = $('ovSources');
  if (!shares.length) {
    target.innerHTML = `<div class="empty" style="grid-column:1/-1"><strong>${t('No folders watched yet')}</strong>${t('Add the folders your sync tool fills, plus one destination.')}</div>`;
    return;
  }

  target.innerHTML = shares.map(share => {
    const isDestination = share.role !== 'Source';
    const storage = (storageInfo?.items || []).find(x => x.shareId === share.id);
    const detail = isDestination
      ? (storage
        ? (storage.exists
          ? t('{{size}} free', { size: formatBytes(storage.availableFreeSpaceBytes) }) + (storage.belowReserve ? ` · ${t('below reserve')}` : '')
          : t('path missing'))
        : t('destination'))
      : `${t(share.recursive ? 'subfolders' : 'top-level')} · ${t('{{seconds}}s stability', { seconds: share.stabilitySeconds })}`;
    const healthy = share.enabled && (!storage || storage.exists);

    return `
      <div class="tile">
        <div class="tile-head">
          <div class="tile-name">${escapeHtml(share.name)}</div>
          ${isDestination
            ? `<span class="pill" style="font-size:10.5px;padding:2px 7px">${t('Destination')}</span>`
            : `<span class="dot dot-sm ${healthy ? 'dot-acc' : 'dot-amb'}"></span>`}
        </div>
        <div class="tile-path">${escapeHtml(share.path)}</div>
        <div class="tile-status">${share.enabled ? escapeHtml(detail) : t('disabled')}</div>
      </div>`;
  }).join('');
}

function renderRecentOps() {
  const target = $('ovRecentOps');
  if (!operations.length) {
    target.innerHTML = `<div style="font-size:12.5px;color:var(--mut)">${t('Nothing has run yet.')}</div>`;
    return;
  }
  target.innerHTML = operations.slice(0, 5).map(operation => `
    <div class="ledger-row">
      <span title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</span>
      <span>${escapeHtml(t(operation.state))}</span>
    </div>`).join('');
}

/* Onboarding + setup wizard -------------------------------------------- */

const SETUP_STEPS = () => [
  { label: t('Safety reviewed'), done: true, view: 'settings' },
  {
    label: shares.length
      ? t(shares.length === 1 ? '{{count}} share added' : '{{count}} shares added', { count: formatNumber(shares.length) })
      : t('Add folders'),
    done: shares.length > 0,
    view: 'shares'
  },
  {
    label: groups.length
      ? t(groups.length === 1 ? '{{count}} group' : '{{count}} groups', { count: formatNumber(groups.length) })
      : t('Group the phones'),
    done: groups.length > 0,
    view: 'groups'
  },
  { label: t(appInfo.dryRun === false ? 'Live mode enabled' : 'Verify, then go Live →'), done: appInfo.dryRun === false, view: 'setup' }
];

function renderOnboarding() {
  const panel = $('onboardingPanel');
  const steps = SETUP_STEPS();
  const remaining = steps.filter(x => !x.done).length;
  const dismissed = localStorage.getItem('momentferry.onboarding.dismissed') === 'true';

  panel.classList.toggle('hidden', remaining === 0 || dismissed);
  if (remaining === 0 || dismissed) return;

  $('onboardingSummary').textContent = t(
    '{{done}} of {{total}} steps done. MomentFerry stays in Dry Run until you say otherwise.',
    { done: steps.length - remaining, total: steps.length });

  $('onboardingSteps').innerHTML = steps.map((step, index) => `
    <button class="guide-step ${step.done ? '' : 'is-todo'}" type="button" data-view="${step.view}">
      <div class="guide-state">${step.done ? t('Done') : t('Step {{number}}', { number: index + 1 })}</div>
      <div class="guide-label"${step.done ? '' : ' style="font-weight:500"'}>${escapeHtml(step.label)}</div>
    </button>`).join('');
}

function renderSetup() {
  const steps = [
    { label: t('Review safety'), done: true },
    { label: t('Add folders'), done: shares.length > 0 },
    { label: t('Group the phones'), done: groups.length > 0 },
    { label: t('Verify and go Live'), done: appInfo.dryRun === false }
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
      name: t('Capture times look right'),
      detail: t('Run a routing preview on a source share to confirm the capture times MomentFerry reads.'),
      ok: operations.length > 0 || events.length > 0,
      mono: false
    },
    {
      name: t('Destination path looks right'),
      detail: sampleEvent && destination
        ? destinationPathFor(sampleEvent, destination)
        : t('No event and destination pair configured yet.'),
      ok: Boolean(sampleEvent && destination),
      mono: true
    },
    {
      name: t('There is room'),
      detail: destination && destination.exists && destination.availableFreeSpaceBytes != null
        ? t('{{free}} free · {{reserve}} reserve untouched', {
          free: formatBytes(destination.availableFreeSpaceBytes),
          reserve: formatBytes(storageInfo.minimumFreeSpaceReserveBytes)
        })
        : t('Destination free space is unknown.'),
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
      <span class="pill ${check.ok ? 'pill-acc' : 'pill-amb'}">${t(check.ok ? 'Checked' : 'Review')}</span>
    </div>`).join('');

  $('setupEnableLive').classList.toggle('hidden', appInfo.dryRun === false);
}

/* Shares --------------------------------------------------------------- */

function renderPresets() {
  $('preset').innerHTML = `<option value="">${t('Plain folder / custom')}</option>` + presets
    .map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.displayName)}</option>`)
    .join('');
}

function renderShares() {
  const list = $('shareList');
  if (!shares.length) {
    list.innerHTML = `<div class="empty"><strong>${t('No shares yet')}</strong>${t('Add the folders your sync tool fills, plus one destination folder.')}</div>`;
    return;
  }

  list.innerHTML = shares.map(share => `
    <article class="list-row">
      <div class="list-main">
        <div class="list-heading">
          <span class="list-title">${escapeHtml(share.name)}</span>
          <span class="pill">${escapeHtml(t(share.role))}</span>
          ${share.preset ? `<span style="font-size:11px;color:var(--dim)">${escapeHtml(share.preset)}</span>` : ''}
          ${share.enabled ? '' : `<span class="pill pill-amb">${t('Disabled')}</span>`}
        </div>
        <div class="list-path">${escapeHtml(share.path)}</div>
        <div class="list-meta">
          ${share.owner ? `${escapeHtml(share.owner)} · ` : ''}
          ${(share.allowedMediaTypes || []).map(type => t(type)).join(` ${t('and')} `) || t('no media types')} ·
          ${t(share.recursive ? 'subfolders' : 'top-level')} ·
          ${t('{{seconds}}s stability', { seconds: share.stabilitySeconds })}
        </div>
        <div class="list-meta" id="state-${escapeHtml(share.id)}"></div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-ghost" type="button" onclick="probeShare('${share.id}')">${t('Test')}</button>
        ${share.role !== 'Destination' ? `<button class="btn btn-sm btn-ghost" type="button" onclick="scanShare('${share.id}')">${t('Scan')}</button>` : ''}
        ${share.role !== 'Destination' ? `<button class="btn btn-sm btn-ghost" type="button" onclick="metadataPreview('${share.id}')">${t('Metadata')}</button>` : ''}
        <button class="btn btn-sm" type="button" onclick="editShare('${share.id}')">${t('Edit')}</button>
        <button class="btn btn-sm btn-danger" type="button" onclick="deleteShare('${share.id}')">${t('Remove')}</button>
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
    : `<span class="subtle">${t('Create at least one source share first.')}</span>`;
}

function renderGroups() {
  const list = $('groupList');
  if (!groups.length) {
    list.innerHTML = `<div class="empty"><strong>${t('No source groups yet')}</strong>${t('A group is just a set of phones or cameras that feed one event. Make one per family, per project, or per trip.')}</div>`;
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
            <div class="card-sub">${t('Events using it: {{events}} · Source shares: {{shares}}', { events: usedBy, shares: members.length })}</div>
          </div>
          <div class="card-actions">
            <button class="btn btn-sm btn-ghost" type="button" onclick="editGroup('${group.id}')">${t('Edit')}</button>
            <button class="btn btn-sm btn-danger" type="button" onclick="deleteGroup('${group.id}')">${t('Remove')}</button>
          </div>
        </div>
        <div class="grid-3" style="gap:10px">
          ${members.length
            ? members.map(share => `
                <div class="tile">
                  <div class="tile-name" style="margin-bottom:3px">${escapeHtml(share.name)}</div>
                  <div class="tile-path" style="margin-bottom:0">${escapeHtml(share.path)}</div>
                </div>`).join('')
            : `<div class="list-meta">${t('No shares in this group yet.')}</div>`}
        </div>
      </article>`;
  }).join('');
}

/* Events --------------------------------------------------------------- */

function renderEventSelectors() {
  $('eventSourceGroup').innerHTML = groups.length
    ? groups.map(group => `<option value="${group.id}">${escapeHtml(group.name)}</option>`).join('')
    : `<option value="">${t('Create a source group first')}</option>`;

  const destinations = shares.filter(x => x.enabled && x.role !== 'Source');
  $('eventDestination').innerHTML = destinations.length
    ? destinations.map(share => `<option value="${share.id}">${escapeHtml(share.name)} · ${escapeHtml(share.path)}</option>`).join('')
    : `<option value="">${t('Create a destination share first')}</option>`;
}

function eventStatusPill(status) {
  if (status === 'Active') return `<span class="pill pill-acc">${t('Collecting')}</span>`;
  if (status === 'Planned') return `<span class="pill pill-amb">${t('Planned')}</span>`;
  return `<span class="pill">${escapeHtml(t(status))}</span>`;
}

function renderEvents() {
  const list = $('eventList');
  if (!events.length) {
    list.innerHTML = `<div class="empty"><strong>${t('No events yet')}</strong>${t('An event is a capture-time window. Anything shot inside it lands in one folder.')}</div>`;
    return;
  }

  list.innerHTML = events.map(event => {
    const groupName = groups.find(x => x.id === event.sourceGroupId)?.name || event.sourceGroupId;
    const destination = shares.find(x => x.id === event.destinationShareId);
    const mode = event.operationMode === 'Copy' ? t('Copy') : t('Safe Move');
    const range = `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : t('still open')}`;
    const routed = operations.filter(o => o.eventId === event.id).length;
    const canStart = event.status !== 'Archived' && event.status !== 'Cancelled';
    const startLabel = t(event.status === 'Closed' ? 'Reopen' : 'Start');

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
          ${routed ? `<div class="list-count">${formatNumber(routed)}</div><div class="list-meta" style="margin-bottom:10px">${t('files routed')}</div>` : ''}
          <div class="card-actions">
            <button class="btn btn-sm btn-ghost" type="button" onclick="editEvent('${event.id}')">${t('Edit')}</button>
            <button class="btn btn-sm btn-ghost" type="button" onclick="backfillEvent('${event.id}')" title="${escapeHtml(t("Scan the source shares and route media already captured in this event's window"))}">${t('Sort existing media')}</button>
            ${appInfo.dryRun ? '' : `<button class="btn btn-sm btn-ghost" type="button" onclick="routeEventAgain('${event.id}')" title="${escapeHtml(t('Clear the finished mark on the files of this event and route them all again under the current rules'))}">${t('Route again')}</button>`}
            <button class="btn btn-sm btn-ghost" type="button" onclick="renameRoutedFiles('${event.id}')" title="${escapeHtml(t('Apply the current naming rules to the files this event already stored'))}">${t('Rename stored files')}</button>
            ${event.status === 'Active'
              ? `<button class="btn btn-sm" type="button" onclick="stopEvent('${event.id}')">${t('Stop')}</button>`
              : (canStart ? `<button class="btn btn-sm" type="button" onclick="startEvent('${event.id}')">${startLabel}</button>` : '')}
            <button class="btn btn-sm btn-danger" type="button" onclick="deleteEvent('${event.id}')">${t('Remove')}</button>
          </div>
        </div>
      </article>`;
  }).join('');
}

/* Operations ----------------------------------------------------------- */

function renderOperations() {
  const target = $('operationList');
  const header = `
    <div class="th">${t('File')}</div>
    <div class="th">${t('From')}</div>
    <div class="th">${t('Stage')}</div>
    <div class="th right">${t('State')}</div>`;
  const terminal = new Set(['Completed', 'Ignored']);

  const rows = operations.map(operation => {
    const share = shareForPath(operation.sourcePath);
    const stage = operation.lastError
      ? operation.lastError
      : (operation.destinationPath
        ? `→ ${operation.destinationPath}`
        : t('started {{time}}', { time: formatDate(operation.startedAt) }));
    return `
      <div class="td strong" title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</div>
      <div class="td">${escapeHtml(share ? share.name : '—')}</div>
      <div class="td">${escapeHtml(stage)}</div>
      <div class="td right">${escapeHtml(t(operation.state))}${terminal.has(operation.state) && !appInfo.dryRun
        ? `<button class="btn btn-sm btn-ghost" style="margin-left:8px" type="button" onclick="routeAgain('${operation.id}')">${t('Route again')}</button>`
        : ''}</div>`;
  }).join('');

  target.innerHTML = `
    <div class="card card-flush">
      <div class="table-scroll">
        <div class="table table-ops">${header}${rows}</div>
      </div>
      ${operations.length ? '' : `<div class="table-empty">${t('No operations recorded yet.')}</div>`}
    </div>`;
}

/* Activity log ----------------------------------------------------------- */

function renderLogs() {
  const target = $('logList');
  if (!logEntries.length) {
    target.innerHTML = `<div class="table-empty">${t('Nothing logged yet.')}</div>`;
    return;
  }

  target.innerHTML = logEntries.map(entry => `
    <div class="divided-row">
      <div style="min-width:0">
        <div style="font-size:12.5px;color:var(--txt)">${escapeHtml(entry.message)}</div>
        <div class="mono" style="font-size:11px;color:var(--mut);margin-top:2px">${escapeHtml(formatDate(entry.at))} · ${escapeHtml(entry.level)} · ${escapeHtml(entry.category)}</div>
        ${entry.detail ? `<details class="log-detail"><summary>${t('Error details')}</summary><pre class="changelog">${escapeHtml(entry.detail)}</pre></details>` : ''}
      </div>
    </div>`).join('');
}

async function refreshLogs() {
  logEntries = await request(`/api/v1/logs?limit=200&level=${encodeURIComponent($('logLevel').value)}`);
  renderLogs();
}

/* Maintenance ------------------------------------------------------------ */

let maintenanceInfo = null;

async function refreshMaintenance() {
  maintenanceInfo = await request('/api/v1/maintenance/');
  const settings = await request('/api/v1/settings');
  $('retentionDays').value = settings.operationRetentionDays ?? 0;

  const total = Object.values(maintenanceInfo.operations || {}).reduce((sum, n) => sum + n, 0);
  $('maintenanceSummary').textContent = t(
    '{{size}} database · {{files}} indexed files · {{operations}} operations',
    {
      size: formatBytes(maintenanceInfo.databaseBytes),
      files: formatNumber(maintenanceInfo.indexedMediaFiles),
      operations: formatNumber(total)
    });

  $('maintenanceShare').innerHTML =
    `<option value="">${t('All shares')}</option>` +
    shares.map(share => `<option value="${share.id}">${escapeHtml(share.name)}</option>`).join('');
}

async function runMaintenance(key, label, action) {
  $('maintenanceMessage').textContent = '';
  try {
    const message = await runBackgroundTask(key, label, 'maintenance', action);
    $('maintenanceMessage').textContent = message;
    await refreshMaintenance();
  } catch (error) {
    $('maintenanceMessage').textContent = error.message;
  }
}

/* Quarantine ------------------------------------------------------------ */

function renderQuarantine() {
  const list = $('quarantineList');
  $('quarantineCount').textContent = quarantinedOperations.length
    ? t(quarantinedOperations.length === 1 ? '{{count}} item' : '{{count}} items', { count: formatNumber(quarantinedOperations.length) })
    : '';

  if (!quarantinedOperations.length) {
    list.innerHTML = `<div class="divided-row" style="display:block;font-size:12.5px;color:var(--mut)">${t('Nothing is waiting. Every file so far routed cleanly.')}</div>`;
    return;
  }

  list.innerHTML = quarantinedOperations.map(operation => `
    <div class="divided-row">
      <div style="min-width:0">
        <div class="mono" style="font-size:12px;color:var(--txt)" title="${escapeHtml(operation.sourcePath)}">${escapeHtml(baseName(operation.sourcePath))}</div>
        <div style="font-size:11.5px;color:var(--mut);margin-top:2px">${escapeHtml(t(operation.state))} · ${escapeHtml(operation.lastError || t('No reason recorded.'))}</div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-ghost" type="button" ${appInfo.dryRun ? `disabled title="${escapeHtml(t('Dry Run is enabled'))}"` : ''} onclick="retryQuarantine('${operation.id}')">${t('Retry')}</button>
        ${operation.state === 'Quarantined' ? `<button class="btn btn-sm btn-ghost" type="button" onclick="dismissQuarantine('${operation.id}')">${t('Dismiss safely')}</button>` : ''}
      </div>
    </div>`).join('');
}

/* Routing preview -------------------------------------------------------- */

function renderRoutingSources() {
  const sourceShares = shares.filter(x => x.enabled && x.role !== 'Destination');
  $('routingSource').innerHTML = sourceShares.length
    ? sourceShares.map(share => `<option value="${share.id}">${escapeHtml(share.name)} · ${escapeHtml(share.path)}</option>`).join('')
    : `<option value="">${t('No source shares')}</option>`;
}

// The preview runs as a server-side job: one request would outlast a reverse proxy's timeout.
async function waitForRoutingPreview(shareId, taskKey) {
  let job = await request(`/api/v1/shares/${shareId}/routing-preview`, { method: 'POST' });
  while (!job.completedAt) {
    const task = backgroundTasks.get(taskKey);
    if (task) {
      const progress = job.progress;
      task.detail = progress?.total
        ? `${t(progress.phase)} · ${formatNumber(progress.processed)} / ${formatNumber(progress.total)}`
        : t('starting…');
      renderBackgroundTasks();
    }
    await new Promise(resolve => setTimeout(resolve, 1500));
    job = await request(`/api/v1/routing-preview-jobs/${job.id}`, { cache: 'no-store' });
  }
  if (job.error) throw new Error(t(job.error));
  return job.result;
}

async function previewRouting() {
  const id = $('routingSource').value;
  if (!id) return;
  const share = shares.find(item => item.id === id);
  $('routingSummary').textContent = '';
  $('routingList').innerHTML = `<div class="empty">${t('Scanning stable files and evaluating events…')}</div>`;

  try {
    const result = await runBackgroundTask(
      `routing-preview:${id}`,
      `${t('Routing preview')} · ${share?.name || t('source')}`,
      'preview',
      () => waitForRoutingPreview(id, `routing-preview:${id}`));
    routingPreview = { result, share };
    renderRoutingPreview();
    $('routingSummary').textContent = appInfo.dryRun !== false ? t('Dry Run: nothing here can be moved or deleted.') : '';
  } catch (error) {
    routingPreview = null;
    $('routingList').innerHTML = `<div class="empty"><strong>${t('Preview failed')}</strong>${t('See the message below.')}</div>`;
    $('routingSummary').textContent = error.message;
    $('routingSummary').className = 'message error';
  }
}

function renderRoutingPreview() {
  if (!routingPreview) return;
  const { result } = routingPreview;
  const dry = appInfo.dryRun !== false;
  const query = $('routingFilter').value.trim().toLowerCase();
  const state = $('routingStateFilter').value;
  const visible = result.items
    .map((item, index) => ({ item, index }))
    .filter(({ item }) => (!state || item.state === state) &&
      (!query || item.mediaFile.sourcePath.toLowerCase().includes(query)));

  const rows = visible.map(({ item, index }) => {
    const event = item.event;
    const canExecute = item.state === 'Matched' && event && !dry;
    const destination = item.destinationPath
      ? escapeHtml(item.destinationPath)
      : (item.message ? escapeHtml(item.message) : t('stays where it is'));
    return `
      <div class="td strong" title="${escapeHtml(item.mediaFile.sourcePath)}">
        ${escapeHtml(item.mediaFile.originalName)}
        <div><button class="link-btn" type="button" onclick="openFileAnalysis(${index})">${t('Details')}</button></div>
      </div>
      <div class="td">${escapeHtml(formatDate(item.mediaFile.capturedAt))}</div>
      <div class="td">${escapeHtml(event ? event.name : t(item.state))}</div>
      <div class="td mono">
        ${destination}
        ${canExecute ? `<div style="margin-top:6px"><button class="btn btn-sm" type="button" onclick="executeTransfer('${item.mediaFile.id}','${event.id}')">${t('Execute')}</button></div>` : ''}
      </div>`;
  }).join('');

  $('routingList').innerHTML = `
    <div class="card card-flush">
      <div class="table-summary">
        <span><b>${formatNumber(result.total)}</b> ${t('scanned')}</span>
        <span><b class="acc">${formatNumber(result.matched)}</b> ${t('matched an event')}</span>
        <span><b>${formatNumber(result.unmatched)}</b> ${t('outside any event')}</span>
        <span><b class="amb">${formatNumber(result.ambiguous)}</b> ${t('need a decision')}</span>
        ${visible.length !== result.items.length ? `<span><b>${formatNumber(visible.length)}</b> ${t('shown')}</span>` : ''}
      </div>
      <div class="table-scroll">
        <div class="table table-preview">
          <div class="th">${t('File')}</div>
          <div class="th">${t('Captured')}</div>
          <div class="th">${t('Match')}</div>
          <div class="th">${t('Destination')}</div>
          ${rows}
        </div>
      </div>
      ${result.items.length ? '' : `<div class="table-empty">${t('No stable media files found yet. Scan again after the stability interval.')}</div>`}
      ${result.items.length && !visible.length ? `<div class="table-empty">${t('No file matches the filter.')}</div>` : ''}
    </div>`;
}

/* File analysis -----------------------------------------------------------
   Repeats the checks RoutingPreviewService and MediaRoutingWorker apply, so the
   answer to "why not this event?" is the one the worker would give. */

let analysisItem = null;

function eventChecks(event, mediaFile, share) {
  const captured = new Date(mediaFile.capturedAt);
  const end = event.endAt ? new Date(event.endAt) : null;
  const group = groups.find(x => x.id === event.sourceGroupId);
  const inGroup = Boolean(group?.shareIds.includes(share.id));
  const destination = shares.find(x => x.id === event.destinationShareId);
  const writable = Boolean(destination?.enabled && destination.role !== 'Source');
  const windowText = captured < new Date(event.startAt)
    ? t('Captured {{captured}}, before the event starts at {{start}}.', { captured: formatDate(mediaFile.capturedAt), start: formatDate(event.startAt) })
    : end && captured > end
      ? t('Captured {{captured}}, after the event ends at {{end}}.', { captured: formatDate(mediaFile.capturedAt), end: formatDate(event.endAt) })
      : t('Captured {{captured}}, inside the event window.', { captured: formatDate(mediaFile.capturedAt) });
  return [
    {
      ok: event.status === 'Active' || event.status === 'Closed',
      text: t('Status is {{status}}. Only Active and Closed events take files.', { status: t(event.status) })
    },
    { ok: captured >= new Date(event.startAt) && (!end || captured <= end), text: windowText },
    {
      ok: inGroup,
      text: !group
        ? t("The event's source group no longer exists.")
        : t(inGroup ? '{{share}} contributes through the group {{group}}.' : '{{share}} is not a member of the group {{group}}.', { share: share.name, group: group.name })
    },
    {
      ok: writable,
      text: !destination
        ? t("The event's destination share no longer exists.")
        : t(writable ? 'Destination {{share}} is writable.' : 'Destination {{share}} is disabled or not writable by role.', { share: destination.name })
    }
  ];
}

// Ambiguity is decided before the destination is looked at, so only the first three checks count.
function eventMatches(event, mediaFile, share) {
  return eventChecks(event, mediaFile, share).slice(0, 3).every(check => check.ok);
}

function renderEventAnalysis() {
  const { mediaFile } = analysisItem;
  const { share } = routingPreview;
  const event = events.find(x => x.id === $('analysisEvent').value);
  if (!event) {
    $('analysisChecks').innerHTML = '';
    return;
  }

  const checks = eventChecks(event, mediaFile, share);
  const others = events.filter(x => x.id !== event.id && eventMatches(x, mediaFile, share));
  if (others.length) {
    checks.push({ ok: false, text: t('Also matches {{events}}. A file that matches more than one event is not routed until only one matches.', { events: others.map(x => x.name).join(', ') }) });
  }
  if (mediaFile.timestampSource === 'FileLastWriteTimeUtc' && !appInfo.allowFilesystemTimestampFallback) {
    checks.push({ ok: false, text: t('No capture time in the metadata, and the file-date fallback is off, so automation skips this file.') });
  }

  const takes = checks.every(check => check.ok);
  $('analysisChecks').innerHTML = checks
    .map(check => `<div class="modal-fact"><span>${escapeHtml(check.text)}</span><b class="${check.ok ? '' : 'amb'}">${check.ok ? '✓' : '✕'}</b></div>`)
    .join('')
    + `<div class="setting-desc" style="margin-top:10px">${escapeHtml(t(takes ? 'This event takes the file.' : 'This event does not take the file.'))}</div>`;
}

function formatOffset(minutes) {
  if (minutes == null) return t('none, the share time zone stands in');
  const absolute = Math.abs(minutes);
  return `UTC${minutes < 0 ? '−' : '+'}${pad2(Math.trunc(absolute / 60))}:${pad2(absolute % 60)}`;
}

window.openFileAnalysis = function (index) {
  analysisItem = routingPreview.result.items[index];
  const { mediaFile } = analysisItem;
  const facts = [
    [t('Path'), mediaFile.sourcePath],
    [t('Size'), formatBytes(mediaFile.size)],
    [t('Type'), `${t(mediaFile.mediaType)} · ${mediaFile.extension}`],
    [t('Captured'), mediaFile.capturedAt ? `${formatDate(mediaFile.capturedAt)} · ${new Date(mediaFile.capturedAt).toISOString()}` : t('unknown')],
    [t('Read from'), mediaFile.timestampSource || t('unknown')],
    [t('Reported offset'), formatOffset(mediaFile.capturedAtOffsetMinutes)],
    [t('Time zone inferred'), t(mediaFile.isTimezoneInferred ? 'yes' : 'no')],
    [t('Camera'), [mediaFile.cameraMake, mediaFile.cameraModel].filter(Boolean).join(' ') || t('unknown')],
    [t('File modified'), formatDate(mediaFile.sourceLastWriteAt)],
    [t('Preview result'), [t(analysisItem.state), analysisItem.event?.name, analysisItem.message].filter(Boolean).join(' · ')]
  ];

  $('analysisTitle').textContent = mediaFile.originalName;
  $('analysisFacts').innerHTML = facts
    .map(([label, value]) => `<div class="modal-fact"><span>${escapeHtml(label)}</span><b>${escapeHtml(value)}</b></div>`)
    .join('');
  $('analysisEvent').innerHTML = `<option value="">${t('Choose an event…')}</option>`
    + events.map(event => `<option value="${event.id}">${escapeHtml(event.name)} · ${escapeHtml(t(event.status))}</option>`).join('');
  $('analysisEvent').value = analysisItem.event?.id || '';
  renderEventAnalysis();
  $('analysisModal').classList.remove('hidden');
};

function closeFileAnalysis() {
  $('analysisModal').classList.add('hidden');
  analysisItem = null;
}

$('analysisEvent').addEventListener('change', renderEventAnalysis);
$('analysisClose').addEventListener('click', closeFileAnalysis);
$('analysisModal').addEventListener('click', event => {
  if (event.target === $('analysisModal')) closeFileAnalysis();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && analysisItem) closeFileAnalysis();
});
$('routingFilter').addEventListener('input', renderRoutingPreview);
$('routingStateFilter').addEventListener('change', renderRoutingPreview);

/* Share / group / event actions ------------------------------------------ */

window.probeShare = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = t('Testing path…');
  try {
    const result = await runBackgroundTask(
      `share-probe:${id}`,
      `${t('Test path')} · ${share?.name || t('share')}`,
      'shares',
      () => request(`/api/v1/shares/${id}/probe`));
    state.textContent = result.exists && result.readable
      ? t('Path OK · readable')
      : t('Path problem · {{error}}', { error: result.error || t(result.exists ? 'not readable' : 'not found') });
  } catch (error) {
    state.textContent = t('Test failed · {{error}}', { error: error.message });
  }
};

window.scanShare = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = t('Scanning…');
  try {
    const result = await runBackgroundTask(
      `share-scan:${id}`,
      `${t('Scan')} · ${share?.name || t('source')}`,
      'shares',
      () => request(`/api/v1/shares/${id}/scan?limit=1`));
    state.textContent = t('{{total}} media files · {{stable}} stable · {{waiting}} waiting', {
      total: formatNumber(result.total),
      stable: formatNumber(result.stable),
      waiting: formatNumber(result.waitingStable)
    });
  } catch (error) {
    state.textContent = t('Scan failed · {{error}}', { error: error.message });
  }
};

window.metadataPreview = async function (id) {
  const state = $(`state-${id}`);
  const share = shares.find(item => item.id === id);
  state.textContent = t('Reading metadata…');
  try {
    const result = await runBackgroundTask(
      `metadata-preview:${id}`,
      `${t('Metadata')} · ${share?.name || t('source')}`,
      'shares',
      () => request(`/api/v1/shares/${id}/metadata-preview?limit=5`));
    if (!result.items.length) {
      state.textContent = t('No stable media yet. Scan again after the stability interval.');
      return;
    }
    const first = result.items[0];
    const captured = first.metadata.capturedAt || t('no capture time');
    const camera = [first.metadata.cameraMake, first.metadata.cameraModel].filter(Boolean).join(' ');
    const error = first.metadata.error ? ` · ${first.metadata.error}` : '';
    state.textContent = t('{{count}} metadata samples · {{captured}}', { count: formatNumber(result.total), captured })
      + (camera ? ` · ${camera}` : '') + error;
  } catch (error) {
    state.textContent = t('Metadata failed · {{error}}', { error: error.message });
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
  $('formTitle').textContent = t('Edit {{name}}', { name: share.name });
  openForm('shares', 'shareFormPanel');
};

window.deleteShare = async function (id) {
  const share = shares.find(x => x.id === id);
  if (!share || !confirm(t('Remove share “{{name}}”? No media files are deleted.', { name: share.name }))) return;
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
  $('groupFormTitle').textContent = t('Edit {{name}}', { name: group.name });
  renderGroupChoices(group.shareIds);
  openForm('groups', 'groupFormPanel');
};

window.deleteGroup = async function (id) {
  const group = groups.find(x => x.id === id);
  if (!group || !confirm(t('Remove source group “{{name}}”?', { name: group.name }))) return;
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
  setDateTime('eventStart', toLocalInput(event.startAt));
  setDateTime('eventEnd', event.endAt ? toLocalInput(event.endAt) : '');
  $('eventStatus').value = event.status;
  setOperationMode(event.operationMode);
  $('eventConflict').value = event.conflictStrategy;
  $('eventDuplicate').value = event.duplicateStrategy;
  $('eventTemplate').value = event.destinationFolderTemplate;
  $('eventFormTitle').textContent = t('Edit {{name}}', { name: event.name });
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

  const range = `${formatDate(event.startAt)} → ${event.endAt ? formatDate(event.endAt) : t('still open')}`;
  const mode = t(event.operationMode === 'Copy' ? 'copy' : 'safe-move');
  if (!confirm(
    t('Sort existing media into “{{name}}”?', { name: event.name }) + '\n\n' +
    t('MomentFerry will scan every source share of this event, read capture metadata for files it has not indexed yet, and {{mode}} everything captured in {{range}}.', { mode, range }) + '\n\n' +
    t('Media matching other events is left alone. On a large share the metadata pass can take a while.'))) {
    return;
  }

  const key = `backfill-${id}`;
  try {
    await runBackgroundTask(key, `${t('Backfill')}: ${event.name}`, 'events', async () => {
      const started = await request(`/api/v1/events/${id}/backfill`, { method: 'POST' });
      const summary = await monitorAutomationCycle(started.requestedAt, key, t('Backfill'));
      const routed = appInfo.dryRun !== false
        ? t('{{count}} would be routed (Dry Run)', { count: formatNumber(summary.wouldMove) })
        : t('{{count}} routed', { count: formatNumber(summary.executed) });
      alert(
        t('Backfill finished for “{{name}}”.', { name: event.name }) + '\n\n' +
        t('{{count}} matched', { count: formatNumber(summary.matched) }) + ` · ${routed}` +
        (summary.errors ? ` · ${t(summary.errors === 1 ? '{{count}} error' : '{{count}} errors', { count: formatNumber(summary.errors) })}` : ''));
    });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.routeEventAgain = async function (id) {
  const event = events.find(x => x.id === id);
  if (!event) return;

  if (!confirm(
    t('Route everything in “{{name}}” again?', { name: event.name }) + '\n\n' +
    t('Files this event already finished are normally never routed a second time. This clears that mark and runs a full pass under the current naming rules.') + '\n\n' +
    t('Copies already at the destination are left where they are. A file whose destination copy still exists is held for your decision instead of being moved again.'))) {
    return;
  }

  const key = `route-event-again-${id}`;
  try {
    await runBackgroundTask(key, `${t('Route again')}: ${event.name}`, 'events', async () => {
      const started = await request(`/api/v1/events/${id}/route-again`, { method: 'POST' });
      const summary = await monitorAutomationCycle(started.requestedAt, key, t('Route again'));
      alert(
        t('Route again finished for “{{name}}”.', { name: event.name }) + '\n\n' +
        t('{{count}} earlier operations cleared', { count: formatNumber(started.superseded) }) + ' · ' +
        t('{{count}} routed', { count: formatNumber(summary.executed) }) +
        (summary.errors ? ` · ${t(summary.errors === 1 ? '{{count}} error' : '{{count}} errors', { count: formatNumber(summary.errors) })}` : ''));
    });
    await Promise.all([reloadEvents(), refreshQuarantine()]);
  } catch (error) {
    alert(error.message);
  }
};

window.renameRoutedFiles = async function (id) {
  const event = events.find(x => x.id === id);
  if (!event) return;

  if (!confirm(
    t('Apply the current naming rules to the files “{{name}}” already stored?', { name: event.name }) + '\n\n' +
    t('MomentFerry names files on the way to the destination, so a preset or camera mapping added later never reached what was already stored. Route again cannot reach them either once Safe Move released their sources.') + '\n\n' +
    t('Only a file whose new name is free is renamed, and only while its bytes still match the checksum on record. Nothing is overwritten, and the operation history follows each file to its new name.'))) {
    return;
  }

  try {
    const result = await runBackgroundTask(
      `rename-routed-${id}`,
      `${t('Rename stored files')}: ${event.name}`,
      'events',
      () => request(`/api/v1/events/${id}/rename-routed`, { method: 'POST' }));
    const renamed = result.dryRun
      ? t('{{count}} would be renamed (Dry Run)', { count: formatNumber(result.renamed) })
      : t('{{count}} renamed', { count: formatNumber(result.renamed) });
    alert(
      t('Renaming finished for “{{name}}”.', { name: event.name }) + '\n\n' +
      t('{{count}} examined', { count: formatNumber(result.examined) }) + ' · ' + renamed + ' · ' +
      t('{{count}} already correct', { count: formatNumber(result.unchanged) }) + ' · ' +
      t('{{count}} skipped', { count: formatNumber(result.skipped) }) +
      (result.errors ? ` · ${t(result.errors === 1 ? '{{count}} error' : '{{count}} errors', { count: formatNumber(result.errors) })}` : '') +
      // Counts alone leave a run that skipped a thousand files unexplained.
      Object.entries(result.reasons || {}).map(([reason, count]) => `\n${formatNumber(count)} × ${t(reason)}`).join(''));
    await refreshOperations();
  } catch (error) {
    alert(error.message);
  }
};

window.deleteEvent = async function (id) {
  const event = events.find(x => x.id === id);
  if (!event || !confirm(t('Remove event “{{name}}”?', { name: event.name }))) return;
  try {
    await request(`/api/v1/events/${id}`, { method: 'DELETE' });
    await reloadEvents();
  } catch (error) {
    alert(error.message);
  }
};

window.executeTransfer = async function (mediaFileId, eventId) {
  const event = events.find(x => x.id === eventId);
  const action = t(event?.operationMode === 'Copy'
    ? 'copy this media file to the verified destination'
    : 'safe-move this media file; the source is only deleted after destination SHA-256 verification');
  if (!confirm(t('MomentFerry will {{action}}. Continue?', { action }))) return;

  try {
    const result = await runBackgroundTask(
      `transfer:${mediaFileId}`,
      `${t('Transfer')} · ${event?.name || t('event')}`,
      'ops',
      () => request('/api/v1/transfers', {
        method: 'POST',
        body: JSON.stringify({ mediaFileId, eventId })
      }));
    alert(result.message || t('Transfer finished: {{state}}', { state: t(result.operation.state) }));
    await refreshOperations();
    await previewRouting();
  } catch (error) {
    alert(error.message);
  }
};

window.routeAgain = async function (id) {
  const operation = operations.find(item => item.id === id);
  if (!operation) return;
  if (!confirm(t('Route this file again with the current naming rules? The copy already at {{path}} stays where it is.', { path: operation.destinationPath || t('the destination') }))) return;

  try {
    const result = await runBackgroundTask(
      `route-again:${id}`,
      `${t('Route again')} · ${baseName(operation.sourcePath)}`,
      'transfer',
      () => request(`/api/v1/operations/${id}/route-again`, { method: 'POST' }));
    alert(result.message || t('Transfer finished: {{state}}', { state: t(result.operation.state) }));
    await Promise.all([refreshOperations(), refreshQuarantine()]);
  } catch (error) {
    alert(error.message);
  }
};

window.dismissQuarantine = async function (id) {
  const resolutionNote = prompt(t('Describe how this held operation was resolved. The source file will not be deleted.'));
  if (resolutionNote === null) return;
  try {
    await request(`/api/v1/quarantine/${id}/dismiss`, {
      method: 'POST',
      body: JSON.stringify({ resolutionNote })
    });
    $('quarantineMessage').textContent = t('Item dismissed. Source preserved.');
    await Promise.all([refreshQuarantine(), refreshOperations()]);
  } catch (error) {
    $('quarantineMessage').textContent = error.message;
  }
};

window.retryQuarantine = async function (id) {
  if (!confirm(t('Retry this held transfer from the preserved source file?'))) return;
  try {
    await runBackgroundTask(
      `quarantine-retry:${id}`,
      t('Retry held transfer'),
      'ops',
      () => request(`/api/v1/operations/${id}/retry`, { method: 'POST' }));
    $('quarantineMessage').textContent = t('Held transfer retried.');
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
  tree.innerHTML = `<div class="subtle">${t('Loading mounted folders…')}</div>`;
  try {
    const result = await request(`/api/v1/folders?role=${encodeURIComponent($('role').value)}`);
    tree.innerHTML = result.roots.length
      ? result.roots.map(root => `
          <div class="folder-root">
            <div class="folder-root-label">${escapeHtml(root.path)}</div>
            <div class="folder-children">${renderFolderNodes(root.folders, 0)}</div>
          </div>`).join('')
      : `<div class="message">${t('No mounted folders found for this role.')}</div>`;
  } catch (error) {
    tree.innerHTML = `<div class="message error">${escapeHtml(error.message)}</div>`;
  }
}

function renderFolderNodes(folders, depth) {
  if (!folders.length) return `<div class="subtle" style="font-size:12px">${t('No subfolders.')}</div>`;
  return folders.map(folder => `
    <div class="folder-node" data-path="${escapeHtml(folder.path)}">
      <div class="folder-row" style="--depth:${depth}">
        <button type="button" class="btn folder-toggle${folder.hasChildren ? '' : ' placeholder'}" ${folder.hasChildren ? `aria-expanded="false" aria-label="${escapeHtml(t('Expand folder'))}"` : 'disabled aria-hidden="true"'}>${folder.hasChildren ? '›' : '·'}</button>
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
    button.setAttribute('aria-label', t('Expand folder'));
    button.textContent = '›';
    return;
  }

  button.disabled = true;
  try {
    const result = await request(`/api/v1/folders?role=${encodeURIComponent($('role').value)}&path=${encodeURIComponent(node.dataset.path)}`);
    children.innerHTML = renderFolderNodes(result.folders, Number(node.querySelector('.folder-row').style.getPropertyValue('--depth')) + 1);
    button.setAttribute('aria-expanded', 'true');
    button.setAttribute('aria-label', t('Collapse folder'));
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
  $('formTitle').textContent = t('Add share');
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
  $('groupFormTitle').textContent = t('Add source group');
  $('groupMessage').textContent = '';
  renderGroupChoices();
}

function resetEventForm() {
  $('eventForm').reset();
  $('eventId').value = '';
  $('eventFormTitle').textContent = t('New event');
  $('eventType').value = 'Vacation';
  setDateTime('eventStart', toLocalInput(new Date().toISOString()));
  // form.reset() clears the shown text but not data-value.
  setDateTime('eventEnd', '');
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
    renamePresetId: $('sharePreset').value || null,
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
    startAt: fromLocalInput($('eventStart').dataset.value),
    endAt: $('eventEnd').dataset.value ? fromLocalInput($('eventEnd').dataset.value) : null,
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
  renderRenamePresets();
  renderMappings();
  renderPresetChoices();
  refreshRenamePreview();
  const badge = $('badgeRenaming');
  if (badge) badge.textContent = renamePresets.length ? String(renamePresets.length) : '';
}

function renderPresetChoices() {
  const select = $('sharePreset');
  if (!select) return;
  const current = select.value;
  select.innerHTML = `<option value="">${t('No renaming')}</option>` +
    renamePresets.map(p => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('');
  select.value = current;
}

function renderRenamePresets() {
  const list = $('presetList');
  if (!list) return;
  if (!renamePresets.length) {
    list.innerHTML = `<div class="empty"><strong>${t('No presets yet')}</strong>${t('A preset is a filename template you can attach to a source or a destination.')}</div>`;
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
          <div class="list-meta">${usedBy.length ? t('Used by {{shares}}', { shares: escapeHtml(usedBy.join(', ')) }) : t('Not attached to a share yet')}</div>
        </div>
        <div class="card-actions">
          <button class="btn btn-sm btn-ghost" type="button" onclick="editPreset('${escapeHtml(preset.id)}')">${t('Edit')}</button>
          <button class="btn btn-sm btn-ghost" type="button" onclick="tryPreset('${escapeHtml(preset.id)}')">${t('Preview')}</button>
          <button class="btn btn-sm btn-danger" type="button" onclick="deletePreset('${escapeHtml(preset.id)}')">${t('Remove')}</button>
        </div>
      </article>`;
  }).join('');
}

function renderMappings() {
  const list = $('mappingList');
  if (!list) return;
  if (!cameraMappings.length) {
    list.innerHTML = `<div class="list-meta">${t('No mappings yet. The reported model is used as-is.')}</div>`;
    return;
  }

  list.innerHTML = cameraMappings.map(mapping => `
    <article class="list-row" style="padding:10px 14px">
      <div class="list-main">
        <div class="mono" style="font-size:12.5px">${escapeHtml(mapping.from)} → <b>${escapeHtml(mapping.to)}</b></div>
      </div>
      <div class="card-actions">
        <button class="btn btn-sm btn-danger" type="button" onclick="deleteMapping('${escapeHtml(mapping.id)}')">${t('Remove')}</button>
      </div>
    </article>`).join('');
}

async function refreshRenamePreview() {
  const target = $('renamePreview');
  if (!target) return;
  const sourceTemplate = $('previewSourceTemplate').value.trim();
  const destinationTemplate = $('previewDestinationTemplate').value.trim();

  try {
    const result = await request('/api/v1/rename-presets/preview', {
      method: 'POST',
      body: JSON.stringify({ sourceTemplate, destinationTemplate })
    });
    const unchanged = !sourceTemplate && !destinationTemplate;
    target.innerHTML = result.samples.map(sample => `
      <div class="list-row" style="padding:9px 13px">
        <div class="list-main">
          <div class="mono" style="font-size:12px;color:var(--mut)">${escapeHtml(sample.original)}</div>
          <div class="mono" style="font-size:13px">${unchanged
            ? `<span style="color:var(--mut)">${t('unchanged until a template is set')}</span>`
            : `<b class="acc">${escapeHtml(sample.result)}</b>`}</div>
        </div>
        <div class="list-side">
          <div class="list-meta">${escapeHtml(sample.origin || t('sample'))}</div>
          <div class="list-meta">${sample.camera ? escapeHtml(sample.camera) : t('no camera')}</div>
        </div>
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
  $('presetFormTitle').textContent = t('Edit {{name}}', { name: preset.name });
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
    ? '\n\n' + t('{{shares}} will stop renaming and keep original filenames.', { shares: usedBy.join(', ') })
    : '';
  if (!preset || !confirm(t('Remove preset “{{name}}”?', { name: preset.name }) + warning)) return;
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
  $('presetFormTitle').textContent = t('Add preset');
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
$('refreshMaintenance').addEventListener('click', () => refreshMaintenance().catch(error => {
  $('maintenanceMessage').textContent = error.message;
}));

$('reindexMetadata').addEventListener('click', () => {
  const shareId = $('maintenanceShare').value;
  const share = shares.find(x => x.id === shareId);
  runMaintenance('reindex-metadata', t('Read metadata again'), async () => {
    const query = shareId ? `?shareId=${encodeURIComponent(shareId)}` : '';
    const result = await request(`/api/v1/maintenance/reindex-metadata${query}`, { method: 'POST' });
    return t('{{count}} files will have their metadata read again on the next cycle.', {
      count: formatNumber(result.affected)
    }) + (share ? ` (${share.name})` : '');
  });
});

$('forgetMissing').addEventListener('click', () => {
  if (!confirm(t('Remove index entries whose source file no longer exists?'))) return;
  runMaintenance('forget-missing', t('Forget missing files'), async () => {
    const result = await request('/api/v1/maintenance/forget-missing', { method: 'POST' });
    return t('{{removed}} of {{missing}} missing entries removed, {{kept}} kept because they carry an operation.', {
      removed: formatNumber(result.removed),
      missing: formatNumber(result.missing),
      kept: formatNumber(result.keptForHistory)
    });
  });
});

$('compactDatabase').addEventListener('click', () => {
  runMaintenance('compact-database', t('Compact database'), async () => {
    const result = await request('/api/v1/maintenance/compact', { method: 'POST' });
    return t('Database compacted, {{size}} reclaimed.', { size: formatBytes(Math.max(0, result.reclaimed || 0)) });
  });
});

$('saveRetention').addEventListener('click', () => {
  const days = Number($('retentionDays').value);
  runMaintenance('save-retention', t('Save'), async () => {
    const settings = await request('/api/v1/settings');
    await request('/api/v1/settings', {
      method: 'PUT',
      body: JSON.stringify({ ...settings, operationRetentionDays: days })
    });
    return days > 0
      ? t('Finished operations older than {{count}} days are removed on each full reconcile.', { count: formatNumber(days) })
      : t('The operation history is kept for good.');
  });
});

$('pruneNow').addEventListener('click', () => {
  const days = Number($('retentionDays').value);
  if (!(days > 0)) {
    $('maintenanceMessage').textContent = t('Set a retention window of at least one day first.');
    return;
  }
  if (!confirm(t('Remove finished operations older than {{count}} days now? This cannot be undone.', { count: formatNumber(days) }))) return;
  runMaintenance('prune-operations', t('Remove now'), async () => {
    const result = await request(`/api/v1/maintenance/prune-operations?olderThanDays=${days}`, { method: 'POST' });
    return t('{{count}} finished operations removed.', { count: formatNumber(result.removed) });
  });
});

$('refreshOperations').addEventListener('click', refreshOperations);
$('refreshLogs').addEventListener('click', refreshLogs);
$('logLevel').addEventListener('change', refreshLogs);
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

initI18n();
applyTheme(document.documentElement.dataset.theme === 'light' ? 'light' : 'dark');
setView(location.hash.slice(1) || 'overview');
setInterval(renderNextScanCountdown, 1000);
load();
