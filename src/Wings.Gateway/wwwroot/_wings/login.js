(() => {
  const $ = (id) => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  const returnUrl = params.get('returnUrl') || '/';
  let csrf = null;
  let mfaStep = false;

  async function token() {
    if (csrf) return csrf;
    const r = await fetch('/_wings/auth/csrf', { credentials: 'same-origin', cache: 'no-store' });
    csrf = (await r.json()).token;
    return csrf;
  }

  async function post(url, body) {
    return fetch(url, {
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': await token() },
      body: JSON.stringify(body)
    });
  }

  if (params.get('error')) $('message').textContent = 'Sign-in failed. Please try again.';

  $('login').addEventListener('submit', async (e) => {
    e.preventDefault();
    const company = $('company').value.trim();
    const username = $('username').value.trim();
    const password = $('password').value;
    if (!company || !username || !password) { $('message').textContent = 'Enter your company, username and password.'; return; }
    $('submit').disabled = true;
    $('message').textContent = '';
    try {
      if (!mfaStep) {
        const d = await (await post('/_wings/auth/discover', { company })).json();
        if (d.method === 'oidc') { location.href = d.url; return; }
      }
      const r = await post('/_wings/auth/login', { company, username, password, mfaCode: $('mfa').value || null, returnUrl });
      const body = await r.json().catch(() => ({}));
      if (r.ok && body.mfaRequired) {
        mfaStep = true;
        $('mfa-row').hidden = false;
        $('mfa').focus();
        $('message').textContent = 'Enter the code from your authenticator app.';
      } else if (r.ok && body.redirect) {
        location.replace(body.redirect);
      } else {
        $('message').textContent = body.message || 'Sign-in failed. Please try again.';
        if (r.status === 400) csrf = null;
      }
    } catch {
      $('message').textContent = 'Sign-in is temporarily unavailable. Please try again.';
    } finally {
      $('submit').disabled = false;
    }
  });
})();
