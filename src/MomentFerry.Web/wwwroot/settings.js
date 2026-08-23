/* MomentFerry Console — runtime settings, automation status, storage, updates.
   Depends on globals declared in app.js: appInfo, automationInfo, storageInfo,
   updateInfo, $(), request-style helpers, formatBytes(), and the renderers.
--------------------------------------------------------------------------- */

let currentRuntimeSettings = null;
const LIVE_CONFIRMATION = 'ENABLE_LIVE_TRANSFERS';
const LIVE_PHRASE = 'ENABLE LIVE';

async function settingsRequest(url, options = {}) {
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
    liveModeConfirmation: null,
    ...overrides
  };
}

async function saveSettings(overrides, messageTarget) {
  const target = messageTarget ? $(messageTarget) : null;
  try {
    const updated = await settingsRequest('/api/v1/settings', {
      method: 'PUT',
      body: JSON.stringify(settingsFromForm(overrides))
    });
    applySettingsToForm(updated);
    if (target) {
      target.className = 'message ok';
      target.textContent = updated.dryRun
        ? 'Saved. Media operations remain non-destructive.'
        : 'Saved. LIVE transfers are enabled.';
    }
    await loadAutomationStatus();
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

async function loadRuntimeSettings() {
  try {
    const settings = await settingsRequest('/api/v1/settings');
    applySettingsToForm(settings);
    $('settingsMessage').textContent = '';
    $('settingsMessage').className = 'message';
  } catch (error) {
    $('settingsMessage').className = 'message error';
    $('settingsMessage').textContent = `Settings failed: ${error.message}`;
  }
}

/* Leave Dry Run modal ------------------------------------------------------ */

function openLiveModal() {
  const matched = automationInfo ? automationInfo.lastMatched : 0;
  const held = quarantinedOperations.length;
  const safeMove = events.some(x => x.status === 'Active' && x.operationMode !== 'Copy');

  $('liveModalFacts').innerHTML = `
    <div class="kicker" style="margin-bottom:9px">This will affect</div>
    <div class="modal-fact"><span>Files matched by the last scan</span><b>${matched.toLocaleString()}</b></div>
    ${safeMove ? `<div class="modal-fact"><span>Originals deleted after verifying</span><b>${matched.toLocaleString()}</b></div>` : ''}
    <div class="modal-fact"><span>Held for review, untouched</span><b class="amb">${held.toLocaleString()}</b></div>`;

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
    const mode = result.mode === 'live' ? 'Live' : 'Dry Run';
    const enabled = result.automationEnabled ? 'Automation running' : 'Automation off';
    const healthy = result.automationEnabled && !automation.lastError;
    $('automationDot').className = `dot ${healthy ? 'dot-acc' : (automation.lastError ? 'dot-red' : 'dot-amb')}`;

    if (!automation.lastCycleStartedAt) {
      target.textContent = `${enabled} · ${mode} · no cycle recorded yet · ${formatStorageStatus(storage)}`;
    } else {
      const completed = automation.lastCycleCompletedAt
        ? new Date(automation.lastCycleCompletedAt).toLocaleTimeString()
        : 'running';
      const error = automation.lastError ? ` · last error: ${automation.lastError}` : '';
      target.textContent = `${enabled} · ${mode} · last cycle ${completed} · `
        + `${automation.lastSourceShares} sources · ${automation.lastMatched} matched · `
        + `${automation.lastWouldMove} would move · ${automation.lastExecuted} executed · ${automation.lastSkipped} skipped · `
        + `${automation.lastErrors} errors${error} · ${formatStorageStatus(storage)}`;
    }

    renderOverview();
    if (!$('view-setup').classList.contains('hidden')) renderSetup();
  } catch (error) {
    $('automationDot').className = 'dot dot-red';
    target.textContent = `Status failed: ${error.message}`;
  }
}

function formatStorageStatus(storage) {
  if (!storage?.items?.length) return 'no destination storage configured';
  const items = storage.items.map(item => {
    if (!item.exists) return `${item.name}: path missing`;
    if (item.availableFreeSpaceBytes == null) return `${item.name}: free space unknown`;
    return `${item.name}: ${formatBytes(item.availableFreeSpaceBytes)} free${item.belowReserve ? ' LOW' : ''}`;
  });
  return `${items.join(', ')} · reserve ${formatBytes(storage.minimumFreeSpaceReserveBytes)}`;
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
  if (!confirm('Reset runtime settings to the Docker/application defaults?')) return;
  try {
    const settings = await settingsRequest('/api/v1/settings', { method: 'DELETE' });
    applySettingsToForm(settings);
    $('settingsMessage').className = 'message ok';
    $('settingsMessage').textContent = 'Runtime settings reset to defaults.';
    await loadAutomationStatus();
  } catch (error) {
    $('settingsMessage').className = 'message error';
    $('settingsMessage').textContent = error.message;
  }
});

$('refreshAutomationStatus').addEventListener('click', loadAutomationStatus);

/* Image updates ------------------------------------------------------------ */

function renderImageUpdate(status) {
  updateInfo = status;

  const banner = $('updateBanner');
  const headline = $('updateHeadline');
  const detail = $('imageUpdateStatus');
  const changelog = $('imageUpdateChangelog');
  const install = $('installImageUpdate');

  const completed = status.lastUpdateCompletedAt
    ? ` · updated ${new Date(status.lastUpdateCompletedAt).toLocaleString()}`
    : '';

  if (status.updateAvailable && status.latestVersion) {
    banner.classList.add('card-accent');
    headline.style.color = 'var(--acctxt)';
    headline.textContent = `${status.latestVersion} is available`;
    detail.textContent = `You are running ${status.runningVersion}. The update is applied by an isolated companion container, so MomentFerry can restart itself safely.${status.lastError ? ` · ${status.lastError}` : ''}`;
  } else {
    banner.classList.remove('card-accent');
    headline.style.color = 'var(--txt)';
    headline.textContent = `Running ${status.runningVersion}`;
    detail.textContent = `${status.latestVersion ? `Latest stable is ${status.latestVersion}. ` : ''}No update pending.${completed}${status.lastError ? ` · ${status.lastError}` : ''}`;
  }

  changelog.textContent = status.changelog || '';
  changelog.classList.toggle('hidden', !status.changelog);
  $('changelogEmpty').classList.toggle('hidden', Boolean(status.changelog));

  install.classList.toggle('hidden', !status.updateAvailable);
  install.disabled = !status.updaterConfigured;
  install.title = status.updaterConfigured ? '' : 'Updater companion is not configured';

  // Prefer the checked release page; fall back to the running version's own tag so the link
  // is present before any update check has run.
  const link = $('releaseLink');
  const linkUrl = status.releaseUrl || status.runningVersionUrl;
  link.classList.toggle('hidden', !linkUrl);
  if (linkUrl) {
    link.href = linkUrl;
    link.textContent = status.releaseUrl && status.latestVersion
      ? `View ${status.latestVersion} release notes on GitHub`
      : `View ${status.runningVersion} on GitHub`;
  }

  $('versionRunning').textContent = status.runningVersion || 'unknown';
  const sideUpdate = $('versionUpdate');
  sideUpdate.classList.toggle('hidden', !status.updateAvailable);
  if (status.updateAvailable) sideUpdate.textContent = `${status.latestVersion} available →`;
}

async function checkImageUpdate() {
  $('imageUpdateStatus').textContent = 'Checking for stable image updates…';
  try {
    const status = await runBackgroundTask(
      'image-update-check',
      'Check for image updates',
      'updates',
      () => settingsRequest('/api/v1/updates/check', { method: 'POST' }));
    renderImageUpdate(status);
  } catch (error) {
    $('imageUpdateStatus').textContent = `Update check failed: ${error.message}`;
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
  throw new Error(`MomentFerry did not return with version ${expectedVersion} within three minutes.`);
}

$('installImageUpdate').addEventListener('click', async () => {
  const confirmation = prompt('The MomentFerry container will restart. Type INSTALL_UPDATE to continue.');
  if (confirmation !== 'INSTALL_UPDATE') return;

  const expectedVersion = updateInfo.latestVersion;
  const statusText = $('imageUpdateStatus');
  const install = $('installImageUpdate');
  install.disabled = true;
  statusText.textContent = `Installing ${expectedVersion}. MomentFerry will restart…`;

  try {
    const status = await runBackgroundTask('image-update', `Install ${expectedVersion}`, 'updates', async () => {
      try {
        await settingsRequest('/api/v1/updates/install', {
          method: 'POST',
          body: JSON.stringify({ confirmation })
        });
      } catch (error) {
        if (error.status) throw error;
      }

      statusText.textContent = 'Update requested. Waiting for MomentFerry to restart…';
      return waitForUpdatedMomentFerry(expectedVersion);
    });
    renderImageUpdate(status);
    statusText.textContent = `Updated to ${status.runningVersion}. Reloading…`;
    window.location.reload();
  } catch (error) {
    statusText.textContent = `Update failed: ${error.message}`;
    install.disabled = false;
  }
});

$('settingsAutomaticImageUpdates').addEventListener('change', async () => {
  const message = $('autoUpdateMessage');
  try {
    await saveSettings({});
    message.className = 'message ok';
    message.textContent = $('settingsAutomaticImageUpdates').checked
      ? 'Stable updates will install automatically.'
      : 'Automatic updates are off.';
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

loadRuntimeSettings();
pollAutomationStatus();
settingsRequest('/api/v1/updates').then(renderImageUpdate).catch(() => {
  $('versionRunning').textContent = 'unknown';
});
