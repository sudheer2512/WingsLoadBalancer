(async () => {
  try {
    const t = await (await fetch('/_wings/auth/csrf', { credentials: 'same-origin', cache: 'no-store' })).json();
    await fetch('/_wings/auth/logout', { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': t.token } });
  } finally {
    location.replace('/_wings/login');
  }
})();
