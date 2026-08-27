initI18n();

const form = document.getElementById('loginForm');
const message = document.getElementById('loginMessage');
const submit = document.getElementById('loginSubmit');

function returnUrl() {
  const value = new URLSearchParams(location.search).get('returnUrl');
  return value?.startsWith('/') && !value.startsWith('//') ? value : '/';
}

async function authRequest(url, options = {}) {
  const response = await fetch(url, {
    headers: {
      'Content-Type': 'application/json',
      'X-MomentFerry-Request': '1',
      ...(options.headers || {})
    },
    cache: 'no-store',
    ...options
  });
  if (!response.ok) {
    let detail = response.status === 429
      ? t('Too many sign-in attempts. Try again in a minute.')
      : `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      detail = body.error || body.title || detail;
    } catch {}
    throw new Error(detail);
  }
  return response.status === 204 ? null : response.json();
}

authRequest('/api/v1/auth/status').then(status => {
  if (!status.protectionEnabled || status.authenticated) location.replace(returnUrl());
  if (!status.credentialsConfigured) {
    submit.disabled = true;
    message.className = 'message error';
    message.textContent = t('Access protection is not configured.');
  }
}).catch(error => {
  message.className = 'message error';
  message.textContent = error.message;
});

form.addEventListener('submit', async event => {
  event.preventDefault();
  submit.disabled = true;
  message.className = 'message';
  message.textContent = t('Signing in…');
  try {
    await authRequest('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({
        username: document.getElementById('loginUsername').value,
        password: document.getElementById('loginPassword').value
      })
    });
    location.replace(returnUrl());
  } catch (error) {
    message.className = 'message error';
    message.textContent = error.message;
    submit.disabled = false;
    document.getElementById('loginPassword').select();
  }
});
