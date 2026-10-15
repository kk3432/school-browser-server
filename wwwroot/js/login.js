// 管理员登录页（v0.8.3 拆页后为独立页面）
async function submitLogin() {
  setMsg('login-msg', '');
  try {
    const data = await api('/api/login', {
      method: 'POST',
      body: JSON.stringify({
        username: $('login-username').value.trim(),
        password: $('login-password').value
      })
    });
    setToken(data.token);
    location.replace('admin.html');
  } catch (e) {
    setMsg('login-msg', '账号或密码错误');
  }
}

$('btn-login').addEventListener('click', submitLogin);
$('login-password').addEventListener('keydown', e => { if (e.key === 'Enter') submitLogin(); });
$('login-username').addEventListener('keydown', e => { if (e.key === 'Enter') submitLogin(); });

(async function init() {
  try {
    const status = await api('/api/setup/status');
    if (status.needsSetup) { location.replace('setup.html'); return; }
    if (state.token) { location.replace('admin.html'); return; }
  } catch (e) {
    // 服务端异常时停留在登录页，由用户手动重试
  }
})();
