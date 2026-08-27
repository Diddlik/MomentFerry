/* MomentFerry Console — runtime settings, automation status, storage, updates.
   Depends on globals declared in app.js: appInfo, automationInfo, storageInfo,
   updateInfo, $(), request-style helpers, formatBytes(), and the renderers.
--------------------------------------------------------------------------- */

let currentRuntimeSettings = null;
let currentAuthStatus = null;
const LIVE_CONFIRMATION = 'ENABLE_LIVE_TRANSFERS';
const LIVE_PHRASE = 'ENABLE LIVE';

async function settingsRequest(url, options = {}) {
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
    const error = new Error(message);
    error.status = response.status;
    throw error;
  }

  return response.status === 204 ? null : response.json();
}

/* Form <-> settings ------------------------------------------------------- */

function applySettingsToForm(settings) {
  currentRuntimeSettings = settings;
  $('settingsDryRun').checked = settings.dryRun;
  $('settingsAutomationEnabled').checked = settings.automationEnabled;
  $('settingsInterval').value = settings.reconciliationIntervalSeconds;
  $('settingsMaxFiles').value = settings.maxFilesPerSharePerCycle;
  $('settingsParallelMetadata').value = settings.maxParallelMetadataReads;
  $('settingsTimestampFallback').checked = settings.allowFilesystemTimestampFallback;
  $('settingsFreeSpaceReserve').value = Math.round(settings.minimumFreeSpaceReserveBytes / 1048576);
  $('settingsAutomaticImageUpdates').checked = settings.automaticImageUpdatesEnabled;
  $('settingsPasswordProtection').checked = settings.passwordProtectionEnabled;

  appInfo = { ...appInfo, ...settings };
  renderMode();
  renderOnboarding();
  renderOverview();
  if (!$('view-setup').classList.contains('hidden')) renderSetup();
}

function settingsFromForm(overrides = {}) {
  return {
    dryRun: $('settingsDryRun').checked,
    automationEnabled: $('settingsAutomationEnabled').checked,
    reconciliationIntervalSeconds: Number($('settingsInterval').value),
    maxFilesPerSharePerCycle: Number($('settingsMaxFiles').value),
    maxParallelMetadataReads: Number($('settingsParallelMetadata').value),
    allowFilesystemTimestampFallback: $('settingsTimestampFallback').checked,
    minimumFreeSpaceReserveBytes: Number($('settingsFreeSpaceReserve').value) * 1048576,
    automaticImageUpdatesEnabled: $('settingsAutomaticImageUpdates').checked,
    passwordProtectionEnabled: $('settingsPasswordProtection').checked,
    liveModeConfirmation: null,
    ...overrides
  };
}

async function saveSettings(overrides, messageTarget) {
  const target = messageTarget ? $(messageTarget) : null;
  try {
    const enablingProtection = !currentRuntimeSettings?.passwordProtectionEnabled &&
      (overrides.passwordProtectionEnabled ?? $('settingsPasswordProtection').checked);
    const updated = await settingsRequest('/api/v1/settings', {
      method: 'PUT',
      body: JSON.stringify(settingsFromForm(overrides))
    });
    applySettingsToForm(updated);
    if (target) {
      target.className = 'message ok';
      target.textContent = t(updated.dryRun
        ? 'Saved. Media operations remain non-destructive.'
        : 'Saved. LIVE transfers are enabled.');
    }
    await loadAutomationStatus();
    await loadAuthStatus();
    if (enablingProtection && updated.passwordProtectionEnabled) {
      location.assign('/login.html');
    }
    return updated;
  } catch (error) {
    if (target) {
      target.className = 'message error';
      target.textContent = error.message;
    }
    await loadRuntimeSettings();
    throw error;
  }
}

async function loadAuthStatus() {
  currentAuthStatus = await settingsRequest('/api/v1/auth/status', { cache: 'no-store' });
  const toggle = $('settingsPasswordProtection');
  toggle.disabled = !currentAuthStatus.credentialsConfigured && !toggle.checked;
  $('passwordProtectionStatus').textContent = t(currentAuthStatus.credentialsConfigured
    ? 'Credentials configured through environment variables.'
    : 'Set MOMENTFERRY_USERNAME and MOMENTFERRY_PASSWORD in your .env file first.');
  $('logoutButton').classList.toggle('hidden', !currentAuthStatus.protectionEnabled);
}

async function loadRuntimeSettings() {
  try {
    const settings = await settingsRequest('/api/v1/settings');
    applySettingsToForm(settings);
    $('settingsMessage').textContent = '';
    $('settingsMessage').className = 'message';
  } catch (error) {
    $('settingsMessage').className = 'message error';
    $('settingsMessage').textContent = t('Settings failed: {{error}}', { error: error.message });
  }
}

/* Leave Dry Run modal ------------------------------------------------------ */

function openLiveModal() {
  const matched = automationInfo ? automationInfo.lastMatched : 0;
  const held = quarantinedOperations.length;
  const safeMove = events.some(x => x.status === 'Active' && x.operationMode !== 'Copy');

  $('liveModalFacts').innerHTML = `
    <div class="kicker" style="margin-bottom:9px">${t('This will affect')}</div>
    <div class="modal-fact"><span>${t('Files matched by the last scan')}</span><b>${formatNumber(matched)}</b></div>
    ${safeMove ? `<div class="modal-fact"><span>${t('Originals deleted after verifying')}</span><b>${formatNumber(matched)}</b></div>` : ''}
    <div class="modal-fact"><span>${t('Held for review, untouched')}</span><b class="amb">${formatNumber(held)}</b></div>`;

  $('liveModalToken').value = '';
  $('liveModalConfirm').disabled = true;
  $('liveModalMessage').textContent = '';
  $('liveModal').classList.remove('hidden');
  $('liveModalToken').focus();
}

function closeLiveModal() {
  $('liveModal').classList.add('hidden');
  $('settingsDryRun').checked = currentRuntimeSettings ? currentRuntimeSettings.dryRun : true;
}

$('liveModalToken').addEventListener('input', event => {
  $('liveModalConfirm').disabled = event.target.value.trim().toUpperCase() !== LIVE_PHRASE;
});

$('liveModalCancel').addEventListener('click', closeLiveModal);
$('liveModal').addEventListener('click', event => {
  if (event.target === $('liveModal')) closeLiveModal();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && !$('liveModal').classList.contains('hidden')) closeLiveModal();
});

$('liveModalConfirm').addEventListener('click', async () => {
  $('liveModalConfirm').disabled = true;
  try {
    await saveSettings({ dryRun: false, liveModeConfirmation: LIVE_CONFIRMATION });
    $('liveModal').classList.add('hidden');
  } catch (error) {
    $('liveModalMessage').className = 'message error';
    $('liveModalMessage').textContent = error.message;
    $('liveModal').classList.remove('hidden');
    $('liveModalConfirm').disabled = false;
  }
});

async function backToDryRun() {
  $('settingsDryRun').checked = true;
  await saveSettings({ dryRun: true }, 'settingsMessage');
}

$('modeAction').addEventListener('click', () => {
  if (appInfo.dryRun === false) backToDryRun(); else openLiveModal();
});

$('setupEnableLive').addEventListener('click', openLiveModal);

/* The Dry Run switch acts immediately — it is the one safety-critical toggle. */
$('settingsDryRun').addEventListener('change', event => {
  if (event.target.checked) backToDryRun();
  else openLiveModal();
});

/* Automation status + storage ---------------------------------------------- */

async function loadAutomationStatus() {
  const target = $('automationStatus');
  try {
    const [result, storage] = await Promise.all([
      settingsRequest('/api/v1/status'),
      settingsRequest('/api/v1/storage')
    ]);

    automationInfo = result.automation;
    storageInfo = storage;

    const automation = result.automation;
    const mode = t(result.mode === 'live' ? 'Live' : 'Dry Run');
    const enabled = t(result.automationEnabled ? 'Automation running' : 'Automation off');
    const healthy = result.automationEnabled && !automation.lastError;
    $('automationDot').className = `dot ${healthy ? 'dot-acc' : (automation.lastError ? 'dot-red' : 'dot-amb')}`;

    if (!automation.lastCycleStartedAt) {
      target.textContent = `${enabled} · ${mode} · ${t('no cycle recorded yet')} · ${formatStorageStatus(storage)}`;
    } else {
      const completed = automation.lastCycleCompletedAt
        ? new Date(automation.lastCycleCompletedAt).toLocaleTimeString(MF_LANG)
        : t('running');
      const error = automation.lastError ? ` · ${t('last error: {{error}}', { error: automation.lastError })}` : '';
      target.textContent = `${enabled} · ${mode} · ${t('last cycle {{time}}', { time: completed })} · `
        + t('{{sources}} sources · {{matched}} matched · {{wouldMove}} would move · {{executed}} executed · {{skipped}} skipped · {{errors}} errors', {
          sources: formatNumber(automation.lastSourceShares),
          matched: formatNumber(automation.lastMatched),
          wouldMove: formatNumber(automation.lastWouldMove),
          executed: formatNumber(automation.lastExecuted),
          skipped: formatNumber(automation.lastSkipped),
          errors: formatNumber(automation.lastErrors)
        })
        + `${error} · ${formatStorageStatus(storage)}`;
    }

    renderOverview();
    if (!$('view-setup').classList.contains('hidden')) renderSetup();
  } catch (error) {
    $('automationDot').className = 'dot dot-red';
    target.textContent = t('Status failed: {{error}}', { error: error.message });
  }
}

function formatStorageStatus(storage) {
  if (!storage?.items?.length) return t('no destination storage configured');
  const items = storage.items.map(item => {
    if (!item.exists) return `${item.name}: ${t('path missing')}`;
    if (item.availableFreeSpaceBytes == null) return `${item.name}: ${t('free space unknown')}`;
    return `${item.name}: ${t('{{size}} free', { size: formatBytes(item.availableFreeSpaceBytes) })}${item.belowReserve ? ` ${t('LOW')}` : ''}`;
  });
  return `${items.join(', ')} · ${t('reserve {{size}}', { size: formatBytes(storage.minimumFreeSpaceReserveBytes) })}`;
}

/* Settings form ------------------------------------------------------------ */

$('settingsForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const goingLive = currentRuntimeSettings?.dryRun && !$('settingsDryRun').checked;
  if (goingLive) {
    openLiveModal();
    return;
  }
  try {
    await saveSettings({}, 'settingsMessage');
  } catch {}
});

$('resetSettings').addEventListener('click', async () => {
  if (!confirm(t('Reset runtime settings to the Docker/application defaults?'))) return;
  try {
    const settings = await settingsRequest('/api/v1/settings', { method: 'DELETE' });
    applySettingsToForm(settings);
    $('settingsMessage').className = 'message ok';
    $('settingsMessage').textContent = t('Runtime settings reset to defaults.');
    await loadAutomationStatus();
  } catch (error) {
    $('settingsMessage').className = 'message error';
    $('settingsMessage').textContent = error.message;
  }
});

$('refreshAutomationStatus').addEventListener('click', loadAutomationStatus);

$('logoutButton').addEventListener('click', async () => {
  await settingsRequest('/api/v1/auth/logout', { method: 'POST' });
  location.assign('/login.html');
});

/* Image updates ------------------------------------------------------------ */

/** Matches ImageUpdateRequest.RequiredConfirmation: the API refuses an install without it. */
const INSTALL_CONFIRMATION = 'INSTALL_UPDATE';

function renderImageUpdate(status) {
  updateInfo = status;

  const banner = $('updateBanner');
  const headline = $('updateHeadline');
  const detail = $('imageUpdateStatus');
  const changelog = $('imageUpdateChangelog');
  const install = $('installImageUpdate');

  const completed = status.lastUpdateCompletedAt
    ? ` · ${t('updated {{time}}', { time: new Date(status.lastUpdateCompletedAt).toLocaleString(MF_LANG) })}`
    : '';

  if (status.updateAvailable && status.latestVersion) {
    banner.classList.add('card-accent');
    headline.style.color = 'var(--acctxt)';
    headline.textContent = t('{{version}} is available', { version: status.latestVersion });
    detail.textContent = t('You are running {{version}}. The update is applied by an isolated companion container, so MomentFerry can restart itself safely.', { version: status.runningVersion })
      + (status.lastError ? ` · ${status.lastError}` : '');
  } else {
    banner.classList.remove('card-accent');
    headline.style.color = 'var(--txt)';
    headline.textContent = t('Running {{version}}', { version: status.runningVersion });
    detail.textContent = `${status.latestVersion ? `${t('Latest stable is {{version}}.', { version: status.latestVersion })} ` : ''}`
      + t('No update pending.') + completed + (status.lastError ? ` · ${status.lastError}` : '');
  }

  changelog.textContent = status.changelog || '';
  changelog.classList.toggle('hidden', !status.changelog);
  $('changelogEmpty').classList.toggle('hidden', Boolean(status.changelog));

  install.classList.toggle('hidden', !status.updateAvailable);
  install.disabled = !status.updaterConfigured;
  install.title = status.updaterConfigured ? '' : t('Updater companion is not configured');

  // Prefer the checked release page; fall back to the running version's own tag so the link
  // is present before any update check has run.
  const link = $('releaseLink');
  const linkUrl = status.releaseUrl || status.runningVersionUrl;
  link.classList.toggle('hidden', !linkUrl);
  if (linkUrl) {
    link.href = linkUrl;
    link.textContent = status.releaseUrl && status.latestVersion
      ? t('View {{version}} release notes on GitHub', { version: status.latestVersion })
      : t('View {{version}} on GitHub', { version: status.runningVersion });
  }

  $('versionRunning').textContent = status.runningVersion || t('unknown');
  const sideUpdate = $('versionUpdate');
  sideUpdate.classList.toggle('hidden', !status.updateAvailable);
  if (status.updateAvailable) sideUpdate.textContent = t('{{version}} available →', { version: status.latestVersion });
}

async function checkImageUpdate() {
  $('imageUpdateStatus').textContent = t('Checking for stable image updates…');
  try {
    const status = await runBackgroundTask(
      'image-update-check',
      t('Check for image updates'),
      'updates',
      () => settingsRequest('/api/v1/updates/check', { method: 'POST' }));
    renderImageUpdate(status);
  } catch (error) {
    $('imageUpdateStatus').textContent = t('Update check failed: {{error}}', { error: error.message });
  }
}

$('checkImageUpdate').addEventListener('click', checkImageUpdate);

async function waitForUpdatedMomentFerry(expectedVersion, timeoutMs = 180000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 2000));
    try {
      const status = await settingsRequest('/api/v1/updates', { cache: 'no-store' });
      if (status.runningVersion === expectedVersion && !status.updateAvailable) return status;
    } catch {}
  }
  throw new Error(t('MomentFerry did not return with version {{version}} within three minutes.', { version: expectedVersion }));
}

$('installImageUpdate').addEventListener('click', async () => {
  // No dialog: the button only exists while an update is available, it names the version it installs,
  // and a restart onto a verified image destroys nothing. The API still requires the explicit token,
  // so a stray POST cannot restart the container.

  const expectedVersion = updateInfo.latestVersion;
  const statusText = $('imageUpdateStatus');
  const install = $('installImageUpdate');
  install.disabled = true;
  statusText.textContent = t('Installing {{version}}. MomentFerry will restart…', { version: expectedVersion });

  try {
    const status = await runBackgroundTask('image-update', t('Install {{version}}', { version: expectedVersion }), 'updates', async () => {
      try {
        await settingsRequest('/api/v1/updates/install', {
          method: 'POST',
          body: JSON.stringify({ confirmation: INSTALL_CONFIRMATION })
        });
      } catch (error) {
        if (error.status) throw error;
      }

      statusText.textContent = t('Update requested. Waiting for MomentFerry to restart…');
      return waitForUpdatedMomentFerry(expectedVersion);
    });
    renderImageUpdate(status);
    statusText.textContent = t('Updated to {{version}}. Reloading…', { version: status.runningVersion });
    window.location.reload();
  } catch (error) {
    statusText.textContent = t('Update failed: {{error}}', { error: error.message });
    install.disabled = false;
  }
});

$('settingsAutomaticImageUpdates').addEventListener('change', async () => {
  const message = $('autoUpdateMessage');
  try {
    await saveSettings({});
    message.className = 'message ok';
    message.textContent = t($('settingsAutomaticImageUpdates').checked
      ? 'Stable updates will install automatically.'
      : 'Automatic updates are off.');
  } catch (error) {
    message.className = 'message error';
    message.textContent = error.message;
  }
});

/* Boot ---------------------------------------------------------------------- */

async function pollAutomationStatus() {
  await loadAutomationStatus();
  setTimeout(pollAutomationStatus, automationInfo?.cycleRunning ? 2000 : 5000);
}

loadRuntimeSettings().then(loadAuthStatus).catch(() => {});
pollAutomationStatus();
settingsRequest('/api/v1/updates').then(renderImageUpdate).catch(() => {
  $('versionRunning').textContent = t('unknown');
});
